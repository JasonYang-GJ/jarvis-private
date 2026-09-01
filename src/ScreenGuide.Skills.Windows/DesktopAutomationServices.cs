using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ScreenGuide.Skills.Windows;

public sealed record ForegroundWindowSnapshot(
    long WindowHandle,
    string WindowTitle,
    string ProcessName,
    int ProcessId,
    DateTimeOffset ProcessStartTimeUtc,
    DateTimeOffset ObservedAtUtc);

public static class WindowIdentityErrorCodes
{
    public const string Missing = "window_identity_missing";
    public const string Changed = "window_identity_changed";
}

public sealed class WindowIdentityException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public static class WindowIdentityContract
{
    public static bool Matches(
        ForegroundWindowSnapshot expected,
        ForegroundWindowSnapshot? actual) =>
        actual is not null
        && expected.WindowHandle == actual.WindowHandle
        && expected.ProcessId > 0
        && expected.ProcessId == actual.ProcessId
        && expected.ProcessStartTimeUtc == actual.ProcessStartTimeUtc
        && string.Equals(expected.ProcessName, actual.ProcessName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.WindowTitle, actual.WindowTitle, StringComparison.Ordinal);

    public static void RequireMatch(
        ForegroundWindowSnapshot expected,
        ForegroundWindowSnapshot? actual)
    {
        if (expected.ProcessId <= 0 || expected.ProcessStartTimeUtc == default)
        {
            throw new WindowIdentityException(
                WindowIdentityErrorCodes.Missing,
                "这条窗口授权缺少可信身份，请重新选择窗口并确认。 ");
        }

        if (!Matches(expected, actual))
        {
            throw new WindowIdentityException(
                WindowIdentityErrorCodes.Changed,
                "目标窗口身份已经变化，请重新选择窗口并确认。 ");
        }
    }
}

public sealed record DesktopAutomationResult(bool Verified, string Summary, string? TechnicalDetail = null);

public static class DesktopSearchErrorCodes
{
    public const string InvalidQuery = "desktop_search_query_invalid";
    public const string CandidateNotFound = "desktop_search_candidate_not_found";
    public const string CandidateAmbiguous = "desktop_search_candidate_ambiguous";
    public const string TargetChanged = "desktop_search_target_changed";
    public const string WriteVerificationFailed = "desktop_search_write_verification_failed";
    public const string FocusChanged = "desktop_search_focus_changed";
}

public sealed class DesktopSearchException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public static class DesktopSearchQuery
{
    public static string NormalizeAndValidate(string? query)
    {
        var raw = query ?? string.Empty;
        if (raw.Any(char.IsControl))
        {
            throw new DesktopSearchException(
                DesktopSearchErrorCodes.InvalidQuery,
                "搜索内容不能包含换行、制表符或控制字符。 ");
        }

        var normalized = raw.Trim().Normalize(NormalizationForm.FormKC);
        if (normalized.Length is < 1 or > 200)
        {
            throw new DesktopSearchException(
                DesktopSearchErrorCodes.InvalidQuery,
                "搜索内容应为 1 到 200 个字符。 ");
        }

        return normalized;
    }
}

internal readonly record struct DesktopSearchBounds(double X, double Y, double Width, double Height)
{
    public bool IsUsable =>
        double.IsFinite(X)
        && double.IsFinite(Y)
        && double.IsFinite(Width)
        && double.IsFinite(Height)
        && Width > 0
        && Height > 0;
}

internal sealed record DesktopSearchControlSnapshot(
    string RuntimeId,
    long OwnerWindowHandle,
    string Name,
    string AutomationId,
    string HelpText,
    string LabeledBy,
    bool IsEnabled,
    bool IsOffscreen,
    bool IsPassword,
    bool IsReadOnly,
    bool SupportsValuePattern,
    bool HasKeyboardFocus,
    DesktopSearchBounds Bounds,
    string Value);

internal sealed record DesktopSearchControl(object NativeReference, DesktopSearchControlSnapshot Snapshot);

internal interface IDesktopSearchAutomationDriver
{
    bool IsWindow(long windowHandle);

    bool TryActivate(long windowHandle);

    long GetForegroundWindowHandle();

    IReadOnlyList<DesktopSearchControl> Discover(long windowHandle);

    DesktopSearchControlSnapshot Read(DesktopSearchControl control);

    void Focus(DesktopSearchControl control);

    void SetValue(DesktopSearchControl control, string value);

    void SubmitEnter(DesktopSearchControl control, Action validateAtSendBoundary);
}

