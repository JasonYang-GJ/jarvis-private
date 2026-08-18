using System.Diagnostics;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed record ProjectInspection(bool IsGitRepository, bool HasChanges, string Summary);

public sealed class ProjectInspector
{
    public async Task<ProjectInspection> InspectAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootPath))
        {
            return new ProjectInspection(false, false, "目录不存在");
        }

        try
        {
            var inside = await RunGitAsync(
                rootPath,
                ["rev-parse", "--is-inside-work-tree"],
                cancellationToken).ConfigureAwait(false);
            if (inside.ExitCode != 0
                || !string.Equals(inside.Output.Trim(), "true", StringComparison.Ordinal))
            {
                return new ProjectInspection(false, false, "不是 Git 项目");
            }

            var status = await RunGitAsync(
                rootPath,
                ["status", "--porcelain=v1", "--untracked-files=normal"],
                cancellationToken).ConfigureAwait(false);
            if (status.ExitCode != 0)
            {
                return new ProjectInspection(true, false, "Git 状态暂时无法读取");
            }

            var count = status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            return count == 0
                ? new ProjectInspection(true, false, "工作区干净")
                : new ProjectInspection(true, true, $"有 {count} 项未提交变化");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ProjectInspection(false, false, "Git 不可用");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunGitAsync(
        string rootPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = rootPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            return (-1, string.Empty);
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false));
    }
}
