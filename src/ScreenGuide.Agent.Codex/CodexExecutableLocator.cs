namespace ScreenGuide.Agent.Codex;

internal sealed class CodexExecutableLocator(CodexConnectorOptions options)
{
    public string Resolve()
    {
        if (options.ExecutablePath is { } configured)
        {
            return RequireExistingFile(configured);
        }

        foreach (var candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            $"未找到可调用的 Codex CLI。可通过 {CodexConnectorOptions.ExecutableEnvironmentVariable} 指定 codex.exe。 ");
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            foreach (var candidate in FromNpmRoot(Path.Combine(appData, "npm")))
            {
                yield return candidate;
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (var pathEntry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string fullEntry;
            try
            {
                fullEntry = Path.GetFullPath(pathEntry.Trim().Trim('"'));
            }
            catch (Exception) when (pathEntry.Length > 0)
            {
                continue;
            }

            yield return Path.Combine(fullEntry, "codex.exe");
            foreach (var candidate in FromNpmRoot(fullEntry))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> FromNpmRoot(string npmRoot)
    {
        var packageRoot = Path.Combine(
            npmRoot,
            "node_modules",
            "@openai",
            "codex",
            "node_modules");
        yield return Path.Combine(
            packageRoot,
            "@openai",
            "codex-win32-x64",
            "vendor",
            "x86_64-pc-windows-msvc",
            "bin",
            "codex.exe");
        yield return Path.Combine(
            packageRoot,
            "@openai",
            "codex-win32-arm64",
            "vendor",
            "aarch64-pc-windows-msvc",
            "bin",
            "codex.exe");
    }

    private static string RequireExistingFile(string path) =>
        File.Exists(path)
            ? path
            : throw new FileNotFoundException("配置的 Codex CLI 不存在。", path);
}