internal sealed class SafeForegroundSearchExecutor(
    IForegroundWindowContextProvider foregroundWindows,
    IDesktopSearchAutomationDriver driver)
{
    private const int MinimumConfidence = 100;

    public DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query)
    {
        ArgumentNullException.ThrowIfNull(expectedWindow);
        var normalizedQuery = DesktopSearchQuery.NormalizeAndValidate(query);

        RequireWindow(expectedWindow);
        if (!driver.IsWindow(expectedWindow.WindowHandle))
        {
            throw Changed();
        }

        if (!driver.TryActivate(expectedWindow.WindowHandle))
        {
            throw Changed();
        }

        RequireWindowAndForeground(expectedWindow);
        IReadOnlyList<DesktopSearchControl> discovered;
        try
        {
            discovered = driver.Discover(expectedWindow.WindowHandle);
        }
        catch (Exception exception) when (IsVolatileAutomationFailure(exception))
        {
            throw Changed();
        }

        RequireWindowAndForeground(expectedWindow);
        var safeCandidates = discovered
            .Where(control => IsSafe(control.Snapshot, expectedWindow.WindowHandle))
            .Select(control => new ScoredControl(control, Score(control.Snapshot)))
            .ToArray();
        if (safeCandidates.Length == 0)
        {
            throw new DesktopSearchException(
                DesktopSearchErrorCodes.CandidateNotFound,
                "没有找到可可靠写入的搜索或地址栏，因此没有输入任何内容。 ");
        }

        var highestScore = safeCandidates.Max(item => item.Score);
        var highest = safeCandidates.Where(item => item.Score == highestScore).ToArray();
        if (highestScore < MinimumConfidence || highest.Length != 1)
        {
            throw new DesktopSearchException(
                DesktopSearchErrorCodes.CandidateAmbiguous,
                "无法唯一确认安全的搜索或地址栏，因此没有输入任何内容。 ");
        }

        var selected = highest[0].Control;
        var bound = selected.Snapshot;
        var beforeFocus = ReadAndRequireBoundTarget(selected, bound, expectedWindow.WindowHandle);
        if (!IsSafe(beforeFocus, expectedWindow.WindowHandle))
        {
            throw Changed();
        }

        try
        {
            driver.Focus(selected);
            RequireWindowAndForeground(expectedWindow);
            var beforeWrite = ReadAndRequireBoundTarget(selected, bound, expectedWindow.WindowHandle);
            if (!beforeWrite.HasKeyboardFocus)
            {
                throw FocusChanged();
            }

            driver.SetValue(selected, normalizedQuery);
            RequireWindowAndForeground(expectedWindow);
            var afterWrite = ReadAndRequireBoundTarget(selected, bound, expectedWindow.WindowHandle);
            if (!afterWrite.HasKeyboardFocus)
            {
                throw FocusChanged();
            }

            if (!string.Equals(afterWrite.Value, normalizedQuery, StringComparison.Ordinal))
            {
                throw new DesktopSearchException(
                    DesktopSearchErrorCodes.WriteVerificationFailed,
                    "搜索内容没有被精确写入，因此没有提交。 ");
            }

            RequireWindowAndForeground(expectedWindow);
            var beforeSubmit = ReadAndRequireBoundTarget(selected, bound, expectedWindow.WindowHandle);
            if (!beforeSubmit.HasKeyboardFocus)
            {
                throw FocusChanged();
            }

            if (!string.Equals(beforeSubmit.Value, normalizedQuery, StringComparison.Ordinal))
            {
                throw new DesktopSearchException(
                    DesktopSearchErrorCodes.WriteVerificationFailed,
                    "搜索内容在提交前已经变化，因此没有提交。 ");
            }

            driver.SubmitEnter(selected, () =>
            {
                RequireWindowAndForeground(expectedWindow);
                var atSendBoundary = ReadAndRequireBoundTarget(
                    selected,
                    bound,
                    expectedWindow.WindowHandle);
                if (!atSendBoundary.HasKeyboardFocus)
                {
                    throw FocusChanged();
                }

                if (!string.Equals(atSendBoundary.Value, normalizedQuery, StringComparison.Ordinal))
                {
                    throw new DesktopSearchException(
                        DesktopSearchErrorCodes.WriteVerificationFailed,
                        "搜索内容在提交边界已经变化，因此没有提交。 ");
                }
            });
        }
        catch (DesktopSearchException)
        {
            throw;
        }
        catch (WindowIdentityException)
        {
            throw;
        }
        catch (Exception exception) when (IsVolatileAutomationFailure(exception))
        {
            throw Changed();
        }

        return new DesktopAutomationResult(
            true,
            "已确认内容写入唯一识别的搜索框并提交；搜索结果页面尚未验证。",
            $"UniqueCandidate=true; Confidence={highestScore}; ExactValueVerified=true; SubmitCount=1");
    }

    private void RequireWindow(ForegroundWindowSnapshot expectedWindow) =>
        WindowIdentityContract.RequireMatch(
            expectedWindow,
            foregroundWindows.ResolveWindow(expectedWindow.WindowHandle));

    private void RequireWindowAndForeground(ForegroundWindowSnapshot expectedWindow)
    {
        RequireWindow(expectedWindow);
        if (driver.GetForegroundWindowHandle() != expectedWindow.WindowHandle)
        {
            throw Changed();
        }
    }

    private DesktopSearchControlSnapshot ReadAndRequireBoundTarget(
        DesktopSearchControl control,
        DesktopSearchControlSnapshot bound,
        long expectedWindowHandle)
    {
        DesktopSearchControlSnapshot current;
        try
        {
            current = driver.Read(control);
        }
        catch (Exception exception) when (IsVolatileAutomationFailure(exception))
        {
            throw Changed();
        }

        if (current.OwnerWindowHandle != expectedWindowHandle
            || !string.Equals(current.RuntimeId, bound.RuntimeId, StringComparison.Ordinal)
            || current.Bounds != bound.Bounds
            || !string.Equals(current.Name, bound.Name, StringComparison.Ordinal)
            || !string.Equals(current.AutomationId, bound.AutomationId, StringComparison.Ordinal)
            || !string.Equals(current.HelpText, bound.HelpText, StringComparison.Ordinal)
            || !string.Equals(current.LabeledBy, bound.LabeledBy, StringComparison.Ordinal)
            || !IsSafe(current, expectedWindowHandle))
        {
            throw Changed();
        }

        return current;
    }

    private static bool IsSafe(DesktopSearchControlSnapshot snapshot, long expectedWindowHandle) =>
        snapshot.OwnerWindowHandle == expectedWindowHandle
        && !string.IsNullOrWhiteSpace(snapshot.RuntimeId)
        && snapshot.IsEnabled
        && !snapshot.IsOffscreen
        && !snapshot.IsPassword
        && !snapshot.IsReadOnly
        && snapshot.SupportsValuePattern
        && snapshot.Bounds.IsUsable;

    private static int Score(DesktopSearchControlSnapshot snapshot) =>
        ScoreEvidence(snapshot.Name, 20)
        + ScoreEvidence(snapshot.LabeledBy, 20)
        + ScoreEvidence(snapshot.AutomationId, 10)
        + ScoreEvidence(snapshot.HelpText, 0);

    private static int ScoreEvidence(string value, int sourceBonus)
    {
        var normalized = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC);
        if (normalized.Length == 0)
        {
            return 0;
        }

        var strongPhrases = new[]
        {
            "地址和搜索栏", "地址栏", "搜索框", "搜索栏", "搜索", "查找",
            "address and search bar", "address bar", "search box", "search field", "omnibox"
        };
        if (strongPhrases.Any(phrase => normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
        {
            return 100 + sourceBonus;
        }

        var weakTerms = new[] { "address", "search", "find", "地址", "网址" };
        return weakTerms.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase))
            ? 50 + sourceBonus
            : 0;
    }

    private static bool IsVolatileAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException
            or InvalidOperationException
            or ArgumentException
            or Win32Exception
            or COMException;

    private static DesktopSearchException Changed() =>
        new(
            DesktopSearchErrorCodes.TargetChanged,
            "目标窗口或搜索控件已经变化，因此没有提交。 ");

    private static DesktopSearchException FocusChanged() =>
        new(
            DesktopSearchErrorCodes.FocusChanged,
            "搜索控件的输入焦点已经变化，因此没有提交。 ");

    private sealed record ScoredControl(DesktopSearchControl Control, int Score);
}

