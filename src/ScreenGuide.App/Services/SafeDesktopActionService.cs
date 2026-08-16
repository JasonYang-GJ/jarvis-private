using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Win32;
using ScreenGuide.Core;
using Forms = System.Windows.Forms;

namespace ScreenGuide.App.Services;

internal sealed record DesktopActionResult(bool Succeeded, string Message);

internal sealed class SafeDesktopActionService
{
    private const string DouyinUrl = "https://www.douyin.com/";
    private const string DouyinSearchUrlPrefix = "https://www.douyin.com/search/";

    public Task<DesktopActionResult> ExecuteAsync(
        DesktopActionIntent intent,
        ForegroundWindowContext foregroundWindow,
        CancellationToken cancellationToken)
    {
        return intent.Kind switch
        {
            DesktopActionKind.OpenTarget => Task.FromResult(OpenTarget(intent)),
            DesktopActionKind.SearchForeground => Task.Run(
                () => SearchForeground(foregroundWindow, intent.Target, cancellationToken),
                cancellationToken),
            _ => Task.FromResult(new DesktopActionResult(false, "这个操作目前还没有开放。"))
        };
    }

    private static DesktopActionResult OpenTarget(DesktopActionIntent intent)
    {
        if (intent.Target.Contains("抖音", StringComparison.Ordinal))
        {
            if (intent.Browser == DesktopBrowserPreference.GoogleChrome)
            {
                return OpenChrome(DouyinUrl, "正在用谷歌浏览器打开抖音。");
            }

            var douyinShortcut = FindShortcut("抖音");
            return douyinShortcut is not null
                ? OpenShortcut(douyinShortcut, "正在打开抖音桌面版。")
                : OpenWithDefaultBrowser(DouyinUrl, "没有找到抖音桌面版，已经改用默认浏览器打开。");
        }

        if (intent.Target.Contains("谷歌", StringComparison.Ordinal)
            || intent.Target.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
        {
            return OpenChrome(null, "已经打开谷歌浏览器。 ");
        }

        return new DesktopActionResult(
            false,
            $"为了避免误开程序，我目前只认识抖音和谷歌浏览器。你说的是“{intent.Target}”。");
    }

    private static DesktopActionResult OpenWithDefaultBrowser(string url, string message)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return new DesktopActionResult(true, message);
        }
        catch
        {
            return new DesktopActionResult(false, "没有成功打开抖音，请检查默认浏览器是否可用。");
        }
    }

    private static DesktopActionResult OpenShortcut(string shortcutPath, string message)
    {
        try
        {
            Process.Start(new ProcessStartInfo(shortcutPath) { UseShellExecute = true });
            return new DesktopActionResult(true, message);
        }
        catch
        {
            return new DesktopActionResult(false, "抖音桌面版没有成功启动，请稍后重试。");
        }
    }

    private static DesktopActionResult OpenChrome(string? url, string successMessage)
    {
        var chromePath = FindChromePath();
        if (chromePath is null)
        {
            return new DesktopActionResult(false, "这台电脑上没有找到谷歌浏览器。请先确认 Chrome 已安装。");
        }

        try
        {
            Process.Start(new ProcessStartInfo(chromePath)
            {
                UseShellExecute = true,
                Arguments = url is null ? string.Empty : $"--new-tab \"{url}\""
            });
            return new DesktopActionResult(true, successMessage.Trim());
        }
        catch
        {
            return new DesktopActionResult(false, "谷歌浏览器没有成功启动，请稍后重试。");
        }
    }

    private static string? FindChromePath()
    {
        foreach (var registryView in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, registryView);
            using var appPath = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            if (appPath?.GetValue(null) is string registeredPath && File.Exists(registeredPath))
            {
                return registeredPath;
            }
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindShortcut(string displayName)
    {
        var locations = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };

        foreach (var location in locations.Where(Directory.Exists))
        {
            try
            {
                var match = Directory.EnumerateFiles(location, "*.lnk", SearchOption.AllDirectories)
                    .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path)
                        .Equals(displayName, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Continue through the other user-accessible shortcut locations.
            }
        }

        return null;
    }

    private static DesktopActionResult SearchForeground(
        ForegroundWindowContext foregroundWindow,
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!foregroundWindow.CanCapture || foregroundWindow.BelongsToCurrentProcess)
        {
            return new DesktopActionResult(false, "我没有取得你正在使用的软件窗口，请先切回要搜索的软件再说一次。");
        }

        if (IsDouyinDesktopProcess(foregroundWindow.ProcessId))
        {
            var searchUrl = DouyinSearchUrlPrefix + Uri.EscapeDataString(query);
            return OpenChrome(
                searchUrl,
                $"抖音桌面版没有向系统开放搜索框，已改用谷歌浏览器搜索“{query}”。");
        }

        var root = AutomationElement.FromHandle(foregroundWindow.WindowHandle);
        if (root is null)
        {
            return new DesktopActionResult(false, "当前软件没有提供可操作的搜索框，我没有输入任何内容。");
        }

        var editCondition = new PropertyCondition(
            AutomationElement.ControlTypeProperty,
            ControlType.Edit);
        var edits = root.FindAll(TreeScope.Descendants, editCondition);
        var searchBox = edits.Cast<AutomationElement>()
            .Select(element => new { Element = element, Score = ScoreSearchElement(element) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Element)
            .FirstOrDefault();

        if (searchBox is null
            || !searchBox.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject)
            || valuePatternObject is not ValuePattern valuePattern
            || valuePattern.Current.IsReadOnly)
        {
            return new DesktopActionResult(false, "我没有可靠地找到当前软件的搜索框，所以没有盲目打字。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!SetForegroundWindow(foregroundWindow.WindowHandle))
        {
            return new DesktopActionResult(false, "当前软件没有取得输入焦点，我没有输入任何内容。");
        }

        searchBox.SetFocus();
        valuePattern.SetValue(query);
        Forms.SendKeys.SendWait("{ENTER}");
        return new DesktopActionResult(true, $"已经在当前软件的搜索框输入“{query}”并开始搜索。");
    }

    private static bool IsDouyinDesktopProcess(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Contains("douyin", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int ScoreSearchElement(AutomationElement element)
    {
        var name = element.Current.Name ?? string.Empty;
        var automationId = element.Current.AutomationId ?? string.Empty;
        var helpText = element.Current.HelpText ?? string.Empty;
        var score = 0;
        if (name.Contains("搜索", StringComparison.OrdinalIgnoreCase)
            || name.Contains("search", StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }
        if (automationId.Contains("search", StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }
        if (helpText.Contains("搜索", StringComparison.OrdinalIgnoreCase)
            || helpText.Contains("search", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        return score;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
