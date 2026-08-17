using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenGuide.Core.Tasking;

namespace ScreenGuide.Evidence;

public sealed class GitEvidenceCollector(EvidenceOptions options, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task CaptureBaselineAsync(
        Guid taskId,
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectRoot);
        var baselineDirectory = options.BaselineDirectory(taskId);
        if (File.Exists(Path.Combine(baselineDirectory, "manifest.json")))
        {
            return;
        }

        Directory.CreateDirectory(baselineDirectory);
        var gitCheck = await RunGitAsync(
                root,
                ["rev-parse", "--is-inside-work-tree"],
                cancellationToken)
            .ConfigureAwait(false);
        if (gitCheck.ExitCode != 0
            || !string.Equals(gitCheck.StandardOutput.Trim(), "true", StringComparison.Ordinal))
        {
            await SaveManifestAsync(
                    baselineDirectory,
                    new GitBaselineManifest(
                        taskId,
                        root,
                        timeProvider.GetUtcNow(),
                        false,
                        [],
                        [],
                        [],
                        "项目不是 Git 工作区。"),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var status = await ReadStatusAsync(root, cancellationToken).ConfigureAwait(false);
        var paths = await ReadGitPathsAsync(root, cancellationToken).ConfigureAwait(false);
        var files = new List<GitBaselineFile>();
        long snapshotBytes = 0;
        foreach (var relativePath in paths)
        {
            var fullPath = ResolvePath(root, relativePath);
            if (!File.Exists(fullPath)
                || File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var info = new FileInfo(fullPath);
            var hash = await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
            string? snapshotPath = null;
            if (info.Length <= options.MaximumSnapshotFileBytes
                && snapshotBytes + info.Length <= options.MaximumSnapshotTotalBytes)
            {
                snapshotPath = Path.Combine("files", relativePath.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(baselineDirectory, snapshotPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(fullPath, destination, overwrite: true);
                snapshotBytes += info.Length;
            }

            files.Add(
                new GitBaselineFile(
                    relativePath,
                    hash,
                    info.Length,
                    status.TryGetValue(relativePath, out var code) ? code : null,
                    snapshotPath));
        }

        await SaveManifestAsync(
                baselineDirectory,
                new GitBaselineManifest(
                    taskId,
                    root,
                    timeProvider.GetUtcNow(),
                    true,
                    status.Keys.Order(StringComparer.Ordinal).ToArray(),
                    status.Select(item => new GitStatusEvidence
                    {
                        RelativePath = item.Key,
                        StatusCode = item.Value
                    })
                        .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                        .ToArray(),
                    files,
                    null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<GitTaskEvidence> CompleteAsync(
        Guid taskId,
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var baselineDirectory = options.BaselineDirectory(taskId);
        var manifestPath = Path.Combine(baselineDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return Unavailable("任务开始前没有保存 Git 基线。", timeProvider.GetUtcNow());
        }

        var manifest = JsonSerializer.Deserialize<GitBaselineManifest>(
                           await File.ReadAllTextAsync(manifestPath, cancellationToken)
                               .ConfigureAwait(false),
                           JsonOptions)
                       ?? throw new InvalidDataException("Git 基线清单无效。 ");
        var completedAt = timeProvider.GetUtcNow();
        if (!manifest.IsGitRepository)
        {
            Cleanup(baselineDirectory);
            return new GitTaskEvidence
            {
                IsGitRepository = false,
                BeforeCapturedAtUtc = manifest.CapturedAtUtc,
                AfterCapturedAtUtc = completedAt,
                PreExistingChangedFiles = manifest.PreExistingChangedFiles,
                BeforeStatus = manifest.BeforeStatus,
                AfterStatus = [],
                ChangedFiles = [],
                DiffStatVerified = false,
                UnavailableReason = manifest.UnavailableReason
            };
        }

        var root = Path.GetFullPath(projectRoot);
        var gitCheck = await RunGitAsync(
                root,
                ["rev-parse", "--is-inside-work-tree"],
                cancellationToken)
            .ConfigureAwait(false);
        if (gitCheck.ExitCode != 0)
        {
            Cleanup(baselineDirectory);
            return Unavailable("任务结束时 Git 工作区不可用。", completedAt, manifest);
        }

        var afterPaths = await ReadGitPathsAsync(root, cancellationToken).ConfigureAwait(false);
        var afterStatus = await ReadStatusAsync(root, cancellationToken).ConfigureAwait(false);
        var after = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relativePath in afterPaths)
        {
            var fullPath = ResolvePath(root, relativePath);
            if (File.Exists(fullPath)
                && !File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
            {
                after[relativePath] = await HashFileAsync(fullPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var before = manifest.Files.ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
        var changedPaths = before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(path => !before.TryGetValue(path, out var oldFile)
                           || !after.TryGetValue(path, out var newHash)
                           || !string.Equals(oldFile.Hash, newHash, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var changes = new List<EvidenceFileChange>();
        var allStatsVerified = true;
        foreach (var path in changedPaths)
        {
            before.TryGetValue(path, out var oldFile);
            after.TryGetValue(path, out var newHash);
            var type = oldFile is null
                ? EvidenceFileChangeType.Added
                : newHash is null
                    ? EvidenceFileChangeType.Deleted
                    : EvidenceFileChangeType.Modified;
            var stats = await ReadDiffStatAsync(
                    root,
                    baselineDirectory,
                    path,
                    oldFile,
                    newHash is not null,
                    cancellationToken)
                .ConfigureAwait(false);
            allStatsVerified &= stats.Verified;
            changes.Add(
                new EvidenceFileChange
                {
                    RelativePath = path,
                    ChangeType = type,
                    HadPreExistingChanges = oldFile?.PreExistingStatus is not null,
                    MixedWithPreExistingChanges = oldFile?.PreExistingStatus is not null,
                    BeforeHash = oldFile?.Hash,
                    AfterHash = newHash,
                    AddedLines = stats.AddedLines,
                    DeletedLines = stats.DeletedLines,
                    IsBinary = stats.IsBinary
                });
        }

        Cleanup(baselineDirectory);
        return new GitTaskEvidence
        {
            IsGitRepository = true,
            BeforeCapturedAtUtc = manifest.CapturedAtUtc,
            AfterCapturedAtUtc = completedAt,
            PreExistingChangedFiles = manifest.PreExistingChangedFiles,
            BeforeStatus = manifest.BeforeStatus,
            AfterStatus = afterStatus.Select(item => new GitStatusEvidence
            {
                RelativePath = item.Key,
                StatusCode = item.Value
            })
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            ChangedFiles = changes,
            AddedFileCount = changes.Count(item => item.ChangeType == EvidenceFileChangeType.Added),
            ModifiedFileCount = changes.Count(item => item.ChangeType == EvidenceFileChangeType.Modified),
            DeletedFileCount = changes.Count(item => item.ChangeType == EvidenceFileChangeType.Deleted),
            AddedLineCount = allStatsVerified ? changes.Sum(item => item.AddedLines ?? 0) : null,
            DeletedLineCount = allStatsVerified ? changes.Sum(item => item.DeletedLines ?? 0) : null,
            BinaryFileCount = changes.Count(item => item.IsBinary),
            DiffStatVerified = allStatsVerified,
            UnavailableReason = allStatsVerified ? null : "部分文件超过证据快照限制，行级 diff 未完全验证。"
        };
    }

    private async Task<DiffStat> ReadDiffStatAsync(
        string root,
        string baselineDirectory,
        string relativePath,
        GitBaselineFile? before,
        bool existsAfter,
        CancellationToken cancellationToken)
    {
        var empty = Path.Combine(baselineDirectory, "empty");
        if (!File.Exists(empty))
        {
            await File.WriteAllBytesAsync(empty, [], cancellationToken).ConfigureAwait(false);
        }

        var beforePath = before?.SnapshotPath is null
            ? before is null ? empty : null
            : Path.Combine(baselineDirectory, before.SnapshotPath);
        if (beforePath is null || !File.Exists(beforePath))
        {
            return new DiffStat(false, null, null, false);
        }

        var afterPath = existsAfter ? ResolvePath(root, relativePath) : empty;
        var result = await RunGitAsync(
                root,
                ["diff", "--no-index", "--numstat", "--", beforePath, afterPath],
                cancellationToken,
                allowDifferenceExitCode: true)
            .ConfigureAwait(false);
        var line = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (line is null)
        {
            return new DiffStat(result.ExitCode is 0 or 1, 0, 0, false);
        }

        var parts = line.Split('\t');
        if (parts.Length < 2)
        {
            return new DiffStat(false, null, null, false);
        }

        if (parts[0] == "-" || parts[1] == "-")
        {
            return new DiffStat(true, null, null, true);
        }

        return int.TryParse(parts[0], out var added)
               && int.TryParse(parts[1], out var deleted)
            ? new DiffStat(true, added, deleted, false)
            : new DiffStat(false, null, null, false);
    }

    private static async Task<Dictionary<string, string>> ReadStatusAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
                root,
                ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(result, "读取 Git 状态");
        var status = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.Length >= 4)
            {
                status[NormalizePath(record[3..])] = record[..2];
            }
        }

        return status;
    }

    private static async Task<IReadOnlyList<string>> ReadGitPathsAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
                root,
                ["ls-files", "-co", "--exclude-standard", "-z"],
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(result, "读取 Git 文件列表");
        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string root,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowDifferenceExitCode = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = root,
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
            throw new InvalidOperationException("Git 证据命令无法启动。 ");
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var result = new GitCommandResult(
            process.ExitCode,
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
        if (!allowDifferenceExitCode && result.ExitCode != 0 && arguments[0] != "rev-parse")
        {
            RequireSuccess(result, $"执行 git {arguments[0]}");
        }

        return result;
    }

    private static void RequireSuccess(GitCommandResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{operation}失败，退出码 {result.ExitCode}：{Sanitize(result.StandardError)}");
        }
    }

    private static string ResolvePath(string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Git 返回了项目目录之外的路径。 ");
        }

        return fullPath;
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static Task SaveManifestAsync(
        string directory,
        GitBaselineManifest manifest,
        CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken);

    private static GitTaskEvidence Unavailable(
        string reason,
        DateTimeOffset now,
        GitBaselineManifest? manifest = null) =>
        new()
        {
            IsGitRepository = manifest?.IsGitRepository ?? false,
            BeforeCapturedAtUtc = manifest?.CapturedAtUtc ?? now,
            AfterCapturedAtUtc = now,
            PreExistingChangedFiles = manifest?.PreExistingChangedFiles ?? [],
            BeforeStatus = manifest?.BeforeStatus ?? [],
            AfterStatus = [],
            ChangedFiles = [],
            DiffStatVerified = false,
            UnavailableReason = reason
        };

    private void Cleanup(string directory)
    {
        var evidenceRoot = Path.GetFullPath(options.DataDirectory);
        var target = Path.GetFullPath(directory);
        var prefix = evidenceRoot.EndsWith(Path.DirectorySeparatorChar)
            ? evidenceRoot
            : evidenceRoot + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("拒绝清理证据数据目录之外的快照。 ");
        }

        if (Directory.Exists(target))
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(target, recursive: true);
        }
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/');

    private static string Sanitize(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }

    private sealed record GitBaselineManifest(
        Guid TaskId,
        string ProjectRoot,
        DateTimeOffset CapturedAtUtc,
        bool IsGitRepository,
        IReadOnlyList<string> PreExistingChangedFiles,
        IReadOnlyList<GitStatusEvidence> BeforeStatus,
        IReadOnlyList<GitBaselineFile> Files,
        string? UnavailableReason);

    private sealed record GitBaselineFile(
        string RelativePath,
        string Hash,
        long Length,
        string? PreExistingStatus,
        string? SnapshotPath);

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record DiffStat(bool Verified, int? AddedLines, int? DeletedLines, bool IsBinary);
}
