using System.Runtime.InteropServices;
using System.Text;

namespace ScreenGuide.App.Services;

internal sealed record ForegroundWindowContext(string Title)
{
    public static ForegroundWindowContext Unknown { get; } = new("未能识别的窗口");
}

internal sealed class ForegroundWindowContextService
{
    public ForegroundWindowContext GetCurrent()
    {
        var windowHandle = GetForegroundWindow();
        if (windowHandle == IntPtr.Zero)
        {
            return ForegroundWindowContext.Unknown;
        }

        var titleLength = GetWindowTextLength(windowHandle);
        if (titleLength <= 0)
        {
            return ForegroundWindowContext.Unknown;
        }

        var titleBuffer = new StringBuilder(titleLength + 1);
        _ = GetWindowText(windowHandle, titleBuffer, titleBuffer.Capacity);
        var title = titleBuffer.ToString().Trim();
        return string.IsNullOrWhiteSpace(title)
            ? ForegroundWindowContext.Unknown
            : new ForegroundWindowContext(title);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);
}
