using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using ScreenGuide.Core;

namespace ScreenGuide.App.Services;

internal sealed record InstalledApplicationEntry(string DisplayName, string LaunchPath, string? Arguments = null);

internal sealed class InstalledApplicationCatalog
{
    private readonly Lazy<IReadOnlyList<InstalledApplicationEntry>> _entries = new(DiscoverApplications);

    private static readonly IReadOnlyDictionary<string, InstalledApplicationEntry> SystemApplications =
        new Dictionary<string, InstalledApplicationEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["此电脑"] = new("此电脑", "explorer.exe", "shell:MyComputerFolder"),
            ["我的电脑"] = new("此电脑", "explorer.exe", "shell:MyComputerFolder"),
            ["文件资源管理器"] = new("文件资源管理器", "explorer.exe"),
            ["资源管理器"] = new("文件资源管理器", "explorer.exe"),
            ["记事本"] = new("记事本", "notepad.exe"),
            ["计算器"] = new("计算器", "calc.exe"),
            ["画图"] = new("画图", "mspaint.exe"),
            ["任务管理器"] = new("任务管理器", "taskmgr.exe"),
            ["控制面板"] = new("控制面板", "control.exe"),
            ["Windows设置"] = new("Windows 设置", "ms-settings:"),
            ["系统设置"] = new("Windows 设置", "ms-settings:")
        };

    public bool TryFind(string requestedName, out InstalledApplicationEntry? entry)
    {
        entry = null;
        var requested = requestedName.Trim();
        if (SystemApplications.TryGetValue(requested, out var systemEntry))
        {
            entry = systemEntry;
            return true;
        }

        var alias = requested switch
        {
            "谷歌" or "谷歌浏览器" or "Chrome" or "chrome" => "Google Chrome",
            "Edge" or "edge" or "微软浏览器" => "Microsoft Edge",
            "剪映" => "剪映专业版",
            _ => requested
        };

        entry = _entries.Value
            .Select(candidate => new
            {
                Entry = candidate,
                Score = ApplicationNameMatcher.Score(alias, candidate.DisplayName)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Entry.DisplayName.Length)
            .Select(candidate => candidate.Entry)
            .FirstOrDefault();
        return entry is not null;
    }

    public DesktopActionResult Launch(string requestedName)
    {
        if (!TryFind(requestedName, out var entry) || entry is null)
        {
            return new DesktopActionResult(
                false,
                $"没有在桌面、开始菜单或系统应用中找到“{requestedName}”。你可以告诉我它的完整名称。 ");
        }

        try
        {
            Process.Start(new ProcessStartInfo(entry.LaunchPath)
            {
                UseShellExecute = true,
                Arguments = entry.Arguments ?? string.Empty
            });
            return new DesktopActionResult(true, $"正在打开{entry.DisplayName}。 ");
        }
        catch (Exception exception)
        {
            return new DesktopActionResult(false, $"找到了{entry.DisplayName}，但启动失败：{exception.Message}");
        }
    }

    private static IReadOnlyList<InstalledApplicationEntry> DiscoverApplications()
    {
        var locations = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        };
        var entries = new List<InstalledApplicationEntry>();
        foreach (var location in locations.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(location, "*.*", SearchOption.AllDirectories)
                             .Where(path => IsShortcutExtension(Path.GetExtension(path))))
                {
                    entries.Add(new InstalledApplicationEntry(Path.GetFileNameWithoutExtension(path), path));
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Continue through all user-accessible shortcut locations.
            }
            catch (IOException)
            {
                // A shortcut folder can change while the Start menu is being updated.
            }
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                    if (appPaths is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in appPaths.GetSubKeyNames())
                    {
                        using var appKey = appPaths.OpenSubKey(subKeyName);
                        if (appKey?.GetValue(null) is not string executablePath || !File.Exists(executablePath))
                        {
                            continue;
                        }
                        entries.Add(new InstalledApplicationEntry(
                            Path.GetFileNameWithoutExtension(subKeyName),
                            executablePath));
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // A registry view can be protected; the Start menu catalog remains available.
                }
            }
        }

        return entries
            .GroupBy(entry => $"{entry.DisplayName}\0{entry.LaunchPath}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool IsShortcutExtension(string extension) =>
        extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase);
}
