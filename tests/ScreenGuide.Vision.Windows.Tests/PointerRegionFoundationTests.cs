using System.Drawing;
using System.Drawing.Imaging;
using ScreenGuide.Vision.Abstractions;
using ScreenGuide.Vision.Windows;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class PointerRegionFoundationTests
{
    [Fact]
    public void AnchorPolicyAcceptsSameProcessChildAndRejectsOtherProcessOverlay()
    {
        var now = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var valid = Snapshot(pointWindowHandle: 43, pointProcessId: 7, sameProcessChild: true);

        var anchor = PointerAnchorPolicy.Create(valid, Guid.Parse("11111111-1111-1111-1111-111111111111"), now);

        Assert.Equal(0.5, anchor.NormalizedX, 6);
        Assert.Equal(0.5, anchor.NormalizedY, 6);
        PointerAnchorPolicy.RequireCurrent(anchor, valid, now.AddSeconds(10));

        var overlay = Snapshot(pointWindowHandle: 99, pointProcessId: 8, sameProcessChild: false);
        var error = Assert.Throws<PointerRegionException>(() =>
            PointerAnchorPolicy.Create(overlay, Guid.NewGuid(), now));
        Assert.Equal(PointerRegionErrorCodes.PointTargetChanged, error.Code);
    }

    [Fact]
    public void AnchorPolicyFailsClosedForStaleBoundsDpiAndMovedWindow()
    {
        var now = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var anchor = PointerAnchorPolicy.Create(Snapshot(), Guid.NewGuid(), now);

        Assert.Equal(PointerRegionErrorCodes.AnchorStale, Assert.Throws<PointerRegionException>(() =>
            PointerAnchorPolicy.RequireCurrent(anchor, Snapshot(), now.AddSeconds(10).AddTicks(1))).Code);
        Assert.Equal(PointerRegionErrorCodes.TargetChanged, Assert.Throws<PointerRegionException>(() =>
            PointerAnchorPolicy.RequireCurrent(anchor, Snapshot(bounds: new PixelBounds(101, 100, 800, 600)), now)).Code);
        Assert.Equal(PointerRegionErrorCodes.TargetChanged, Assert.Throws<PointerRegionException>(() =>
            PointerAnchorPolicy.RequireCurrent(anchor, Snapshot(dpi: 144), now)).Code);
        var changedProcess = Snapshot() with
        {
            Window = Snapshot().Window with
            {
                Target = Snapshot().Window.Target with
                {
                    ProcessStartTimeUtc = Snapshot().Window.Target.ProcessStartTimeUtc.AddSeconds(1)
                }
            }
        };
        Assert.Equal(PointerRegionErrorCodes.TargetChanged, Assert.Throws<PointerRegionException>(() =>
            PointerAnchorPolicy.RequireCurrent(anchor, changedProcess, now)).Code);
    }

    [Fact]
    public void SelectorPadsTrustworthyElementClipsAndBoundsMaximum()
    {
        var window = new PixelBounds(100, 100, 800, 600);
        var element = new PixelBounds(80, 80, 900, 520);

        var selected = PointerRegionSelector.Select(window, 500, 400, element);

        Assert.Equal(new PixelBounds(180, 144, 640, 480), selected.Bounds);
        Assert.Equal("uia-element", selected.Source);
    }

    [Fact]
    public void SelectorFallsBackToCenteredBoundedRegion()
    {
        var selected = PointerRegionSelector.Select(
            new PixelBounds(100, 100, 500, 300),
            110,
            110,
            trustworthyElementBounds: null);

        Assert.Equal(new PixelBounds(100, 100, 500, 300), selected.Bounds);
        Assert.Equal("pointer-centered", selected.Source);
    }

    [Fact]
    public async Task CropDisposesFullFrameAndRegionDisposalClearsBytes()
    {
        var full = Frame(800, 600);
        var service = new WindowsPointerRegionCaptureService(new FixedCapture(full));
        var target = Snapshot().Window.Target;

        var region = await service.CaptureAsync(
            target,
            new PixelBounds(100, 100, 800, 600),
            new PixelBounds(180, 120, 640, 480));

        Assert.Throws<ObjectDisposedException>(() => _ = full.PngBytes);
        Assert.InRange(region.PixelWidth, 1, 640);
        Assert.InRange(region.PixelHeight, 1, 480);
        region.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = region.PngBytes);
    }

    [Fact]
    public async Task CancelledCropStillDisposesFullFrame()
    {
        var full = Frame(800, 600);
        var service = new WindowsPointerRegionCaptureService(new FixedCapture(full));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CaptureAsync(
            Snapshot().Window.Target,
            new PixelBounds(100, 100, 800, 600),
            new PixelBounds(180, 160, 640, 480),
            cancellation.Token));

        Assert.Throws<ObjectDisposedException>(() => _ = full.PngBytes);
    }

    private static PointerDesktopSnapshot Snapshot(
        PixelBounds? bounds = null,
        uint dpi = 96,
        long pointWindowHandle = 42,
        int pointProcessId = 7,
        bool sameProcessChild = false) => new(
        new PointerWindowIdentity(
            new WindowCaptureTarget(
                42,
                "目标窗口",
                "fake",
                7,
                new DateTimeOffset(2026, 9, 1, 7, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 1, 7, 59, 0, TimeSpan.Zero)),
            bounds ?? new PixelBounds(100, 100, 800, 600),
            dpi,
            dpi),
        ForegroundWindowHandle: 42,
        ScreenX: 500,
        ScreenY: 400,
        WindowUnderPointHandle: pointWindowHandle,
        WindowUnderPointProcessId: pointProcessId,
        WindowUnderPointIsChild: sameProcessChild,
        TrustworthyElementBounds: null);

    private static CapturedWindowFrame Frame(int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new CapturedWindowFrame(stream.ToArray(), width, height, "fake");
    }

    private sealed class FixedCapture(CapturedWindowFrame frame) : IWindowCaptureService
    {
        public Task<CapturedWindowFrame> CaptureAsync(
            WindowCaptureTarget target,
            CancellationToken cancellationToken = default) => Task.FromResult(frame);
    }
}
