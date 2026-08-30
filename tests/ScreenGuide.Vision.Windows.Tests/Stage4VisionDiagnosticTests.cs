using ScreenGuide.Stage4.RealUsageRunner;
using ScreenGuide.Vision.Abstractions;
using ScreenGuide.Vision.Windows;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class Stage4VisionDiagnosticTests
{
    [Fact]
    public void DiagnosticContractUsesFourShortHighContrastCandidatesOutsideTheTrustedTitle()
    {
        Assert.Collection(
            VisionEvaluationContract.DiagnosticCandidates,
            item => Assert.Equal(("common-cn", "今天学习中文"), (item.Id, item.Text)),
            item => Assert.Equal(("digits-token", "86428642"), (item.Id, item.Text)),
            item => Assert.Equal(("latin-token", "VISION4827"), (item.Id, item.Text)),
            item => Assert.Equal(("mixed-token", "元枢4827"), (item.Id, item.Text)));
        Assert.All(
            VisionEvaluationContract.DiagnosticCandidates,
            item => Assert.DoesNotContain(
                item.Text,
                VisionEvaluationContract.FormTitle,
                StringComparison.Ordinal));
        Assert.True(VisionEvaluationContract.DiagnosticWindowWidth >= 1100);
        Assert.True(VisionEvaluationContract.DiagnosticWindowHeight >= 700);
        Assert.True(VisionEvaluationContract.DiagnosticFontSize >= 54);
        Assert.DoesNotContain("1.", VisionEvaluationContract.DiagnosticWindowText, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzerKeepsOnlyCandidateIdsLengthsAndEditDistances()
    {
        const string analyzedSummary = "窗口摘要：今 天 我 们 一 起 学 习 中 文；Y U A N S H U 4 8 2 7";
        var summary = VisionDiagnosticAnalyzer.Analyze(
            analyzedSummary,
            [
                new VisionDiagnosticCandidate("common-cn", "今天我们一起学习中文"),
                new VisionDiagnosticCandidate("ascii-token", "YUANSHU4827"),
                new VisionDiagnosticCandidate("wrong-token", "YUANSHU4828")
            ]);

        Assert.Equal(1, summary.SampleCount);
        Assert.True(summary.CompactTextLength > 0);
        Assert.Equal(0, summary.Candidates.Single(item => item.CandidateId == "common-cn").BestEditDistance);
        Assert.Equal(0, summary.Candidates.Single(item => item.CandidateId == "ascii-token").BestEditDistance);
        Assert.Equal(1, summary.Candidates.Single(item => item.CandidateId == "wrong-token").BestEditDistance);
    }

    [Fact]
    public void FrameShapeAnalyzerDistinguishesBlankAndRenderedFramesWithoutReturningPixels()
    {
        using var blank = Frame(graphics => graphics.Clear(Color.White));
        using var rendered = Frame(graphics =>
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Black, 16, 16, 96, 64);
        });

        var blankShape = VisionFrameShapeAnalyzer.Analyze(blank);
        var renderedShape = VisionFrameShapeAnalyzer.Analyze(rendered);

        Assert.Equal("Windows.PrintWindow.SingleHwnd", blankShape.CaptureTechnology);
        Assert.Equal(128, blankShape.PixelWidth);
        Assert.Equal(96, blankShape.PixelHeight);
        Assert.Equal(0, blankShape.DarkPixelPermille);
        Assert.Equal(1000, blankShape.BrightPixelPermille);
        Assert.Equal(1000, blankShape.OpaquePixelPermille);
        Assert.Equal(0, blankShape.LuminanceRange);
        Assert.True(renderedShape.DarkPixelPermille > 0);
        Assert.True(renderedShape.BrightPixelPermille > 0);
        Assert.Equal(255, renderedShape.LuminanceRange);
    }

    [Fact]
    public void SafeReportDoesNotContainCandidateOrOcrText()
    {
        const string candidateText = "今天我们一起学习中文";
        const string ocrText = "私密OCR正文";
        var diagnostic = VisionDiagnosticAnalyzer.Analyze(
            ocrText,
            [
                new VisionDiagnosticCandidate("common-cn", candidateText),
                new VisionDiagnosticCandidate("private-token-abc123", "another candidate")
            ],
            new VisionFrameShapeSummary(
                "Windows.PrintWindow.SingleHwnd",
                1120,
                720,
                4096,
                25,
                970,
                1000,
                255,
                OcrTextDetected: false));
        var report = EvaluationReport.Create(
            "vision-diagnostic",
            "941c2d8635939bd1329daa81f34b6829bd447750",
            "completed",
            EvaluationAggregator.Build(
            [
                new EvaluationAttempt(
                    false,
                    EvaluationTerminalState.Success,
                    TimeSpan.FromMilliseconds(10))
            ]),
            new EvaluationEnvironment("10.0.26100", "x64", true, "not_applicable"),
            cleanupConfirmed: true,
            visionDiagnostic: diagnostic);

        var json = SafeEvaluationReportWriter.Serialize(report);
        using var document = JsonDocument.Parse(json);

        Assert.DoesNotContain(candidateText, json, StringComparison.Ordinal);
        Assert.DoesNotContain(ocrText, json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token-abc123", json, StringComparison.Ordinal);
        Assert.Contains("common-cn", json, StringComparison.Ordinal);
        Assert.Contains("unknown", json, StringComparison.Ordinal);
        Assert.Equal(
            "s4-r2.usage-evaluation.v3",
            document.RootElement.GetProperty("contractVersion").GetString());
        var frameShape = document.RootElement
            .GetProperty("visionDiagnostic")
            .GetProperty("frameShape");
        Assert.Equal("Windows.PrintWindow.SingleHwnd", frameShape.GetProperty("captureTechnology").GetString());
        Assert.Equal(25, frameShape.GetProperty("darkPixelPermille").GetInt32());
        Assert.False(frameShape.GetProperty("ocrTextDetected").GetBoolean());
    }

    [Fact]
    public async Task DiagnosticEvaluatorClearsFrameAndReturnsOnlySafeShape()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var evaluator = new VisionDiagnosticEvaluator(
            new StubCaptureService(() => new CapturedWindowFrame(bytes, 2, 2, "test")),
            new StubVisionProvider("今 天 我 们 一 起 学 习 中 文"));

        var result = await evaluator.EvaluateAsync(
            Target(),
            [new VisionDiagnosticCandidate("common-cn", "今天我们一起学习中文")],
            CancellationToken.None);

        Assert.Equal(EvaluationTerminalState.Success, result.AttemptResult.Attempt.State);
        Assert.Equal(0, result.Diagnostic.Candidates.Single().BestEditDistance);
        Assert.All(bytes, value => Assert.Equal(0, value));
    }

    [Fact]
    public void RunnerCaptureCompositionUsesTheProductExactWindowFallback()
    {
        var verifier = new RecordingVerifier();
        var backend = Stage4VisionCaptureFactory.CreateBackend(verifier);

        Assert.IsType<ResilientExactWindowCaptureBackend>(backend);
    }

    private static WindowCaptureTarget Target() => new(
        1,
        "test",
        "test",
        1,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    private static CapturedWindowFrame Frame(Action<Graphics> draw)
    {
        using var bitmap = new Bitmap(128, 96, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            draw(graphics);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new CapturedWindowFrame(
            stream.ToArray(),
            bitmap.Width,
            bitmap.Height,
            "Windows.PrintWindow.SingleHwnd");
    }

    private sealed class StubCaptureService(Func<CapturedWindowFrame> factory) : IWindowCaptureService
    {
        public Task<CapturedWindowFrame> CaptureAsync(
            WindowCaptureTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(factory());
    }

    private sealed class StubVisionProvider(string summary) : IWindowVisionProvider
    {
        public string ProviderId => "test";

        public bool SendsImageOffDevice => false;

        public Task<WindowVisionResult> AnalyzeAsync(
            WindowVisionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowVisionResult(summary, "test", [], []));
    }

    private sealed class RecordingVerifier : IWindowCaptureTargetVerifier
    {
        public int Calls { get; private set; }

        public void Verify(WindowCaptureTarget target) => Calls++;
    }

}
