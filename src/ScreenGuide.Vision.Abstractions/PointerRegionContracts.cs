using System.Security.Cryptography;

namespace ScreenGuide.Vision.Abstractions;

public readonly record struct PixelBounds(int Left, int Top, int Width, int Height)
{
    public int Right => checked(Left + Width);

    public int Bottom => checked(Top + Height);

    public bool IsValid => Width > 0 && Height > 0;

    public bool Contains(int x, int y) =>
        IsValid && x >= Left && x < Right && y >= Top && y < Bottom;

    public PixelBounds Intersect(PixelBounds other)
    {
        var left = Math.Max(Left, other.Left);
        var top = Math.Max(Top, other.Top);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top
            ? new PixelBounds(left, top, right - left, bottom - top)
            : default;
    }
}

public sealed record PointerWindowIdentity(
    WindowCaptureTarget Target,
    PixelBounds Bounds,
    uint DpiX,
    uint DpiY);

public sealed record PointerDesktopSnapshot(
    PointerWindowIdentity Window,
    long ForegroundWindowHandle,
    int ScreenX,
    int ScreenY,
    long WindowUnderPointHandle,
    int WindowUnderPointProcessId,
    bool WindowUnderPointIsChild,
    PixelBounds? TrustworthyElementBounds);

public sealed record PointerAnchor(
    Guid AnchorId,
    Guid AppRunId,
    int PhysicalScreenX,
    int PhysicalScreenY,
    double NormalizedX,
    double NormalizedY,
    PointerWindowIdentity Window,
    DateTimeOffset CapturedAtUtc);

public sealed record PointerRegionSelection(PixelBounds Bounds, string Source);

public static class PointerRegionErrorCodes
{
    public const string ConsentRequired = "pointer_capture_consent_required";
    public const string AnchorUnavailable = "pointer_anchor_unavailable";
    public const string AnchorStale = "pointer_anchor_stale";
    public const string TargetChanged = "pointer_target_changed";
    public const string PointTargetChanged = "pointer_window_under_point_changed";
    public const string PointOutsideTarget = "pointer_outside_target";
    public const string CaptureFailed = "pointer_region_capture_failed";
    public const string OcrFailed = "pointer_region_ocr_failed";
    public const string Cancelled = "pointer_region_cancelled";
}

public sealed class PointerRegionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class PointerAnchorPolicy
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(10);

    public static PointerAnchor Create(
        PointerDesktopSnapshot snapshot,
        Guid appRunId,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequirePointBinding(snapshot);
        var bounds = snapshot.Window.Bounds;
        if (!bounds.IsValid || !bounds.Contains(snapshot.ScreenX, snapshot.ScreenY))
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.PointOutsideTarget,
                "指针不在已确认的目标窗口内，请重新确认。 ");
        }

        return new PointerAnchor(
            Guid.NewGuid(),
            appRunId,
            snapshot.ScreenX,
            snapshot.ScreenY,
            (snapshot.ScreenX - bounds.Left) / (double)bounds.Width,
            (snapshot.ScreenY - bounds.Top) / (double)bounds.Height,
            snapshot.Window,
            capturedAtUtc);
    }

    public static void RequireCurrent(
        PointerAnchor anchor,
        PointerDesktopSnapshot current,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(current);
        if (nowUtc < anchor.CapturedAtUtc || nowUtc - anchor.CapturedAtUtc >= MaximumAge)
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.AnchorStale,
                "这次指针位置已经过期，请重新确认。 ");
        }

        RequireFrozenTextTargetCurrent(anchor, current);
    }

    // Identity-only validation for an already captured immutable text snapshot. This
    // never authorizes another pixel read; all capture paths must use RequireCurrent.
    public static void RequireFrozenTextTargetCurrent(PointerAnchor anchor, PointerDesktopSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(current);
        var expected = anchor.Window;
        var actual = current.Window;
        if (current.ForegroundWindowHandle != expected.Target.WindowHandle
            || actual.Target.WindowHandle != expected.Target.WindowHandle
            || actual.Target.ProcessId != expected.Target.ProcessId
            || actual.Target.ProcessStartTimeUtc != expected.Target.ProcessStartTimeUtc
            || !string.Equals(actual.Target.ProcessName, expected.Target.ProcessName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actual.Target.WindowTitle, expected.Target.WindowTitle, StringComparison.Ordinal)
            || actual.Bounds != expected.Bounds
            || actual.DpiX != expected.DpiX
            || actual.DpiY != expected.DpiY)
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.TargetChanged,
                "目标窗口的位置、大小或身份已经变化，请重新确认。 ");
        }

        if (current.ScreenX != anchor.PhysicalScreenX
            || current.ScreenY != anchor.PhysicalScreenY)
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.PointTargetChanged,
                "指针锚点已经变化，请重新确认。 ");
        }

        RequirePointBinding(current);
        if (!actual.Bounds.Contains(anchor.PhysicalScreenX, anchor.PhysicalScreenY))
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.PointOutsideTarget,
                "指针不在已确认的目标窗口内，请重新确认。 ");
        }
    }

    private static void RequirePointBinding(PointerDesktopSnapshot snapshot)
    {
        var target = snapshot.Window.Target;
        var exact = snapshot.WindowUnderPointHandle == target.WindowHandle;
        var trustedChild = snapshot.WindowUnderPointIsChild
            && snapshot.WindowUnderPointProcessId == target.ProcessId;
        if (snapshot.ForegroundWindowHandle != target.WindowHandle || (!exact && !trustedChild))
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.PointTargetChanged,
                "指针下方不是已确认窗口或其可信子窗口，请重新确认。 ");
        }
    }
}