public interface IForegroundWindowContextProvider
{
    ForegroundWindowSnapshot? GetLastExternalWindow();

    ForegroundWindowSnapshot? ResolveWindow(long windowHandle)
    {
        var current = GetLastExternalWindow();
        return current?.WindowHandle == windowHandle ? current : null;
    }
}

public interface IReliableDesktopAutomation
{
    DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query);

    DesktopAutomationResult Describe(ForegroundWindowSnapshot expectedWindow);
}

/// <summary>
/// Remembers the most recently focused non-ScreenGuide top-level window so clicking the
/// assistant does not lose the user's intended target. No screenshot or control value is stored.
/// </summary>
public sealed class ForegroundWindowTracker : IForegroundWindowContextProvider, IDisposable
{
    private readonly System.Threading.Timer _timer;
    private ForegroundWindowSnapshot? _lastExternal;
    private int _polling;

    public ForegroundWindowTracker()
    {
        _timer = new System.Threading.Timer(
            _ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(400));
    }

    public ForegroundWindowSnapshot? GetLastExternalWindow() => Volatile.Read(ref _lastExternal);

    public ForegroundWindowSnapshot? ResolveWindow(long windowHandle) =>
        TryReadWindow(new IntPtr(windowHandle));

    public void Dispose() => _timer.Dispose();

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero || !IsWindow(handle))
            {
                return;
            }

            var snapshot = TryReadWindow(handle);
            if (snapshot is null
                || snapshot.ProcessId == Environment.ProcessId
                || snapshot.ProcessName.StartsWith("ScreenGuide.", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Volatile.Write(ref _lastExternal, snapshot);
        }
        catch (ArgumentException)
        {
            // The foreground process can exit between Win32 calls.
        }
        catch (InvalidOperationException)
        {
            // The foreground process can exit between Win32 calls.
        }
        catch (Win32Exception)
        {
            // Windows can deny process metadata while the foreground target changes.
        }
        catch (NotSupportedException)
        {
            // Some protected processes do not expose a start time.
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static ForegroundWindowSnapshot? TryReadWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            return null;
        }

        try
        {
            _ = GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0 || processId > int.MaxValue)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;
            var processStartTimeUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
            var title = GetTitle(handle).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            return new ForegroundWindowSnapshot(
                handle.ToInt64(),
                title,
                processName,
                process.Id,
                processStartTimeUtc,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidOperationException or
            Win32Exception or
            NotSupportedException)
        {
            return null;
        }
    }

    private static string GetTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[Math.Min(length + 1, 512)];
        var written = GetWindowText(handle, buffer, buffer.Length);
        return written > 0 ? new string(buffer, 0, written) : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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

