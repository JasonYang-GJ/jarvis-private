using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ScreenGuide.DesktopClient.Services;

/// <summary>Registered only during one visibly armed interaction; no global input hook.</summary>
internal sealed class TemporaryPointerHotkey : IDisposable
{
    private const int HotkeyId = 0x5318;
    private readonly HwndSource _source;
    private readonly Action _pressed;
    private bool _disposed;

    public TemporaryPointerHotkey(nint window, Action pressed)
    {
        _source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("元枢窗口尚未就绪。");
        _pressed = pressed;
        if (!RegisterHotKey(window, HotkeyId, 0x4003, 0x77)) // Ctrl+Alt+F8, no repeat
            throw new InvalidOperationException("Ctrl+Alt+F8 已被占用，未开始读取或发送。请释放快捷键后重试。");
        try { _source.AddHook(OnMessage); }
        catch { UnregisterHotKey(window, HotkeyId); throw; }
    }

    private nint OnMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (!_disposed && message == 0x0312 && wParam == HotkeyId)
        {
            handled = true;
            _pressed();
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterHotKey(_source.Handle, HotkeyId);
        _source.RemoveHook(OnMessage);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();
}
