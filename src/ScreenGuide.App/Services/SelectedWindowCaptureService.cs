using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ScreenGuide.App.Services;

internal sealed record CapturedWindowFrame(
    byte[] JpegBytes,
    int PixelWidth,
    int PixelHeight,
    byte[]? PointerFocusJpegBytes);
internal sealed record WindowCaptureTarget(string DisplayName, IntPtr WindowHandle);

internal sealed class SelectedWindowCaptureService
{
    private const uint PrintWindowRenderFullContent = 0x00000002;
    private const int MaxWidth = 1920;
    private const int MaxHeight = 1080;
    private const int FocusWidth = 1100;
    private const int FocusHeight = 760;

    public CapturedWindowFrame CaptureOnce(WindowCaptureTarget selectedWindow)
    {
        if (selectedWindow.WindowHandle == IntPtr.Zero || !IsWindow(selectedWindow.WindowHandle))
        {
            throw new InvalidOperationException("刚才的前台窗口已经关闭或无法定位，请切回目标软件后重新提问。");
        }

        if (!GetWindowRect(selectedWindow.WindowHandle, out var rectangle))
        {
            throw new InvalidOperationException("无法取得当前软件窗口的大小，请恢复显示后再试。");
        }

        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("当前软件窗口已经最小化，无法读取画面。");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var captured = false;
        if (GetForegroundWindow() == selectedWindow.WindowHandle)
        {
            captured = TryCopyVisibleWindow(bitmap, rectangle);
        }

        if (!captured)
        {
            using var graphics = Graphics.FromImage(bitmap);
            var deviceContext = graphics.GetHdc();
            try
            {
                captured = PrintWindow(selectedWindow.WindowHandle, deviceContext, PrintWindowRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
        }

        if (!captured || LooksBlank(bitmap))
        {
            throw new InvalidOperationException("Windows 返回了空白画面。请把目标窗口恢复到最前面后再问一次。");
        }

        Bitmap? focusBitmap = null;
        if (TryGetCursorCoordinates(bitmap, rectangle, out var cursorX, out var cursorY))
        {
            var focusRectangle = BuildFocusRectangle(bitmap, cursorX, cursorY);
            focusBitmap = bitmap.Clone(focusRectangle, PixelFormat.Format24bppRgb);
            DrawCursorMarker(bitmap, cursorX, cursorY);
            DrawCursorMarker(focusBitmap, cursorX - focusRectangle.Left, cursorY - focusRectangle.Top);
        }

        var fullFrame = EncodeJpeg(bitmap, MaxWidth, MaxHeight, 85L);
        byte[]? focusedFrame = null;
        if (focusBitmap is not null)
        {
            using (focusBitmap)
            {
                focusedFrame = EncodeJpeg(focusBitmap, FocusWidth, FocusHeight, 90L).Bytes;
            }
        }

        return new CapturedWindowFrame(
            fullFrame.Bytes,
            fullFrame.Width,
            fullFrame.Height,
            focusedFrame);
    }

    private static (byte[] Bytes, int Width, int Height) EncodeJpeg(
        Bitmap source,
        int maxWidth,
        int maxHeight,
        long quality)
    {
        using var prepared = ResizeIfNeeded(source, maxWidth, maxHeight);
        using var output = new MemoryStream();
        var jpegEncoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        prepared.Save(output, jpegEncoder, encoderParameters);
        return (output.ToArray(), prepared.Width, prepared.Height);
    }

    private static Bitmap ResizeIfNeeded(Bitmap source, int maxWidth, int maxHeight)
    {
        var scale = Math.Min(1D, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(resized);
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, width, height);
        return resized;
    }

    private static bool TryCopyVisibleWindow(Bitmap bitmap, NativeRectangle rectangle)
    {
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                rectangle.Left,
                rectangle.Top,
                0,
                0,
                bitmap.Size,
                CopyPixelOperation.SourceCopy);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksBlank(Bitmap bitmap)
    {
        var minimum = 255;
        var maximum = 0;
        var stepX = Math.Max(1, bitmap.Width / 80);
        var stepY = Math.Max(1, bitmap.Height / 60);
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                var color = bitmap.GetPixel(x, y);
                var luminance = (color.R * 3 + color.G * 6 + color.B) / 10;
                minimum = Math.Min(minimum, luminance);
                maximum = Math.Max(maximum, luminance);
                if (maximum - minimum >= 18)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryGetCursorCoordinates(
        Bitmap bitmap,
        NativeRectangle windowRectangle,
        out int x,
        out int y)
    {
        x = 0;
        y = 0;
        if (!GetCursorPos(out var cursor))
        {
            return false;
        }

        x = cursor.X - windowRectangle.Left;
        y = cursor.Y - windowRectangle.Top;
        return x >= 0 && y >= 0 && x < bitmap.Width && y < bitmap.Height;
    }

    private static Rectangle BuildFocusRectangle(Bitmap bitmap, int cursorX, int cursorY)
    {
        var width = Math.Min(FocusWidth, bitmap.Width);
        var height = Math.Min(FocusHeight, bitmap.Height);
        var left = Math.Clamp(cursorX - width / 2, 0, bitmap.Width - width);
        var top = Math.Clamp(cursorY - height / 2, 0, bitmap.Height - height);
        return new Rectangle(left, top, width, height);
    }

    private static void DrawCursorMarker(Bitmap bitmap, int x, int y)
    {
        if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
        {
            return;
        }

        const int radius = 30;
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var whitePen = new Pen(Color.White, 8F);
        using var redPen = new Pen(Color.FromArgb(230, 220, 32, 32), 4F);
        var marker = new Rectangle(x - radius, y - radius, radius * 2, radius * 2);
        graphics.DrawEllipse(whitePen, marker);
        graphics.DrawEllipse(redPen, marker);
        graphics.DrawLine(whitePen, x - 12, y, x + 12, y);
        graphics.DrawLine(whitePen, x, y - 12, x, y + 12);
        graphics.DrawLine(redPen, x - 12, y, x + 12, y);
        graphics.DrawLine(redPen, x, y - 12, x, y + 12);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr windowHandle, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
