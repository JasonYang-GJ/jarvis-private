using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Automation;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Vision.Windows;

public sealed class WindowsSensitiveWindowPolicy : ISensitiveWindowPolicy
{
    private static readonly string[] SensitiveProcesses =
    [
        "CredentialUIBroker", "LogonUI", "LockApp", "SecurityHealthHost"
    ];

    private static readonly string[] SensitiveTitles =
    [
        "Windows 安全中心", "Windows Security", "输入密码", "请输入密码",
        "更改密码", "安全验证", "付款", "支付", "银行卡"
    ];

    public SensitiveWindowAssessment Assess(WindowCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (SensitiveProcesses.Any(item =>
                string.Equals(item, target.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            return new SensitiveWindowAssessment(true, "这是 Windows 安全或凭据界面，当前版本不会读取。 ");
        }

        if (SensitiveTitles.Any(item =>
                target.WindowTitle.Contains(item, StringComparison.OrdinalIgnoreCase)))
        {
            return new SensitiveWindowAssessment(true, "这个窗口可能包含密码、支付或身份验证信息，当前版本不会读取。 ");
        }

        try
        {
            var handle = new IntPtr(target.WindowHandle);
            var root = AutomationElement.FromHandle(handle);
            var password = root?.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsPasswordProperty, true));
            if (password is not null)
            {
                return new SensitiveWindowAssessment(true, "这个窗口包含密码输入框，当前版本不会读取。 ");
            }
        }
        catch (ElementNotAvailableException)
        {
            return new SensitiveWindowAssessment(true, "目标窗口已经变化，请重新切回后再试。 ");
        }
        catch (InvalidOperationException)
        {
            // Some windows expose no UI Automation tree. Capture remains bounded to the HWND.
        }

        return new SensitiveWindowAssessment(false);
    }
}

public interface IExactWindowCaptureBackend
{
    Task<RawWindowFrame> CaptureAsync(WindowCaptureTarget target, CancellationToken cancellationToken);
}

public sealed record RawWindowFrame(byte[] PngBytes, int PixelWidth, int PixelHeight, string Technology);

public sealed class WindowsWindowCaptureTargetVerifier : IWindowCaptureTargetVerifier
{
    public void Verify(WindowCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.ProcessId <= 0 || target.ProcessStartTimeUtc == default)
        {
            throw new WindowIdentityException(
                WindowIdentityErrorCodes.Missing,
                "这条窗口授权缺少可信身份，请重新选择窗口并确认。 ");
        }

        var handle = new IntPtr(target.WindowHandle);
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            throw Changed();
        }

        try
        {
            _ = GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0 || processId > int.MaxValue || processId != target.ProcessId)
            {
                throw Changed();
            }

            using var process = Process.GetProcessById((int)processId);
            var startTimeUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
            if (startTimeUtc != target.ProcessStartTimeUtc
                || !string.Equals(process.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(GetTitle(handle), target.WindowTitle, StringComparison.Ordinal))
            {
                throw Changed();
            }
        }
        catch (WindowIdentityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            throw Changed();
        }
    }

    private static WindowIdentityException Changed() => new(
        WindowIdentityErrorCodes.Changed,
        "目标窗口身份已经变化，请重新选择窗口并确认。 ");

    private static string GetTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[Math.Min(length + 1, 512)];
        var written = GetWindowText(handle, buffer, buffer.Length);
        return written > 0 ? new string(buffer, 0, written).Trim() : string.Empty;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}

public sealed class WindowsSingleWindowCaptureService(
    IExactWindowCaptureBackend backend,
    ISensitiveWindowPolicy sensitiveWindowPolicy,
    IWindowCaptureTargetVerifier identityVerifier) : IWindowCaptureService
{
    public async Task<CapturedWindowFrame> CaptureAsync(
        WindowCaptureTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        identityVerifier.Verify(target);
        var assessment = sensitiveWindowPolicy.Assess(target);
        if (assessment.IsSensitive)
        {
            throw new UnauthorizedAccessException(assessment.Reason ?? "这个窗口不允许读取。 ");
        }

        var raw = await backend.CaptureAsync(target, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            identityVerifier.Verify(target);
            return new CapturedWindowFrame(raw.PngBytes, raw.PixelWidth, raw.PixelHeight, raw.Technology);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(raw.PngBytes);
            throw;
        }
    }
}

/// <summary>
/// Captures the exact HWND with PrintWindow. It never calls desktop or monitor capture APIs.
/// This is the conservative Windows fallback while the provider-neutral boundary allows a
/// Windows Graphics Capture implementation to replace it without changing Host or UI code.
/// </summary>
public sealed class PrintWindowCaptureBackend(
    IWindowCaptureTargetVerifier identityVerifier) : IExactWindowCaptureBackend
{
    private const uint DwmwaExtendedFrameBounds = 9;
    private const uint PwRenderFullContent = 2;
    private const long MaxPixels = 32_000_000;

    public Task<RawWindowFrame> CaptureAsync(
        WindowCaptureTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = new IntPtr(target.WindowHandle);
        identityVerifier.Verify(target);
        var bounds = GetBounds(handle);
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width < 2 || height < 2 || (long)width * height > MaxPixels)
        {
            throw new InvalidOperationException("目标窗口尺寸无效或过大，因此没有读取画面。 ");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var deviceContext = graphics.GetHdc();
        bool succeeded;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            succeeded = PrintWindow(handle, deviceContext, PwRenderFullContent);
        }
        finally
        {
            graphics.ReleaseHdc(deviceContext);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!succeeded)
        {
            throw new InvalidOperationException("Windows 未能读取这个窗口的画面；没有改为截取整个桌面。 ");
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();
        if (stream.TryGetBuffer(out var buffer))
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan());
        }
        return Task.FromResult(new RawWindowFrame(
            bytes, width, height, "Windows.PrintWindow.SingleHwnd"));
    }

    private static Rect GetBounds(IntPtr handle)
    {
        if (DwmGetWindowAttribute(
                handle,
                DwmwaExtendedFrameBounds,
                out var bounds,
                Marshal.SizeOf<Rect>()) != 0
            && !GetWindowRect(handle, out bounds))
        {
            throw new InvalidOperationException("无法确定目标窗口范围，因此没有读取画面。 ");
        }

        return bounds;
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
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hWnd,
        uint attribute,
        out Rect value,
        int size);
}
