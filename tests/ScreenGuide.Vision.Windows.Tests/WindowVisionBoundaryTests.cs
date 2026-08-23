using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;
using ScreenGuide.Vision.Windows;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class WindowVisionBoundaryTests
{
    [Fact]
    public void DisposingFrameClearsOwnedPixels()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var frame = new CapturedWindowFrame(bytes, 1, 1, "mock");
        frame.Dispose();
        Assert.All(bytes, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => _ = frame.PngBytes);
    }

    [Fact]
    public async Task NoConsentMeansNoCapture()
    {
        var capture = new RecordingCaptureService();
        var service = CreateService(capture, new RecordingVisionProvider());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.DescribeAsync(Snapshot(), explicitConsent: false));
        Assert.Equal(0, capture.Calls);
    }

    [Fact]
    public async Task ConsentCapturesOnlyBoundWindowAndClearsFrame()
    {
        var ownedBytes = new byte[] { 9, 8, 7 };
        var capture = new RecordingCaptureService(ownedBytes);
        var vision = new RecordingVisionProvider();
        var result = await CreateService(capture, vision).DescribeAsync(Snapshot(), true);
        Assert.Equal(41, capture.Target?.WindowHandle);
        Assert.Equal(41, vision.Target?.WindowHandle);
        Assert.Equal("识别完成", result.UserSummary);
        Assert.All(ownedBytes, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CancellationStopsAnalysisAndClearsFrame()
    {
        var ownedBytes = new byte[] { 6, 5, 4 };
        var vision = new BlockingVisionProvider();
        using var cancellation = new CancellationTokenSource();
        var operation = CreateService(new RecordingCaptureService(ownedBytes), vision)
            .DescribeAsync(Snapshot(), true, cancellation.Token);
        await vision.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.All(ownedBytes, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task SensitiveWindowNeverReachesBackend()
    {
        var backend = new RecordingBackend();
        var service = new WindowsSingleWindowCaptureService(
            backend, new FixedSensitivePolicy("包含密码输入框"));
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CaptureAsync(Target()));
        Assert.Contains("密码", error.Message);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task LocalProviderStatesTextBasedLimitation()
    {
        var provider = new WindowsLocalWindowVisionProvider(new FixedOcr("设置 系统 显示 蓝牙"));
        await using var frame = new CapturedWindowFrame([1], 100, 80, "mock");
        var result = await provider.AnalyzeAsync(new WindowVisionRequest(Target(), frame));
        Assert.Contains("设置页面", result.UserSummary);
        Assert.Contains("设置 系统 显示 蓝牙", result.UserSummary);
        Assert.Contains(result.Limitations, value => value.Contains("纯图片", StringComparison.Ordinal));
        Assert.False(provider.SendsImageOffDevice);
    }

    private static WindowUnderstandingService CreateService(
        IWindowCaptureService capture, IWindowVisionProvider vision) =>
        new(capture, vision, new FixedAutomation(), NullLogger<WindowUnderstandingService>.Instance);

    private static ForegroundWindowSnapshot Snapshot() =>
        new(41, "系统设置", "SystemSettings", DateTimeOffset.UtcNow);

    private static WindowCaptureTarget Target() =>
        new(41, "系统设置", "SystemSettings", DateTimeOffset.UtcNow);

    private sealed class FixedAutomation : IReliableDesktopAutomation
    {
        public DesktopAutomationResult Search(long windowHandle, string query) => throw new NotSupportedException();
        public DesktopAutomationResult Describe(long windowHandle) => new(true, "按钮“系统”");
    }

    private sealed class RecordingCaptureService(byte[]? bytes = null) : IWindowCaptureService
    {
        public int Calls { get; private set; }
        public WindowCaptureTarget? Target { get; private set; }
        public Task<CapturedWindowFrame> CaptureAsync(WindowCaptureTarget target, CancellationToken cancellationToken = default)
        {
            Calls++;
            Target = target;
            return Task.FromResult(new CapturedWindowFrame(bytes ?? [1], 100, 80, "mock"));
        }
    }

    private sealed class RecordingVisionProvider : IWindowVisionProvider
    {
        public string ProviderId => "mock";
        public bool SendsImageOffDevice => false;
        public WindowCaptureTarget? Target { get; private set; }
        public Task<WindowVisionResult> AnalyzeAsync(WindowVisionRequest request, CancellationToken cancellationToken = default)
        {
            Target = request.Target;
            return Task.FromResult(new WindowVisionResult("识别完成", "mock", ["mock"], ["mock limitation"]));
        }
    }

    private sealed class BlockingVisionProvider : IWindowVisionProvider
    {
        public string ProviderId => "blocking-mock";
        public bool SendsImageOffDevice => false;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WindowVisionResult> AnalyzeAsync(WindowVisionRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class FixedSensitivePolicy(string reason) : ISensitiveWindowPolicy
    {
        public SensitiveWindowAssessment Assess(WindowCaptureTarget target) => new(true, reason);
    }

    private sealed class RecordingBackend : IExactWindowCaptureBackend
    {
        public int Calls { get; private set; }
        public Task<RawWindowFrame> CaptureAsync(WindowCaptureTarget target, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new RawWindowFrame([1], 1, 1, "mock"));
        }
    }

    private sealed class FixedOcr(string text) : ILocalOcrTextExtractor
    {
        public Task<string> ExtractAsync(ReadOnlyMemory<byte> pngBytes, CancellationToken cancellationToken = default) =>
            Task.FromResult(text);
    }
}
