using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ScreenGuide.Skills.Windows;

public interface IInstalledApplicationCatalog
{
    IReadOnlyList<KnownDesktopApplication> GetApplications();

    KnownDesktopApplication? FindById(string id);

    KnownDesktopApplication? FindByDisplayName(string displayName);

    KnownDesktopApplication? FindBrowser(string browserName);
}

/// <summary>
/// Reads only Windows' explicit application registration locations. It never scans the disk.
/// </summary>
public sealed class InstalledApplicationCatalog : IInstalledApplicationCatalog
{
    private static readonly KnownDesktopApplication[] BuiltIns =
    [
        new("file-explorer", "文件资源管理器", "explorer.exe"),
        new("notepad", "记事本", "notepad.exe"),
        new("calculator", "计算器", "calc.exe"),
        new("paint", "画图", "mspaint.exe"),
        new("windows-settings", "Windows 设置", "ms-settings:")
    ];

    private readonly Lazy<IReadOnlyList<KnownDesktopApplication>> _applications;

    public InstalledApplicationCatalog()
    {
        _applications = new Lazy<IReadOnlyList<KnownDesktopApplication>>(Discover);
    }

    public IReadOnlyList<KnownDesktopApplication> GetApplications() => _applications.Value;

    public KnownDesktopApplication? FindById(string id) =>
        GetApplications().FirstOrDefault(item =>
            string.Equals(item.Id, id?.Trim(), StringComparison.Ordinal));

