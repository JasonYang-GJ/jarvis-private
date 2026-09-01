using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Automation;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Vision.Windows;

public sealed class WindowsPointerDesktopProbe(
    IForegroundWindowContextProvider foregroundWindows) : IPointerDesktopProbe
{
    public PointerDesktopSnapshot CaptureCurrent()
    {
        if (!GetCursorPos(out var point))
        {
            throw Unavailable();
        }

        return Observe(point.X, point.Y);
    }

    public PointerDesktopSnapshot ObserveAt(int screenX, int screenY) => Observe(screenX, screenY);

    private PointerDesktopSnapshot Observe(int screenX, int screenY)
    {
        var foregroundHandle = GetForegroundWindow();
        if (foregroundHandle == IntPtr.Zero)
        {
            throw Unavailable();
        }

        var foreground = foregroundWindows.ResolveWindow(foregroundHandle.ToInt64());
        if (foreground is null
            || foreground.ProcessId <= 0
            || foreground.ProcessStartTimeUtc == default
            || !GetWindowRect(foregroundHandle, out var rect))
        {
            throw Unavailable();
        }

        var bounds = ToBounds(rect);
        var dpi = GetDpiForWindow(foregroundHandle);
        if (!bounds.IsValid || dpi == 0)
        {
            throw Unavailable();
        }

        var point = new Point(screenX, screenY);
        var underPoint = WindowFromPoint(point);
        if (underPoint == IntPtr.Zero)
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.PointTargetChanged,
                "指针下方没有可确认的目标窗口。 ");
        }

        _ = GetWindowThreadProcessId(underPoint, out var pointProcessId);
        var sameProcessChild = underPoint != foregroundHandle
            && IsChild(foregroundHandle, underPoint)
            && pointProcessId == foreground.ProcessId;
        var target = new WindowCaptureTarget(
            foreground.WindowHandle,
            foreground.WindowTitle,
            foreground.ProcessName,
            foreground.ProcessId,
            foreground.ProcessStartTimeUtc,
            foreground.ObservedAtUtc);
        return new PointerDesktopSnapshot(
            new PointerWindowIdentity(target, bounds, dpi, dpi),
            foregroundHandle.ToInt64(),
            screenX,
            screenY,
            underPoint.ToInt64(),
            pointProcessId <= int.MaxValue ? (int)pointProcessId : -1,
            sameProcessChild,
            TryGetTrustworthyElementBounds(screenX, screenY, foreground.ProcessId));
    }

    private static PixelBounds? TryGetTrustworthyElementBounds(int x, int y, int targetProcessId)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            if (element is null
                || element.Current.ProcessId != targetProcessId
                || element.Current.IsOffscreen)
            {
                return null;
            }

            var rectangle = element.Current.BoundingRectangle;
            if (rectangle.IsEmpty
                || !double.IsFinite(rectangle.Left)
                || !double.IsFinite(rectangle.Top)
                || !double.IsFinite(rectangle.Width)
                || !double.IsFinite(rectangle.Height))
            {
                return null;
            }

            var bounds = new PixelBounds(
                checked((int)Math.Floor(rectangle.Left)),
                checked((int)Math.Floor(rectangle.Top)),
                checked((int)Math.Ceiling(rectangle.Width)),
                checked((int)Math.Ceiling(rectangle.Height)));
            return bounds.IsValid && bounds.Contains(x, y) ? bounds : null;
        }
        catch (Exception exception) when (exception is
            ElementNotAvailableException or
            InvalidOperationException or
            OverflowException or
            COMException)
        {
            return null;
        }
    }

    private static PixelBounds ToBounds(Rect value) => new(
        value.Left,
        value.Top,
        checked(value.Right - value.Left),
        checked(value.Bottom - value.Top));

    private static PointerRegionException Unavailable() => new(
        PointerRegionErrorCodes.TargetChanged,
        "无法确认当前前台窗口的完整身份，请重新切回目标窗口。 ");

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}

public sealed class WindowsPointerRegionCaptureService(
    IWindowCaptureService windowCapture) : IPointerRegionCaptureService
{
    public async Task<CapturedPointerRegion> CaptureAsync(
        WindowCaptureTarget target,
        PixelBounds windowBounds,
        PixelBounds regionBounds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!windowBounds.IsValid
            || !regionBounds.IsValid
            || regionBounds.Intersect(windowBounds) != regionBounds
            || regionBounds.Width > PointerRegionSelector.MaximumWidth
            || regionBounds.Height > PointerRegionSelector.MaximumHeight)
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.CaptureFailed,
                "目标区域超出已确认窗口，未读取画面。 ");
        }

        await using var fullFrame = await windowCapture.CaptureAsync(target, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var encodedCopy = fullFrame.PngBytes.ToArray();
        try
        {
            using var input = new MemoryStream(encodedCopy, writable: false);
            using var source = new Bitmap(input);
            var sourceRectangle = ScaleToFrame(windowBounds, regionBounds, source.Width, source.Height);
            var outputWidth = Math.Min(regionBounds.Width, PointerRegionSelector.MaximumWidth);
            var outputHeight = Math.Min(regionBounds.Height, PointerRegionSelector.MaximumHeight);
            using var output = new Bitmap(outputWidth, outputHeight, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(output))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(
                    source,
                    new Rectangle(0, 0, outputWidth, outputHeight),
                    sourceRectangle,
                    GraphicsUnit.Pixel);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var encoded = new MemoryStream();
            output.Save(encoded, ImageFormat.Png);
            var bytes = encoded.ToArray();
            if (encoded.TryGetBuffer(out var buffer))
            {
                CryptographicOperations.ZeroMemory(buffer.AsSpan());
            }

            return new CapturedPointerRegion(
                bytes,
                outputWidth,
                outputHeight,
                $"{fullFrame.CaptureTechnology}.RegionCrop");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PointerRegionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            ExternalException or
            InvalidOperationException)
        {
            throw new PointerRegionException(
                PointerRegionErrorCodes.CaptureFailed,
                "本机未能安全裁剪目标区域，未保留画面。 ");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedCopy);
        }
    }

    private static Rectangle ScaleToFrame(
        PixelBounds window,
        PixelBounds region,
        int frameWidth,
        int frameHeight)
    {
        var scaleX = frameWidth / (double)window.Width;
        var scaleY = frameHeight / (double)window.Height;
        var left = Math.Clamp(
            (int)Math.Floor((region.Left - window.Left) * scaleX),
            0,
            frameWidth - 1);
        var top = Math.Clamp(
            (int)Math.Floor((region.Top - window.Top) * scaleY),
            0,
            frameHeight - 1);
        var right = Math.Clamp(
            (int)Math.Ceiling((region.Right - window.Left) * scaleX),
            left + 1,
            frameWidth);
        var bottom = Math.Clamp(
            (int)Math.Ceiling((region.Bottom - window.Top) * scaleY),
            top + 1,
            frameHeight);
        return new Rectangle(left, top, right - left, bottom - top);
    }
}
