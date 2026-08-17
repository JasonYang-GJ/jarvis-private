namespace ScreenGuide.Core.Tasking;

public static class ProjectPathPolicy
{
    public static string NormalizeExistingRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("项目目录不能为空。", nameof(rootPath));
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath.Trim()));
        if (!Path.IsPathFullyQualified(normalized))
        {
            throw new ArgumentException("项目目录必须是绝对路径。", nameof(rootPath));
        }

        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"项目目录不存在：{normalized}");
        }

        EnsureNoReparsePoints(normalized);
        return normalized;
    }

    public static string ResolveWithinRoot(string authorizedRoot, string? relativePath)
    {
        var normalizedRoot = NormalizeExistingRoot(authorizedRoot);
        var requestedPath = string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath.Trim();
        if (Path.IsPathFullyQualified(requestedPath))
        {
            throw new UnauthorizedAccessException("任务工作目录必须使用相对于授权项目的路径。");
        }

        var resolved = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(normalizedRoot, requestedPath)));
        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        var isRoot = string.Equals(resolved, normalizedRoot, StringComparison.OrdinalIgnoreCase);
        var isChild = resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        if (!isRoot && !isChild)
        {
            throw new UnauthorizedAccessException("任务工作目录超出了授权项目范围。");
        }

        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"任务工作目录不存在：{resolved}");
        }

        EnsureNoReparsePoints(resolved);
        return resolved;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        DirectoryInfo? current = new(path);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    $"授权目录路径不能包含符号链接或目录联接：{current.FullName}");
            }

            current = current.Parent;
        }
    }
}