    public KnownDesktopApplication? FindByDisplayName(string displayName)
    {
        var normalized = NormalizeDisplayName(displayName);
        var exact = GetApplications().Where(item =>
            string.Equals(NormalizeDisplayName(item.DisplayName), normalized,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        return SelectUniqueApplication(exact);
    }

    internal static KnownDesktopApplication? SelectUniqueApplication(
        IReadOnlyList<KnownDesktopApplication> exact)
    {
        if (exact.Count == 1)
        {
            return exact[0];
        }

        // Windows may expose one of our built-in applications a second time through
        // AppsFolder. Prefer the deliberately allowlisted built-in in that case, but
        // keep genuinely ambiguous third-party display names blocked.
        var builtIn = exact.Where(item =>
            !item.Id.StartsWith("installed-", StringComparison.Ordinal)).ToArray();
        if (builtIn.Length == 1)
        {
            return builtIn[0];
        }

        // The same installed program is often registered both as its executable
        // and as a Start Menu shortcut. A single existing executable is the most
        // specific safe target. Multiple executable targets remain ambiguous.
        var executables = exact.Where(item =>
            item.LaunchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && Path.IsPathFullyQualified(item.LaunchTarget)
            && File.Exists(item.LaunchTarget)).ToArray();
        return executables.Length == 1 ? executables[0] : null;
    }

    public KnownDesktopApplication? FindBrowser(string browserName)
    {
        var executableName = BrowserExecutableName(browserName);
        if (executableName is null)
        {
            return null;
        }

        var matches = GetApplications()
            .Where(item => LaunchesExecutable(item.LaunchTarget, executableName))
            .OrderBy(item => item.LaunchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return matches.FirstOrDefault();
    }

    private static string? BrowserExecutableName(string browserName)
    {
        var normalized = NormalizeDisplayName(browserName);
        if (normalized.Contains("GoogleChrome", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("谷歌浏览器", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Chrome", StringComparison.OrdinalIgnoreCase))
        {
            return "chrome.exe";
        }

        if (normalized.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("微软浏览器", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Edge", StringComparison.OrdinalIgnoreCase))
        {
            return "msedge.exe";
        }

        return null;
    }

    private static bool LaunchesExecutable(string launchTarget, string executableName) =>
        string.Equals(Path.GetFileName(launchTarget.Trim().Trim('"')), executableName,
            StringComparison.OrdinalIgnoreCase)
        || launchTarget.Contains(executableName, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<KnownDesktopApplication> Discover()
    {
        var byTarget = new Dictionary<string, KnownDesktopApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var builtIn in BuiltIns)
        {
            byTarget[builtIn.LaunchTarget] = builtIn;
        }

        ReadAppsFolder(byTarget);

        foreach (var root in ShortcutRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var shortcut in Directory.EnumerateFiles(root, "*.lnk",
                             SearchOption.AllDirectories))
                {
                    var displayName = Path.GetFileNameWithoutExtension(shortcut).Trim();
                    AddCandidate(byTarget, displayName, shortcut);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // A protected Start Menu subtree must not break the safe catalog.
            }
            catch (IOException)
            {
                // A shortcut can disappear while Windows updates the Start Menu.
            }
        }

        ReadAppPaths(Registry.CurrentUser, byTarget);
        ReadAppPaths(Registry.LocalMachine, byTarget);
        return byTarget.Values
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ShortcutRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static void ReadAppsFolder(
        IDictionary<string, KnownDesktopApplication> applications)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        object? shell = null;
        object? folder = null;
        object? items = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return;
            }

            dynamic shellObject = shell;
            folder = shellObject.NameSpace("shell:AppsFolder");
            if (folder is null)
            {
                return;
            }

            dynamic folderObject = folder;
            items = folderObject.Items();
            dynamic itemCollection = items;
            for (var index = 0; index < (int)itemCollection.Count; index++)
            {
                object? rawItem = null;
                try
                {
                    rawItem = itemCollection.Item(index);
                    if (rawItem is null)
                    {
                        continue;
                    }

                    dynamic item = rawItem;
                    var displayName = (item.Name as string)?.Trim();
                    var shellPath = (item.Path as string)?.Trim();
                    var executable = (item.ExtendedProperty(
                        "System.Link.TargetParsingPath") as string)?.Trim();
                    var launchTarget = !string.IsNullOrWhiteSpace(executable)
                        ? executable
                        : string.IsNullOrWhiteSpace(shellPath)
                            ? null
                            : $"shell:AppsFolder\\{shellPath}";
                    if (!string.IsNullOrWhiteSpace(displayName)
                        && !string.IsNullOrWhiteSpace(launchTarget))
                    {
                        AddCandidate(applications, displayName, launchTarget);
                    }
                }
                finally
                {
                    ReleaseComObject(rawItem);
                }
            }
        }
        catch (COMException)
        {
            // AppsFolder discovery is optional; shortcuts and App Paths remain available.
        }
        catch (UnauthorizedAccessException)
        {
            // Windows can restrict individual shell namespaces.
        }
        finally
        {
            ReleaseComObject(items);
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private static void ReadAppPaths(
        RegistryKey hive,
        IDictionary<string, KnownDesktopApplication> applications)
    {
        const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        try
        {
            using var root = hive.OpenSubKey(appPaths);
            if (root is null)
            {
                return;
            }

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                var target = subKey?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                AddCandidate(applications, Path.GetFileNameWithoutExtension(subKeyName), target);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Registry discovery is optional; built-ins and shortcuts remain available.
        }
        catch (System.Security.SecurityException)
        {
            // Registry discovery is optional.
        }
    }

    private static void AddCandidate(
        IDictionary<string, KnownDesktopApplication> applications,
        string displayName,
        string target)
    {
        displayName = displayName.Trim();
        target = target.Trim().Trim('"');
        if (displayName.Length is < 1 or > 120 || target.Length is < 1 or > 1024)
        {
            return;
        }

        var lowered = displayName.ToLowerInvariant();
        if (lowered.Contains("uninstall", StringComparison.Ordinal)
            || lowered.Contains("卸载", StringComparison.Ordinal))
        {
            return;
        }

        applications.TryAdd(target, new KnownDesktopApplication(
            "installed-" + StableId(target), displayName, target));
    }

    private static string NormalizeDisplayName(string value) =>
        (value ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string StableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
