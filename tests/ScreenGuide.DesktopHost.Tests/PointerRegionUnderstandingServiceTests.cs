using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class PointerRegionUnderstandingServiceTests
{
    [Fact]
    public async Task AnchorAgeMustRemainStrictlyLessThanTenSeconds()
    {
        var startedAt = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var allowedTime = new FixedTimeProvider(startedAt);
        var allowedCapture = new FakeRegionCapture();
        var allowedOcr = new FixedOcr("allowed");
        using var allowedService = new PointerRegionUnderstandingService(
            new FakeProbe(Snapshot()),
            allowedCapture,
            allowedOcr,
            allowedTime);
        var allowedAnchor = allowedService.Prepare(true, "VisibleConfirmation");
        allowedTime.UtcNow = startedAt.AddSeconds(10).AddTicks(-1);

        _ = await allowedService.ReadAsync(allowedAnchor.AnchorId);

        Assert.Equal(1, allowedCapture.Count);
        Assert.Equal(1, allowedOcr.Count);

        var expiredTime = new FixedTimeProvider(startedAt);
        var expiredCapture = new FakeRegionCapture();
        var expiredOcr = new FixedOcr("must not run");
        using var expiredService = new PointerRegionUnderstandingService(
            new FakeProbe(Snapshot()),
            expiredCapture,
            expiredOcr,
            expiredTime);
        var expiredAnchor = expiredService.Prepare(true, "VisibleConfirmation");
        expiredTime.UtcNow = startedAt.AddSeconds(10);

        var error = await Assert.ThrowsAsync<PointerRegionException>(() =>
            expiredService.ReadAsync(expiredAnchor.AnchorId));

        Assert.Equal(PointerRegionErrorCodes.AnchorStale, error.Code);
        Assert.Equal(0, expiredCapture.Count);
        Assert.Equal(0, expiredOcr.Count);
    }

    [Fact]
    public async Task ConsentIsExplicitOneUseAndExactTargetBound()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var probe = new FakeProbe(Snapshot());
        var capture = new FakeRegionCapture();
        var service = new PointerRegionUnderstandingService(probe, capture, new FixedOcr("按钮 设置"), time);

        Assert.Equal(PointerRegionErrorCodes.ConsentRequired, Assert.Throws<PointerRegionException>(() =>
            service.Prepare(confirmed: false, "VisibleConfirmation")).Code);
        Assert.Equal(PointerRegionErrorCodes.ConsentRequired, Assert.Throws<PointerRegionException>(() =>
            service.Prepare(confirmed: true, "BackgroundOrImplicit")).Code);
        Assert.Equal(PointerRegionErrorCodes.ConsentRequired, Assert.Throws<PointerRegionException>(() =>
            service.Prepare(confirmed: true, " VisibleConfirmation ")).Code);

        var anchor = service.Prepare(confirmed: true, "VisibleConfirmation");
        var result = await service.ReadAsync(anchor.AnchorId);

        Assert.Equal("按钮 设置", result.Text);
        Assert.Equal(1, capture.Count);
        Assert.True(capture.LastRegion!.IsDisposed);
        Assert.Equal(PointerRegionErrorCodes.AnchorUnavailable, (await Assert.ThrowsAsync<PointerRegionException>(() =>
            service.ReadAsync(anchor.AnchorId))).Code);

        var changed = service.Prepare(true, "VisibleConfirmation");
        probe.Current = Snapshot(bounds: new PixelBounds(101, 100, 800, 600));
        Assert.Equal(PointerRegionErrorCodes.TargetChanged, (await Assert.ThrowsAsync<PointerRegionException>(() =>
            service.ReadAsync(changed.AnchorId))).Code);
        Assert.Equal(1, capture.Count);
    }

    [Fact]
    public async Task CancellationDisposesRegionAndRejectsLateOcr()
    {
        var probe = new FakeProbe(Snapshot());
        var capture = new FakeRegionCapture();
        var ocr = new BlockingOcr();
        var service = new PointerRegionUnderstandingService(probe, capture, ocr, TimeProvider.System);
        var anchor = service.Prepare(true, "VisibleConfirmation");

        var read = service.ReadAsync(anchor.AnchorId);
        await ocr.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(service.Cancel(anchor.AnchorId));
        ocr.Release.TrySetResult("late private text");

        Assert.Equal(PointerRegionErrorCodes.Cancelled, (await Assert.ThrowsAsync<PointerRegionException>(() => read)).Code);
        Assert.True(capture.LastRegion!.IsDisposed);
    }

    [Fact]
    public async Task HostStopDisposesRegionAndRejectsLateOcr()
    {
        var capture = new FakeRegionCapture();
        var ocr = new BlockingOcr();
        var service = new PointerRegionUnderstandingService(
            new FakeProbe(Snapshot()),
            capture,
            ocr,
            TimeProvider.System);
        var anchor = service.Prepare(true, "VisibleConfirmation");
        var read = service.ReadAsync(anchor.AnchorId);
        await ocr.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        service.Dispose();
        ocr.Release.TrySetResult("late private text");

        Assert.Equal(PointerRegionErrorCodes.Cancelled, (await Assert.ThrowsAsync<PointerRegionException>(() => read)).Code);
        Assert.True(capture.LastRegion!.IsDisposed);
    }

    [Fact]
    public async Task OcrFailureIsStableAndDisposesRegionWithoutEchoingContent()
    {
        var capture = new FakeRegionCapture();
        var service = new PointerRegionUnderstandingService(
            new FakeProbe(Snapshot()),
            capture,
            new ThrowingOcr("private OCR body"),
            TimeProvider.System);
        var anchor = service.Prepare(true, "VisibleConfirmation");

        var error = await Assert.ThrowsAsync<PointerRegionException>(() =>
            service.ReadAsync(anchor.AnchorId));

        Assert.Equal(PointerRegionErrorCodes.OcrFailed, error.Code);
        Assert.DoesNotContain("private", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(capture.LastRegion!.IsDisposed);
    }

    [Fact]
    public async Task ChangedProcessIdentityFailsBeforeCapture()
    {
        var probe = new FakeProbe(Snapshot());
        var capture = new FakeRegionCapture();
        var service = new PointerRegionUnderstandingService(
            probe,
            capture,
            new FixedOcr("unused"),
            TimeProvider.System);
        var anchor = service.Prepare(true, "VisibleConfirmation");
        probe.Current = Snapshot() with
        {
            Window = Snapshot().Window with
            {
                Target = Snapshot().Window.Target with { ProcessId = 8 }
            }
        };

        var error = await Assert.ThrowsAsync<PointerRegionException>(() =>
            service.ReadAsync(anchor.AnchorId));

        Assert.Equal(PointerRegionErrorCodes.TargetChanged, error.Code);
        Assert.Equal(0, capture.Count);
    }

    private static PointerDesktopSnapshot Snapshot(PixelBounds? bounds = null) => new(
        new PointerWindowIdentity(
            new WindowCaptureTarget(
                42,
                "目标窗口",
                "fake",
                7,
                new DateTimeOffset(2026, 9, 1, 7, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 1, 7, 59, 0, TimeSpan.Zero)),
            bounds ?? new PixelBounds(100, 100, 800, 600),
            96,
            96),
        42,
        500,
        400,
        42,
        7,
        false,
        new PixelBounds(450, 350, 100, 80));

    private sealed class FakeProbe(PointerDesktopSnapshot current) : IPointerDesktopProbe
    {
        public PointerDesktopSnapshot Current { get; set; } = current;

        public PointerDesktopSnapshot CaptureCurrent() => Current;

        public PointerDesktopSnapshot ObserveAt(int screenX, int screenY) => Current with
        {
            ScreenX = screenX,
            ScreenY = screenY
        };
    }

    private sealed class FakeRegionCapture : IPointerRegionCaptureService
    {
        public int Count { get; private set; }

        public CapturedPointerRegion? LastRegion { get; private set; }

        public Task<CapturedPointerRegion> CaptureAsync(
            WindowCaptureTarget target,
            PixelBounds windowBounds,
            PixelBounds regionBounds,
            CancellationToken cancellationToken = default)
        {
            Count++;
            LastRegion = new CapturedPointerRegion([1, 2, 3], 10, 10, "fake");
            return Task.FromResult(LastRegion);
        }
    }

    private sealed class FixedOcr(string text) : ILocalOcrTextExtractor
    {
        public int Count { get; private set; }

        public Task<string> ExtractAsync(ReadOnlyMemory<byte> pngBytes, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.FromResult(text);
        }
    }

    private sealed class BlockingOcr : ILocalOcrTextExtractor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> ExtractAsync(
            ReadOnlyMemory<byte> pngBytes,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return await Release.Task;
        }
    }

    private sealed class ThrowingOcr(string privateText) : ILocalOcrTextExtractor
    {
        public Task<string> ExtractAsync(
            ReadOnlyMemory<byte> pngBytes,
            CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new InvalidOperationException(privateText));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
