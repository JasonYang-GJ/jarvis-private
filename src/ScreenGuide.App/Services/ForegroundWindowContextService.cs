using System.Runtime.InteropServices;
using System.Text;

namespace ScreenGuide.App.Services;

internal sealed record ForegroundWindowContext(
    IntPtr WindowHandle,
    string Title,
    uint ProcessId)
{
    public static ForegroundWindowContext Unknown { get; } = new(IntPtr.Zero, "未能识别的窗口", 0);

    public bool CanCapture => WindowHandle != IntPtr.Zero;

    public bool BelongsToCurrentProcess => ProcessId == Environment.ProcessId;
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
        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return string.IsNullOrWhiteSpace(title)
            ? ForegroundWindowContext.Unknown
            : new ForegroundWindowContext(windowHandle, title, processId);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