public sealed class WindowsUiAutomationService : IReliableDesktopAutomation
{
    private readonly IForegroundWindowContextProvider _foregroundWindows;
    private readonly SafeForegroundSearchExecutor _searchExecutor;

    public WindowsUiAutomationService(IForegroundWindowContextProvider foregroundWindows)
        : this(foregroundWindows, new WindowsDesktopSearchAutomationDriver())
    {
    }

    internal WindowsUiAutomationService(
        IForegroundWindowContextProvider foregroundWindows,
        IDesktopSearchAutomationDriver searchDriver)
    {
        _foregroundWindows = foregroundWindows;
        _searchExecutor = new SafeForegroundSearchExecutor(foregroundWindows, searchDriver);
    }

    public DesktopAutomationResult Search(ForegroundWindowSnapshot expectedWindow, string query)
        => _searchExecutor.Search(expectedWindow, query);

    public DesktopAutomationResult Describe(ForegroundWindowSnapshot expectedWindow)
    {
        ArgumentNullException.ThrowIfNull(expectedWindow);
        RequireCurrentIdentity(expectedWindow);
        var handle = new IntPtr(expectedWindow.WindowHandle);
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            throw new InvalidOperationException("目标窗口已经关闭，请重新切回目标软件后再试。 ");
        }

