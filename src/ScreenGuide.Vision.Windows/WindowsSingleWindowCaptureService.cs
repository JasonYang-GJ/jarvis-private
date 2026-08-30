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
internal readonly record struct ExactWindowCaptureBounds(
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    public long Width => (long)Right - Left;

    public long Height => (long)Bottom - Top;

    public bool IsValid => Right > Left && Bottom > Top && Width >= 2 && Height >= 2;
}

internal static class PrintWindowCaptureBoundsSelector
{
    // DWM can occasionally return only a title-bar-sized rectangle even though
    // GetWindowRect still describes the full exact HWND. Normal invisible-border
    // trimming is only a few pixels, so losing over 20% in either dimension is
    // treated as a truncated DWM result, not as authority to capture less content.
    private const int MinimumDwmCoveragePercent = 80;

    public static ExactWindowCaptureBounds Select(
        ExactWindowCaptureBounds? windowBounds,
        ExactWindowCaptureBounds? extendedFrameBounds)
    {
        var window = windowBounds is { IsValid: true } validWindow
            ? validWindow
            : (ExactWindowCaptureBounds?)null;
        var extended = extendedFrameBounds is { IsValid: true } validExtended
            ? validExtended
            : (ExactWindowCaptureBounds?)null;
        if (window is null && extended is null)
        {
            throw new InvalidOperationException("无法确定目标窗口范围，因此没有读取画面。 ");
        }

        if (window is null)
        {
            return extended!.Value;
        }

        if (extended is null)
        {
            return window.Value;
        }

        var dwmIsImplausiblyNarrow =
            (long)extended.Value.Width * 100
            < (long)window.Value.Width * MinimumDwmCoveragePercent;
        var dwmIsImplausiblyShort =
            (long)extended.Value.Height * 100
            < (long)window.Value.Height * MinimumDwmCoveragePercent;
        return dwmIsImplausiblyNarrow || dwmIsImplausiblyShort
            ? window.Value
            : extended.Value;
    }
}

internal static class PrintWindowCaptureBoundsValidator
{
    private const long MaxPixels = 32_000_000;

    public static (int Width, int Height) GetBitmapDimensions(ExactWindowCaptureBounds bounds)
    {
        if (!bounds.IsValid
            || bounds.Width > int.MaxValue
            || bounds.Height > int.MaxValue
            || bounds.Width > MaxPixels / bounds.Height)
        {
            throw new InvalidOperationException("目标窗口尺寸无效或过大，因此没有读取画面。 ");
        }

        return ((int)bounds.Width, (int)bounds.Height);
    }
}

public sealed class PrintWindowCaptureBackend(
    IWindowCaptureTargetVerifier identityVerifier) : IExactWindowCaptureBackend
{
    private const uint DwmwaExtendedFrameBounds = 9;
    private const uint PwRenderFullContent = 2;

    public Task<RawWindowFrame> CaptureAsync(
        WindowCaptureTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = new IntPtr(target.WindowHandle);
        identityVerifier.Verify(target);
        var bounds = GetBounds(handle);
        var (width, height) = PrintWindowCaptureBoundsValidator.GetBitmapDimensions(bounds);

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

    private static ExactWindowCaptureBounds GetBounds(IntPtr handle)
    {
        var hasWindowBounds = GetWindowRect(handle, out var windowBounds);
        var hasExtendedFrameBounds = DwmGetWindowAttribute(
            handle,
            DwmwaExtendedFrameBounds,
            out var extendedFrameBounds,
            Marshal.SizeOf<Rect>()) == 0;
        var selected = PrintWindowCaptureBoundsSelector.Select(
            hasWindowBounds ? Convert(windowBounds) : null,
            hasExtendedFrameBounds ? Convert(extendedFrameBounds) : null);
        return selected;
    }

    private static ExactWindowCaptureBounds Convert(Rect bounds) => new(
        bounds.Left,
        bounds.Top,
        bounds.Right,
        bounds.Bottom);

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
