using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class PointerRegionIpcIntegrationTests
{
    [Fact]
    public async Task ProtocolV12ExposesOneUseLocalRegionWithoutChangingSchema()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var probe = new FakeProbe(Snapshot());
        var capture = new FakeCapture();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IPointerDesktopProbe>(probe);
            services.AddSingleton<IPointerRegionCaptureService>(capture);
            services.AddSingleton<ILocalOcrTextExtractor>(new FixedOcr("本机 私密 OCR"));
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var status = await client.GetSystemStatusAsync();
        var anchor = await client.PreparePointerRegionAsync(
            new PreparePointerRegionRequestDto(true));
        var result = await client.ReadPointerRegionAsync(anchor.AnchorId);

        Assert.Equal(12, status.ProtocolVersion);
        Assert.Equal(11, status.DatabaseSchemaVersion);
        Assert.Equal(V02Contract.SchemaVersion, status.DatabaseSchemaVersion);
        Assert.Equal("本机 私密 OCR", result.Text);
        Assert.DoesNotContain("私密", result.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, capture.Count);
        var duplicate = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.ReadPointerRegionAsync(anchor.AnchorId));
        Assert.Equal(PointerRegionErrorCodes.AnchorUnavailable, duplicate.Error.Code);
        await host.StopAsync();
    }

    [Fact]
    public async Task MissingVisibleConsentFailsBeforeCapture()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var capture = new FakeCapture();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IPointerDesktopProbe>(new FakeProbe(Snapshot()));
            services.AddSingleton<IPointerRegionCaptureService>(capture);
            services.AddSingleton<ILocalOcrTextExtractor>(new FixedOcr("unused"));
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var error = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.PreparePointerRegionAsync(new PreparePointerRegionRequestDto(false)));

        Assert.Equal(PointerRegionErrorCodes.ConsentRequired, error.Error.Code);
        Assert.Equal(0, capture.Count);
        await host.StopAsync();
    }

    [Fact]
    public async Task CancelEndpointClearsRegionAndRejectsLateOcrResult()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var capture = new FakeCapture();
        var ocr = new BlockingOcr();
        using var host = environment.BuildHost(services =>
        {
            services.AddSingleton<IPointerDesktopProbe>(new FakeProbe(Snapshot()));
            services.AddSingleton<IPointerRegionCaptureService>(capture);
            services.AddSingleton<ILocalOcrTextExtractor>(ocr);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var anchor = await client.PreparePointerRegionAsync(new PreparePointerRegionRequestDto(true));
        var reading = client.ReadPointerRegionAsync(anchor.AnchorId);
        await ocr.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(await client.CancelPointerRegionAsync(anchor.AnchorId));
        ocr.Release.TrySetResult("late private OCR");
        var error = await Assert.ThrowsAsync<DesktopApiException>(() => reading);

        Assert.Equal(PointerRegionErrorCodes.Cancelled, error.Error.Code);
        Assert.True(capture.LastRegion!.IsDisposed);
        await host.StopAsync();
    }

    private static PointerDesktopSnapshot Snapshot() => new(
        new PointerWindowIdentity(
            new WindowCaptureTarget(
                42,
                "目标窗口",
                "fake",
                7,
                new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 17, 9, 59, 0, TimeSpan.Zero)),
            new PixelBounds(100, 100, 800, 600),
            96,
            96),
        42,
        500,
        400,
        42,
        7,
        false,
        new PixelBounds(450, 350, 100, 80));

    private sealed class FakeProbe(PointerDesktopSnapshot snapshot) : IPointerDesktopProbe
    {
        public PointerDesktopSnapshot CaptureCurrent() => snapshot;

        public PointerDesktopSnapshot ObserveAt(int screenX, int screenY) => snapshot with
        {
            ScreenX = screenX,
            ScreenY = screenY
        };
    }

    private sealed class FakeCapture : IPointerRegionCaptureService
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
            LastRegion = new CapturedPointerRegion([1], 1, 1, "fake");
            return Task.FromResult(LastRegion);
        }
    }

    private sealed class FixedOcr(string text) : ILocalOcrTextExtractor
    {
        public Task<string> ExtractAsync(ReadOnlyMemory<byte> pngBytes, CancellationToken cancellationToken = default) =>
            Task.FromResult(text);
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
}
