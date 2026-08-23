using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ScreenGuide.Skills.Windows;

public sealed record ForegroundWindowSnapshot(
    long WindowHandle,
    string WindowTitle,
    string ProcessName,
    DateTimeOffset ObservedAtUtc);

public sealed record DesktopAutomationResult(bool Verified, string Summary, string? TechnicalDetail = null);

public interface IForegroundWindowContextProvider
{
    ForegroundWindowSnapshot? GetLastExternalWindow();
}

public interface IReliableDesktopAutomation
{
    DesktopAutomationResult Search(long windowHandle, string query);

    DesktopAutomationResult Describe(long windowHandle);
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

            _ = GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0)
            {
                return;
            }

            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;
            if (process.Id == Environment.ProcessId
                || processName.StartsWith("ScreenGuide.", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var title = GetTitle(handle);
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            Volatile.Write(ref _lastExternal, new ForegroundWindowSnapshot(
                handle.ToInt64(), title.Trim(), processName, DateTimeOffset.UtcNow));
        }
        catch (ArgumentException)
        {
            // The foreground process can exit between Win32 calls.
        }
        catch (InvalidOperationException)
        {
            // The foreground process can exit between Win32 calls.
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
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
    private static readonly string[] SearchTerms =
    [
        "搜索", "查找", "搜索框", "地址", "网址", "search", "find", "address"
    ];

    public DesktopAutomationResult Search(long windowHandle, string query)
    {
        query = (query ?? string.Empty).Trim();
        if (query.Length is < 1 or > 200)
        {
            throw new ArgumentException("搜索内容应为 1 到 200 个字符。", nameof(query));
        }

        var handle = new IntPtr(windowHandle);
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            throw new InvalidOperationException("目标窗口已经关闭，请重新切回目标软件后再试。 ");
        }

        var root = AutomationElement.FromHandle(handle)
            ?? throw new InvalidOperationException("Windows 无法读取这个窗口的控件结构。 ");
        var candidates = FindWritableSearchBoxes(root);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("没有找到可可靠识别的搜索框，因此没有输入任何内容。 ");
        }

        var bestScore = candidates.Max(item => item.Score);
        var best = candidates.Where(item => item.Score == bestScore).ToArray();
        if (bestScore < 2 || best.Length != 1)
        {
            throw new InvalidOperationException("窗口中存在多个相似输入框，无法安全确定搜索目标。 ");
        }

        var candidate = best[0];
        if (!VisibleWindowActivation.TryActivate(handle, TimeSpan.FromSeconds(2)))
        {
            throw new InvalidOperationException("无法把目标软件切到前台，因此没有输入任何内容。 ");
        }

        candidate.Element.SetFocus();
        candidate.Pattern.SetValue(query);
        if (!string.Equals(candidate.Pattern.Current.Value, query, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("搜索内容没有被可靠写入，因此没有提交。 ");
        }

        SendKeys.SendWait("{ENTER}");
        return new DesktopAutomationResult(
            true,
            "已确认内容写入唯一识别的搜索框并提交；搜索结果页面尚未验证。",
            $"Control={candidate.Name}; AutomationId={candidate.AutomationId}");
    }

    public DesktopAutomationResult Describe(long windowHandle)
    {
        var handle = new IntPtr(windowHandle);
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            throw new InvalidOperationException("目标窗口已经关闭，请重新切回目标软件后再试。 ");
        }

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

    private static List<SearchCandidate> FindWritableSearchBoxes(AutomationElement root)
    {
        var condition = new PropertyCondition(
            AutomationElement.ControlTypeProperty,
            ControlType.Edit);
        var elements = root.FindAll(TreeScope.Descendants, condition);
        var candidates = new List<SearchCandidate>();
        foreach (AutomationElement element in elements)
        {
            try
            {
                if (!element.Current.IsEnabled
                    || element.Current.IsPassword
                    || !element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawPattern))
                {
                    continue;
                }

                var pattern = (ValuePattern)rawPattern;
                if (pattern.Current.IsReadOnly)
                {
                    continue;
                }

                var name = element.Current.Name ?? string.Empty;
                var automationId = element.Current.AutomationId ?? string.Empty;
                var helpText = element.Current.HelpText ?? string.Empty;
                var searchableText = $"{name} {automationId} {helpText}";
                var score = SearchTerms.Count(term =>
                    searchableText.Contains(term, StringComparison.OrdinalIgnoreCase));
                if (score > 0)
                {
                    candidates.Add(new SearchCandidate(element, pattern, score, name, automationId));
                }
            }
            catch (ElementNotAvailableException)
            {
                // Ignore a control that disappears during enumeration.
            }
        }

        return candidates;
    }

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

    private sealed record SearchCandidate(
        AutomationElement Element,
        ValuePattern Pattern,
        int Score,
        string Name,
        string AutomationId);
}
