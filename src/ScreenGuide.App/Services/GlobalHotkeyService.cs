using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenGuide.App.Services;

internal sealed class GlobalHotkeyService : IDisposable
{
    private const int VoiceHotkeyId = 0x5101;
    private const int StopHotkeyId = 0x5102;
    private const int WmHotkey = 0x0312;

    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeySpace = 0x20;
    private const uint VirtualKeyQ = 0x51;
    private const uint VirtualKeyF10 = 0x79;

    private static readonly HotkeyCandidate[] VoiceCandidates =
    [
        new(ModControl | ModShift | ModNoRepeat, VirtualKeySpace, "Ctrl + Shift + 空格"),
        new(ModControl | ModAlt | ModNoRepeat, VirtualKeySpace, "Ctrl + Alt + 空格"),
        new(ModControl | ModShift | ModNoRepeat, VirtualKeyF10, "Ctrl + Shift + F10")
    ];

    private static readonly HotkeyCandidate[] StopCandidates =
    [
        new(ModControl | ModShift | ModNoRepeat, VirtualKeyQ, "Ctrl + Shift + Q"),
        new(ModControl | ModAlt | ModNoRepeat, VirtualKeyQ, "Ctrl + Alt + Q")
    ];

    private IntPtr _windowHandle;
    private HwndSource? _windowSource;
    private bool _isRegistered;

    public event EventHandler? VoiceRequested;

    public event EventHandler? StopRequested;

    public string VoiceShortcutDisplay { get; private set; } = VoiceCandidates[0].DisplayText;

    public string StopShortcutDisplay { get; private set; } = StopCandidates[0].DisplayText;

    public void Register(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (_isRegistered)
        {
            return;
        }

        _windowHandle = new WindowInteropHelper(owner).EnsureHandle();
        _windowSource = HwndSource.FromHwnd(_windowHandle)
                        ?? throw new InvalidOperationException("无法连接 Windows 快捷键消息。");
        _windowSource.AddHook(WindowMessageHook);

        var voiceCandidate = RegisterFirstAvailable(VoiceHotkeyId, VoiceCandidates);
        if (voiceCandidate is null)
        {
            CleanupHook();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "常用的语音快捷键都已被其他软件占用。");
        }

        var stopCandidate = RegisterFirstAvailable(StopHotkeyId, StopCandidates);
        if (stopCandidate is null)
        {
            UnregisterHotKey(_windowHandle, VoiceHotkeyId);
            CleanupHook();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "常用的停止快捷键都已被其他软件占用。");
        }

        VoiceShortcutDisplay = voiceCandidate.Value.DisplayText;
        StopShortcutDisplay = stopCandidate.Value.DisplayText;
        _isRegistered = true;
    }

    public void Dispose()
    {
        if (_isRegistered)
        {
            UnregisterHotKey(_windowHandle, VoiceHotkeyId);
            UnregisterHotKey(_windowHandle, StopHotkeyId);
            _isRegistered = false;
        }

        CleanupHook();
    }

    private IntPtr WindowMessageHook(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message != WmHotkey)
        {
            return IntPtr.Zero;
        }

        var hotkeyId = wordParameter.ToInt32();
        if (hotkeyId == VoiceHotkeyId)
        {
            handled = true;
            VoiceRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (hotkeyId == StopHotkeyId)
        {
            handled = true;
            StopRequested?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private void CleanupHook()
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }

        _windowHandle = IntPtr.Zero;
    }

    private HotkeyCandidate? RegisterFirstAvailable(int hotkeyId, IEnumerable<HotkeyCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (RegisterHotKey(_windowHandle, hotkeyId, candidate.Modifiers, candidate.VirtualKey))
            {
                return candidate;
            }
        }

        return null;
    }

    private readonly record struct HotkeyCandidate(uint Modifiers, uint VirtualKey, string DisplayText);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
