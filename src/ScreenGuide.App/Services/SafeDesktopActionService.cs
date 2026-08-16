using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Specialized;
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
    private readonly InstalledApplicationCatalog _applications = new();

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
            DesktopActionKind.PrepareFirstImage => Task.FromResult(PrepareFirstImage(intent.Target)),
            DesktopActionKind.InvokeForeground => Task.Run(
                () => InvokeForeground(foregroundWindow, intent.Target, cancellationToken),
                cancellationToken),
            _ => Task.FromResult(new DesktopActionResult(false, "这个操作目前还没有开放。"))
        };
    }

    private DesktopActionResult OpenTarget(DesktopActionIntent intent)
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

        return _applications.Launch(intent.Target);
    }

    private static DesktopActionResult PrepareFirstImage(string location)
    {
        var root = location switch
        {
            "桌面" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "下载" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            _ => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (!Directory.Exists(root))
        {
            return new DesktopActionResult(false, $"没有找到{location}对应的文件夹。 ");
        }

        string? firstImage;
        try
        {
            firstImage = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(path => IsImageExtension(Path.GetExtension(path)))
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return new DesktopActionResult(false, $"没有权限读取{root}中的部分文件，所以没有选择照片。 ");
        }
        catch (IOException exception)
        {
            return new DesktopActionResult(false, $"读取图片文件夹失败：{exception.Message}");
        }

        if (firstImage is null)
        {
            return new DesktopActionResult(false, $"在{root}中没有找到常见格式的照片。 ");
        }

        try
        {
            var files = new StringCollection { firstImage };
            Forms.Clipboard.SetFileDropList(files);
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                Arguments = $"/select,\"{firstImage}\""
            });
            return new DesktopActionResult(
                true,
                $"已在资源管理器中选中并复制第一张照片“{Path.GetFileName(firstImage)}”。因为你没有指定发送到哪个软件和联系人，我没有自动发出；现在可在目标聊天框按 Ctrl 加 V。 ");
        }
        catch (Exception exception)
        {
            return new DesktopActionResult(false, $"找到了照片，但没有成功选中或复制：{exception.Message}");
        }
    }

    private static bool IsImageExtension(string extension) =>
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);

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

    private static DesktopActionResult InvokeForeground(
        ForegroundWindowContext foregroundWindow,
        string target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!foregroundWindow.CanCapture || foregroundWindow.BelongsToCurrentProcess)
        {
            return new DesktopActionResult(false, "我没有取得你正在使用的软件窗口，请先切回目标软件再说一次。 ");
        }

        var root = AutomationElement.FromHandle(foregroundWindow.WindowHandle);
        if (root is null)
        {
            return new DesktopActionResult(false, "当前软件没有向 Windows 提供可操作的按钮或选项。 ");
        }

        var normalizedTarget = ApplicationNameMatcher.Normalize(target);
        var controls = root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(element => element.Current.IsEnabled && !element.Current.IsOffscreen)
            .Select(element => new
            {
                Element = element,
                Name = element.Current.Name ?? string.Empty,
                Score = ScoreActionElement(element, normalizedTarget)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();

        foreach (var candidate in controls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObject)
                && invokeObject is InvokePattern invokePattern)
            {
                if (!SetForegroundWindow(foregroundWindow.WindowHandle))
                {
                    return new DesktopActionResult(false, "目标软件没有取得焦点，所以没有执行点击。 ");
                }
                invokePattern.Invoke();
                return new DesktopActionResult(true, $"已经点击“{candidate.Name}”。 ");
            }

            if (candidate.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionObject)
                && selectionObject is SelectionItemPattern selectionPattern)
            {
                if (!SetForegroundWindow(foregroundWindow.WindowHandle))
                {
                    return new DesktopActionResult(false, "目标软件没有取得焦点，所以没有执行选择。 ");
                }
                selectionPattern.Select();
                return new DesktopActionResult(true, $"已经选择“{candidate.Name}”。 ");
            }
        }

        return new DesktopActionResult(
            false,
            $"当前软件里没有找到名称可靠匹配“{target}”的可用按钮或选项，所以没有盲目点击。 ");
    }

    private static int ScoreActionElement(AutomationElement element, string normalizedTarget)
    {
        var controlType = element.Current.ControlType;
        if (controlType != ControlType.Button
            && controlType != ControlType.MenuItem
            && controlType != ControlType.ListItem
            && controlType != ControlType.Hyperlink
            && controlType != ControlType.TabItem)
        {
            return 0;
        }

        var normalizedName = ApplicationNameMatcher.Normalize(element.Current.Name);
        if (normalizedName.Length == 0)
        {
            return 0;
        }
        if (string.Equals(normalizedName, normalizedTarget, StringComparison.Ordinal))
        {
            return 100;
        }
        if (normalizedName.Contains(normalizedTarget, StringComparison.Ordinal))
        {
            return 70;
        }
        return 0;
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
