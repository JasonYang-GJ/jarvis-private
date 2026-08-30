using System.Diagnostics;
using ScreenGuide.Stage4.RealUsageRunner;
using ScreenGuide.Vision.Abstractions;
using ScreenGuide.Vision.Windows;
using WinForms = System.Windows.Forms;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class RealSingleWindowCaptureTests
{
    [Fact]
    public async Task CapturesAndRecognizesExactStage4RunnerWindowWithoutWritingImageToDisk()
    {
        const string title = "元枢本机单窗口评测";
        const string canary = "这是无个人数据的本机单窗口评测标记";
        var ready = new TaskCompletionSource<(WinForms.Form Form, long Handle)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var form = new WinForms.Form
            {
                Text = title,
                Width = 680,
                Height = 280,
                StartPosition = WinForms.FormStartPosition.CenterScreen,
                TopMost = true
            };
            form.Controls.Add(new WinForms.Label
            {
                Text = canary,
                AutoSize = true,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 18),
                Left = 46,
                Top = 86
            });
            form.Shown += (_, _) => ready.TrySetResult((form, form.Handle.ToInt64()));
            WinForms.Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var (form, handle) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var process = Process.GetCurrentProcess();
            var target = new WindowCaptureTarget(
                handle,
                title,
                process.ProcessName,
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime()),
                DateTimeOffset.UtcNow);
            var backend = new WindowsGraphicsCaptureBackend(
                new WindowsWindowCaptureTargetVerifier());
            var raw = await backend.CaptureAsync(target, CancellationToken.None);
            await using var frame = new CapturedWindowFrame(
                raw.PngBytes, raw.PixelWidth, raw.PixelHeight, raw.Technology);

            Assert.True(frame.PixelWidth >= 600);
            Assert.True(frame.PixelHeight >= 200);
            Assert.Equal("Windows.GraphicsCapture.SingleHwnd", frame.CaptureTechnology);
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, frame.PngBytes.Span[..4].ToArray());

            var provider = new WindowsLocalWindowVisionProvider(new WindowsLocalOcrTextExtractor());
            var result = await provider.AnalyzeAsync(new WindowVisionRequest(target, frame));
            Assert.True(VisionCanaryMatcher.Contains(result.UserSummary, canary));
            Assert.False(provider.SendsImageOffDevice);
        }
        finally
        {
            form.BeginInvoke(form.Close);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task CapturesAndAnalyzesExactStage4DiagnosticWindowThroughProductFallback()
    {
        var ready = new TaskCompletionSource<(WinForms.Form Form, long Handle)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var form = new SyntheticEvaluationForm(
                VisionEvaluationContract.FormTitle,
                VisionEvaluationContract.DiagnosticWindowText,
                VisionEvaluationContract.DiagnosticWindowWidth,
                VisionEvaluationContract.DiagnosticWindowHeight,
                highContrast: true);
            form.Shown += (_, _) =>
            {
                form.Activate();
                form.Refresh();
                ready.TrySetResult((form, form.Handle.ToInt64()));
            };
            WinForms.Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var (form, handle) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Empty(form.Controls);
            using var process = Process.GetCurrentProcess();
            var target = new WindowCaptureTarget(
                handle,
                VisionEvaluationContract.FormTitle,
                process.ProcessName,
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime()),
                DateTimeOffset.UtcNow);
            var verifier = new WindowsWindowCaptureTargetVerifier();
            var capture = new WindowsSingleWindowCaptureService(
                new ResilientExactWindowCaptureBackend(
                    new ForcedInternalTimeoutBackend(),
                    new PrintWindowCaptureBackend(verifier),
                    verifier),
                new WindowsSensitiveWindowPolicy(),
                verifier);
            var evaluator = new VisionDiagnosticEvaluator(
                capture,
                new WindowsLocalWindowVisionProvider(new WindowsLocalOcrTextExtractor()));

            var result = await evaluator.EvaluateAsync(
                target,
                VisionEvaluationContract.DiagnosticCandidates,
                CancellationToken.None);

            Assert.Equal(EvaluationTerminalState.Success, result.AttemptResult.Attempt.State);
            Assert.Equal(1, result.Diagnostic.SampleCount);
            Assert.Equal(
                VisionEvaluationContract.DiagnosticCandidates.Count,
                result.Diagnostic.Candidates.Count);
            Assert.Contains(result.Diagnostic.Candidates, item => item.BestEditDistance == 0);
            Assert.True(result.AttemptResult.CleanupConfirmed);
        }
        finally
        {
            form.BeginInvoke(form.Close);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class ForcedInternalTimeoutBackend : IExactWindowCaptureBackend
    {
        public Task<RawWindowFrame> CaptureAsync(
            WindowCaptureTarget target,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException("simulated WGC first-frame timeout");
    }
}
