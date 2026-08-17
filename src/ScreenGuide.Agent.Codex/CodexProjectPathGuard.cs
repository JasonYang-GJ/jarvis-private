namespace ScreenGuide.Agent.Codex;

internal static class CodexProjectPathGuard
{
    public static (string ProjectRoot, string WorkingDirectory) Validate(
        string projectRootPath,
        string workingDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(projectRootPath)
            || string.IsNullOrWhiteSpace(workingDirectoryPath))
        {
            throw new UnauthorizedAccessException("Codex 项目路径不能为空。");
        }

        var root = NormalizeExistingDirectory(projectRootPath);
        var working = NormalizeExistingDirectory(workingDirectoryPath);
        var relative = Path.GetRelativePath(root, working);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Codex 工作目录不在授权项目内。");
        }

        EnsureNoReparsePoints(root, working);
        return (root, working);
    }

    private static string NormalizeExistingDirectory(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"目录不存在：{fullPath}");
        }

        return fullPath;
    }

    private static void EnsureNoReparsePoints(string root, string working)
    {
        var current = new DirectoryInfo(working);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Codex 工作目录路径不能包含符号链接或目录联接。");
            }

            if (string.Equals(current.FullName, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = current.Parent;
        }

        throw new UnauthorizedAccessException("Codex 工作目录无法追溯到授权项目根目录。");
    }
}
