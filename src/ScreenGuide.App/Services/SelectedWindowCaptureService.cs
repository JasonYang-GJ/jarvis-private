using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenGuide.App.Services;

internal sealed record CapturedWindowFrame(byte[] JpegBytes, int PixelWidth, int PixelHeight);

internal sealed class SelectedWindowCaptureService
{
    private const uint PrintWindowRenderFullContent = 0x00000002;
    private const int MaxWidth = 1280;
    private const int MaxHeight = 900;

    public CapturedWindowFrame CaptureOnce(WindowSelectionResult selectedWindow)
    {
        if (selectedWindow.WindowHandle == IntPtr.Zero || !IsWindow(selectedWindow.WindowHandle))
        {
            throw new InvalidOperationException("授权窗口已经关闭或无法准确定位，请重新选择软件。");
        }

        if (!GetWindowRect(selectedWindow.WindowHandle, out var rectangle))
        {
            throw new InvalidOperationException("无法取得授权窗口的大小，请恢复显示后再试。");
        }

        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("授权窗口当前已最小化，无法读取画面。");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var deviceContext = graphics.GetHdc();
            try
            {
                if (!PrintWindow(selectedWindow.WindowHandle, deviceContext, PrintWindowRenderFullContent))
                {
                    throw new InvalidOperationException("Windows 没有返回授权窗口画面，请恢复窗口显示后重试。");
                }
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
        }

        using var prepared = ResizeIfNeeded(bitmap);
        using var output = new MemoryStream();
        var jpegEncoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr windowHandle, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal static class NativeWindowLocator
{
    public static IntPtr FindUniqueWindow(string title, int expectedWidth, int expectedHeight)
    {
        var candidates = new List<WindowCandidate>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindowTextLength(handle) == 0)
            {
                return true;
            }

            var builder = new StringBuilder(GetWindowTextLength(handle) + 1);
            _ = GetWindowText(handle, builder, builder.Capacity);
            if (!string.Equals(builder.ToString(), title, StringComparison.Ordinal))
            {
                return true;
            }

            if (!GetWindowRect(handle, out var rectangle))
            {
                return true;
            }

            var width = rectangle.Right - rectangle.Left;
            var height = rectangle.Bottom - rectangle.Top;
            var distance = Math.Abs(width - expectedWidth) + Math.Abs(height - expectedHeight);
            candidates.Add(new WindowCandidate(handle, distance));
            return true;
        }, IntPtr.Zero);

        if (candidates.Count == 0)
        {
            return IntPtr.Zero;
        }

        var ordered = candidates.OrderBy(candidate => candidate.SizeDistance).ToArray();
        if (ordered.Length > 1 && ordered[0].SizeDistance == ordered[1].SizeDistance)
        {
            return IntPtr.Zero;
        }

        return ordered[0].SizeDistance <= 80 ? ordered[0].Handle : IntPtr.Zero;
    }

    private sealed record WindowCandidate(IntPtr Handle, int SizeDistance);

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