        RequireCurrentIdentity(expectedWindow);
        var root = AutomationElement.FromHandle(handle)
            ?? throw new InvalidOperationException("Windows 无法读取这个窗口的控件结构。 ");
        var title = SafeCurrent(() => root.Current.Name);
        var controls = root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Select(element => new
            {
                Name = SafeCurrent(() => element.Current.Name),
                Type = SafeCurrent(() => element.Current.ControlType?.LocalizedControlType ?? string.Empty)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Take(30)
            .Select(item => $"{item.Type}“{item.Name.Trim()}”")
            .ToArray();
        var summary = controls.Length == 0
            ? $"当前窗口“{title}”没有向 Windows 公开可说明的控件名称。"
            : $"当前窗口“{title}”包含：{string.Join("、", controls)}。";
        return new DesktopAutomationResult(true, summary);
    }

    private void RequireCurrentIdentity(ForegroundWindowSnapshot expectedWindow) =>
        WindowIdentityContract.RequireMatch(
            expectedWindow,
            _foregroundWindows.ResolveWindow(expectedWindow.WindowHandle));

    private static string SafeCurrent(Func<string> getter)
    {
        try
        {
            return getter() ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

}

internal class WindowsDesktopSearchAutomationDriver : IDesktopSearchAutomationDriver
{
    public bool IsWindow(long windowHandle) =>
        windowHandle != 0 && NativeIsWindow(new IntPtr(windowHandle));

    public bool TryActivate(long windowHandle) =>
        VisibleWindowActivation.TryActivate(new IntPtr(windowHandle), TimeSpan.FromSeconds(2));

    public virtual long GetForegroundWindowHandle() => GetForegroundWindow().ToInt64();

    public IReadOnlyList<DesktopSearchControl> Discover(long windowHandle)
    {
        var root = AutomationElement.FromHandle(new IntPtr(windowHandle))
            ?? throw new ElementNotAvailableException();
        var rootRuntimeId = RuntimeId(root);
        var condition = new PropertyCondition(
            AutomationElement.ControlTypeProperty,
            ControlType.Edit);
        var elements = root.FindAll(TreeScope.Descendants, condition);
        var result = new List<DesktopSearchControl>();
        foreach (AutomationElement element in elements)
        {
            try
            {
                var token = new AutomationToken(root, rootRuntimeId, element, windowHandle);
                result.Add(new DesktopSearchControl(token, ReadToken(token)));
            }
            catch (ElementNotAvailableException)
            {
                // A disappearing control is omitted; later phases fail closed on the bound control.
            }
        }

        return result;
    }

    public DesktopSearchControlSnapshot Read(DesktopSearchControl control) =>
        ReadToken(Token(control));

    public void Focus(DesktopSearchControl control) => Token(control).Element.SetFocus();

    public void SetValue(DesktopSearchControl control, string value)
    {
        var token = Token(control);
        if (!token.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawPattern))
        {
            throw new ElementNotAvailableException();
        }

        ((ValuePattern)rawPattern).SetValue(value);
    }

    public virtual void SubmitEnter(
        DesktopSearchControl control,
        Action validateAtSendBoundary)
    {
        _ = Token(control);
        ArgumentNullException.ThrowIfNull(validateAtSendBoundary);
        validateAtSendBoundary();
        SendKeys.SendWait("{ENTER}");
    }

    private static DesktopSearchControlSnapshot ReadToken(AutomationToken token)
    {
        if (!IsDescendantOfBoundRoot(token))
        {
            throw new ElementNotAvailableException();
        }

        var element = token.Element;
        var supportsValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawPattern);
        var value = supportsValue ? ((ValuePattern)rawPattern).Current.Value ?? string.Empty : string.Empty;
        var isReadOnly = supportsValue && ((ValuePattern)rawPattern).Current.IsReadOnly;
        var bounds = element.Current.BoundingRectangle;
        return new DesktopSearchControlSnapshot(
            RuntimeId(element),
            token.WindowHandle,
            element.Current.Name ?? string.Empty,
            element.Current.AutomationId ?? string.Empty,
            element.Current.HelpText ?? string.Empty,
            element.Current.LabeledBy?.Current.Name ?? string.Empty,
            element.Current.IsEnabled,
            element.Current.IsOffscreen,
            element.Current.IsPassword,
            isReadOnly,
            supportsValue,
            element.Current.HasKeyboardFocus,
            new DesktopSearchBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            value);
    }

    private static bool IsDescendantOfBoundRoot(AutomationToken token)
    {
        if (!string.Equals(RuntimeId(token.Root), token.RootRuntimeId, StringComparison.Ordinal))
        {
            return false;
        }

        var current = token.Element;
        for (var depth = 0; current is not null && depth < 128; depth++)
        {
            if (string.Equals(RuntimeId(current), token.RootRuntimeId, StringComparison.Ordinal))
            {
                return true;
            }

            current = TreeWalker.RawViewWalker.GetParent(current);
        }

        return false;
    }

    private static AutomationToken Token(DesktopSearchControl control) =>
        control.NativeReference as AutomationToken
        ?? throw new ElementNotAvailableException();

    private static string RuntimeId(AutomationElement element) =>
        string.Join(".", element.GetRuntimeId());

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeIsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private sealed record AutomationToken(
        AutomationElement Root,
        string RootRuntimeId,
        AutomationElement Element,
        long WindowHandle);
}
