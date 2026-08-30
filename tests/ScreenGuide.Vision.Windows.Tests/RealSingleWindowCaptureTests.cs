using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ScreenGuide.Stage4.RealUsageRunner;
using ScreenGuide.Vision.Abstractions;
using ScreenGuide.Vision.Windows;
using WinForms = System.Windows.Forms;

namespace ScreenGuide.Vision.Windows.Tests;

[CollectionDefinition("Real single-window capture serial", DisableParallelization = true)]
public sealed class RealSingleWindowCaptureCollection
{
    public const string Name = "Real single-window capture serial";
}

[Collection(RealSingleWindowCaptureCollection.Name)]
public sealed class RealSingleWindowCaptureTests
{
    private const int WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const int SwMinimize = 6;

    [Fact]
    public async Task CapturesAndRecognizesExactStage4RunnerWindowWithoutWritingImageToDisk()
    {
        const string title = VisionEvaluationContract.FormTitle;
        const string canary = VisionEvaluationContract.FormalCanary;
        var ready = new TaskCompletionSource<(WinForms.Form Form, long Handle)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var form = new SyntheticEvaluationForm(
                title,
                canary,
                VisionEvaluationContract.FormalWindowWidth,
                VisionEvaluationContract.FormalWindowHeight,
                highContrast: true);
            form.Shown += (_, _) =>
            {
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

            Assert.True(frame.PixelWidth >= VisionEvaluationContract.FormalWindowWidth * 0.8);
            Assert.True(frame.PixelHeight >= VisionEvaluationContract.FormalWindowHeight * 0.8);
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
            Assert.Equal(
                "Windows.PrintWindow.SingleHwnd",
                result.Diagnostic.FrameShape.CaptureTechnology);
            Assert.True(result.Diagnostic.FrameShape.SampledPixelCount > 0);
            Assert.True(result.Diagnostic.FrameShape.DarkPixelPermille > 0);
            Assert.True(result.Diagnostic.FrameShape.BrightPixelPermille > 0);
            Assert.True(result.Diagnostic.FrameShape.OcrTextDetected);
            Assert.True(result.AttemptResult.CleanupConfirmed);
        }
        finally
        {
            form.BeginInvoke(form.Close);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Stage4RunnerPreventsUserMinimizeAndKeepsConsoleForegroundDuringCapture()
    {
        await using var window = await VisionEvaluationRunner.SyntheticEvaluationWindow.StartAsync(
            VisionEvaluationContract.FormTitle,
            VisionEvaluationContract.DiagnosticWindowText,
            VisionEvaluationContract.DiagnosticWindowWidth,
            VisionEvaluationContract.DiagnosticWindowHeight,
            highContrast: true,
            CancellationToken.None);
        var handle = new IntPtr(window.Handle);
        var foregroundBefore = GetForegroundWindow();
        _ = SendMessage(handle, WmSysCommand, new IntPtr(ScMinimize), IntPtr.Zero);
        Assert.False(IsIconic(handle));

        await window.PrepareForCaptureAsync(
            VisionEvaluationContract.DiagnosticWindowWidth,
            VisionEvaluationContract.DiagnosticWindowHeight,
            CancellationToken.None);
        var foregroundAfterPrepare = GetForegroundWindow();
        Assert.True(
            foregroundAfterPrepare == foregroundBefore,
            $"Foreground changed during prepare: before={foregroundBefore}, after={foregroundAfterPrepare}, target={handle}.");

        using var process = Process.GetCurrentProcess();
        var target = new WindowCaptureTarget(
            window.Handle,
            VisionEvaluationContract.FormTitle,
            process.ProcessName,
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime()),
            DateTimeOffset.UtcNow);
        var verifier = new WindowsWindowCaptureTargetVerifier();
        var raw = await new PrintWindowCaptureBackend(verifier)
            .CaptureAsync(target, CancellationToken.None);
        await using var frame = new CapturedWindowFrame(
            raw.PngBytes,
            raw.PixelWidth,
            raw.PixelHeight,
            raw.Technology);

        Assert.True(frame.PixelWidth >= VisionEvaluationContract.DiagnosticWindowWidth * 0.8);
        Assert.True(frame.PixelHeight >= VisionEvaluationContract.DiagnosticWindowHeight * 0.8);
        Assert.Equal(window.Handle, handle.ToInt64());
        var foregroundAfter = GetForegroundWindow();
        Assert.True(
            foregroundAfter == foregroundBefore,
            $"Foreground changed: before={foregroundBefore}, after={foregroundAfter}, target={handle}.");
    }

    [Fact]
    public async Task Stage4RunnerFailsClosedIfSyntheticWindowWasExternallyMinimized()
    {
        await using var window = await VisionEvaluationRunner.SyntheticEvaluationWindow.StartAsync(
            VisionEvaluationContract.FormTitle,
            VisionEvaluationContract.DiagnosticWindowText,
            VisionEvaluationContract.DiagnosticWindowWidth,
            VisionEvaluationContract.DiagnosticWindowHeight,
            highContrast: true,
            CancellationToken.None);
        var handle = new IntPtr(window.Handle);
        _ = ShowWindow(handle, SwMinimize);
        Assert.True(SpinWait.SpinUntil(() => IsIconic(handle), TimeSpan.FromSeconds(2)));
        var foregroundBefore = GetForegroundWindow();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            window.PrepareForCaptureAsync(
                VisionEvaluationContract.DiagnosticWindowWidth,
                VisionEvaluationContract.DiagnosticWindowHeight,
                CancellationToken.None));

        Assert.True(IsIconic(handle));
        Assert.Equal(foregroundBefore, GetForegroundWindow());
    }

    [Fact]
    public async Task StopDuringLateWindowPreparationPreventsCapture()
    {
        var input = new TestLineInput();
        var preparationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationCancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCount = 0;
        var controlled = ManualAttemptControl.RunAsync(
            input,
            token => VisionEvaluationAttemptPipeline.PrepareAndCaptureAsync(
                async prepareToken =>
                {
                    using var registration = prepareToken.Register(
                        () => preparationCancelled.TrySetResult(true));
                    preparationStarted.TrySetResult(true);
                    await releasePreparation.Task.ConfigureAwait(false);
                },
                _ =>
                {
                    Interlocked.Increment(ref captureCount);
                    return Task.FromResult(true);
                },
                token),
            CancellationToken.None);

        await preparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await input.WriteAsync("STOP");
        await preparationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releasePreparation.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await controlled);
        Assert.Equal(0, Volatile.Read(ref captureCount));
    }

    private sealed class ForcedInternalTimeoutBackend : IExactWindowCaptureBackend
    {
        public Task<RawWindowFrame> CaptureAsync(
            WindowCaptureTarget target,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException("simulated WGC first-frame timeout");
    }

    private sealed class TestLineInput : IManualLineInput
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await _lines.Reader.ReadAsync(cancellationToken);

        public ValueTask WriteAsync(string value) => _lines.Writer.WriteAsync(value);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr hWnd,
        int message,
        IntPtr wParam,
        IntPtr lParam);
}
