using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ScreenGuide.App.Services;

internal sealed record CapturedWindowFrame(byte[] JpegBytes, int PixelWidth, int PixelHeight);
internal sealed record WindowCaptureTarget(string DisplayName, IntPtr WindowHandle);

internal sealed class SelectedWindowCaptureService
{
    private const uint PrintWindowRenderFullContent = 0x00000002;
    private const int MaxWidth = 1920;
    private const int MaxHeight = 1080;

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
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var deviceContext = graphics.GetHdc();
            try
            {
                if (!PrintWindow(selectedWindow.WindowHandle, deviceContext, PrintWindowRenderFullContent))
                {
                    throw new InvalidOperationException("Windows 没有返回当前软件的画面，请恢复窗口显示后重试。");
                }
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
        }

        DrawCursorMarkerIfInside(bitmap, rectangle);

        using var prepared = ResizeIfNeeded(bitmap);
        using var output = new MemoryStream();
        var jpegEncoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        prepared.Save(output, jpegEncoder, encoderParameters);

        return new CapturedWindowFrame(output.ToArray(), prepared.Width, prepared.Height);
    }

    private static Bitmap ResizeIfNeeded(Bitmap source)
    {
        var scale = Math.Min(1D, Math.Min((double)MaxWidth / source.Width, (double)MaxHeight / source.Height));
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

    private static void DrawCursorMarkerIfInside(Bitmap bitmap, NativeRectangle windowRectangle)
    {
        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var x = cursor.X - windowRectangle.Left;
        var y = cursor.Y - windowRectangle.Top;
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