public static class PointerRegionSelector
{
    public const int Padding = 24;
    public const int MaximumWidth = 640;
    public const int MaximumHeight = 480;

    public static PointerRegionSelection Select(
        PixelBounds windowBounds,
        int screenX,
        int screenY,
        PixelBounds? trustworthyElementBounds)
    {
        if (!windowBounds.IsValid || !windowBounds.Contains(screenX, screenY))
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.PointOutsideTarget,
                "指针不在已确认的目标窗口内，请重新确认。 ");
        }

        if (trustworthyElementBounds is { IsValid: true } element
            && element.Contains(screenX, screenY))
        {
            var padded = Expand(element, Padding).Intersect(windowBounds);
            if (padded.IsValid)
            {
                return new PointerRegionSelection(
                    BoundAroundPoint(padded, screenX, screenY, MaximumWidth, MaximumHeight),
                    "uia-element");
            }
        }

        return new PointerRegionSelection(
            BoundAroundPoint(windowBounds, screenX, screenY, MaximumWidth, MaximumHeight),
            "pointer-centered");
    }

    private static PixelBounds Expand(PixelBounds value, int amount) => new(
        checked(value.Left - amount),
        checked(value.Top - amount),
        checked(value.Width + amount * 2),
        checked(value.Height + amount * 2));

    private static PixelBounds BoundAroundPoint(
        PixelBounds allowed,
        int x,
        int y,
        int maximumWidth,
        int maximumHeight)
    {
        var width = Math.Min(allowed.Width, maximumWidth);
        var height = Math.Min(allowed.Height, maximumHeight);
        var left = Math.Clamp(x - width / 2, allowed.Left, allowed.Right - width);
        var top = Math.Clamp(y - height / 2, allowed.Top, allowed.Bottom - height);
        return new PixelBounds(left, top, width, height);
    }
}

public interface IPointerDesktopProbe
{
    PointerDesktopSnapshot CaptureCurrent();

    PointerDesktopSnapshot ObserveAt(int screenX, int screenY);
}

public interface IPointerRegionCaptureService
{
    Task<CapturedPointerRegion> CaptureAsync(
        WindowCaptureTarget target,
        PixelBounds windowBounds,
        PixelBounds regionBounds,
        CancellationToken cancellationToken = default);
}

public sealed class CapturedPointerRegion : IAsyncDisposable, IDisposable
{
    private byte[]? _pngBytes;

    public CapturedPointerRegion(
        byte[] pngBytes,
        int pixelWidth,
        int pixelHeight,
        string captureTechnology)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0 || pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new ArgumentException("指针区域图像无效。", nameof(pngBytes));
        }

        _pngBytes = pngBytes;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        CaptureTechnology = captureTechnology;
    }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public string CaptureTechnology { get; }

    public bool IsDisposed => Volatile.Read(ref _pngBytes) is null;

    public ReadOnlyMemory<byte> PngBytes => Volatile.Read(ref _pngBytes)
        ?? throw new ObjectDisposedException(nameof(CapturedPointerRegion));

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _pngBytes, null);
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
