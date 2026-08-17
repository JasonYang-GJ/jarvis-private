using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenGuide.Core.Tasking;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.Evidence;

public sealed partial class TaskEvidenceService(
    ILocalTaskStore store,
    GitEvidenceCollector gitCollector,
    EvidenceOptions options,
    TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public Task CaptureBaselineAsync(
        AgentTask task,
        ProjectRecord project,
        CancellationToken cancellationToken = default) =>
        gitCollector.CaptureBaselineAsync(task.Id, project.RootPath, cancellationToken);

    public async Task<TaskEvidence?> FinalizeIfTerminalAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false)
                   ?? throw new KeyNotFoundException("找不到需要生成证据的任务。 ");
        if (task.Status is AgentTaskStatus.Pending
            or AgentTaskStatus.Running
            or AgentTaskStatus.WaitingForUser
            or AgentTaskStatus.CancellationRequested)
        {
            return null;
        }

        return await FinalizeAsync(task, cancellationToken).ConfigureAwait(false);
    }

    public async Task FinalizeRecoveredTasksAsync(
        IReadOnlyList<Guid> taskIds,
        CancellationToken cancellationToken = default)
    {
        foreach (var taskId in taskIds)
        {
            _ = await FinalizeIfTerminalAsync(taskId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TaskEvidence> FinalizeAsync(
        AgentTask task,
        CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(task.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await store.GetTaskEvidenceAsync(task.Id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            var project = await store.GetProjectAsync(task.ProjectId, cancellationToken)
                              .ConfigureAwait(false)
                          ?? throw new InvalidOperationException("证据任务关联的 Project 不存在。 ");
            var run = await store.GetAgentRunByTaskAsync(task.Id, cancellationToken)
                .ConfigureAwait(false);
            var events = await store.GetTaskEventsAsync(task.Id, cancellationToken)
                .ConfigureAwait(false);
            GitTaskEvidence git;
            try
            {
                git = await gitCollector.CompleteAsync(
                        task.Id,
                        project.RootPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var now = timeProvider.GetUtcNow();
                git = new GitTaskEvidence
                {
                    IsGitRepository = false,
                    BeforeCapturedAtUtc = task.CreatedAtUtc,
                    AfterCapturedAtUtc = now,
                    PreExistingChangedFiles = [],
                    BeforeStatus = [],
                    AfterStatus = [],
                    ChangedFiles = [],
                    DiffStatVerified = false,
                    UnavailableReason = $"Git 证据采集失败：{Sanitize(exception.Message)}"
                };
            }

            var claim = ReadClaim(run, task.Status);
            var tests = ReadTestEvidence(events);
            var connector = ReadConnectorEvidence(run, events);
            var actualFiles = git.ChangedFiles.Select(item => NormalizePath(item.RelativePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var claimedFiles = claim.ClaimedChangedFiles.Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fileClaimMismatch = claim.Status == AgentClaimStatus.Completed
                                    && !actualFiles.SetEquals(claimedFiles);
            var testClaimContradiction = tests.Status == EvidenceTestStatus.Failed
                                         && claim.ClaimedTests.Count > 0
                                         && run?.FinalResultJson?.Contains(
                                             "\"passed\"",
                                             StringComparison.OrdinalIgnoreCase) == true;
            var contradiction = fileClaimMismatch || testClaimContradiction;
            var reasons = BuildReasons(task, git, tests, connector, contradiction, fileClaimMismatch);
            var verification = DetermineVerification(task.Status, git, tests, contradiction);
            var summary = TaskEvidenceSummaryGenerator.Generate(
                task.Status,
                git,
                tests,
                verification);
            var evidence = new TaskEvidence
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                GeneratedAtUtc = timeProvider.GetUtcNow(),
                TaskStatus = task.Status,
                AgentClaim = claim,
                Git = git,
                Tests = tests,
                Connector = connector,
                VerificationStatus = verification,
                AgentClaimContradictedByEvidence = contradiction,
                VerificationReasons = reasons,
                UserSummary = summary
            };
            await store.UpsertTaskEvidenceAsync(evidence, cancellationToken).ConfigureAwait(false);
            return evidence;
        }
        finally
        {
            gate.Release();
        }
    }

    private ConnectorCompatibilityEvidence ReadConnectorEvidence(
        AgentRunRecord? run,
        IReadOnlyList<TaskEventRecord> events)
    {
        var version = run?.ConnectorVersion ?? FindDetectedVersion(events);
        var verified = version is not null
                       && options.VerifiedCodexVersions.Contains(
                           version,
                           StringComparer.OrdinalIgnoreCase);
        return new ConnectorCompatibilityEvidence
        {
            ConnectorId = run?.ConnectorId ?? "codex",
            DetectedVersion = version,
            VersionVerified = verified,
            VerifiedVersions = options.VerifiedCodexVersions,
            Decision = verified
                ? "允许：版本已通过 V0.1 兼容性验证。"
                : version is null
                    ? "未验证：没有取得 Connector 版本。"
                    : "拒绝：版本未通过 V0.1 兼容性验证。"
        };
    }

    private static string? FindDetectedVersion(IReadOnlyList<TaskEventRecord> events)
    {
        foreach (var item in events.Reverse())
        {
            if (TryReadJson(item.DataJson, out var root)
                && TryGetProperty(root, "detectedVersion", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                return version.GetString();
            }
        }

        return null;
    }

    private static AgentClaimEvidence ReadClaim(
        AgentRunRecord? run,
        AgentTaskStatus taskStatus)
    {
        var status = taskStatus switch
        {
            AgentTaskStatus.Succeeded => AgentClaimStatus.Completed,
            AgentTaskStatus.WaitingForUser => AgentClaimStatus.ActionRequired,
            AgentTaskStatus.Failed => AgentClaimStatus.Failed,
            _ => AgentClaimStatus.Unavailable
        };
        if (!TryReadJson(run?.FinalResultJson, out var root))
        {
            return new AgentClaimEvidence
            {
                Status = status,
                FinalExplanation = run?.FinalSummary,
                ClaimedChangedFiles = [],
                ClaimedTests = []
            };
        }

        var outcome = GetString(root, "outcome");
        status = outcome switch
        {
            "completed" => AgentClaimStatus.Completed,
            "action_required" => AgentClaimStatus.ActionRequired,
            _ => status
        };
        return new AgentClaimEvidence
        {
            Status = status,
            FinalExplanation = GetString(root, "summary") ?? run?.FinalSummary,
            ClaimedChangedFiles = ReadStringArray(root, "changedFiles"),
            ClaimedTests = ReadClaimedTests(root)
        };
    }

    private static TaskTestEvidence ReadTestEvidence(IReadOnlyList<TaskEventRecord> events)
    {
        var byItem = new Dictionary<string, MutableCommand>(StringComparer.Ordinal);
        foreach (var taskEvent in events)
        {
            if (!TryReadJson(taskEvent.DataJson, out var root)
                || !string.Equals(GetString(root, "ItemType"), "command_execution", StringComparison.Ordinal)
                || GetString(root, "Command") is not { } command
                || !IsTestCommand(command))
            {
                continue;
            }

            var itemId = GetString(root, "ItemId") ?? $"event-{taskEvent.SequenceNumber}";
            var attemptId = GetString(root, "AttemptId") ?? "unknown-attempt";
            var commandKey = $"{attemptId}:{itemId}";
            if (!byItem.TryGetValue(commandKey, out var item))
            {
                item = new MutableCommand(itemId, RedactCommand(command));
                byItem.Add(commandKey, item);
            }

            item.ExitCode = GetInt32(root, "ExitCode") ?? item.ExitCode;
            item.Total = GetInt32(root, "TotalTests") ?? item.Total;
            item.Passed = GetInt32(root, "PassedTests") ?? item.Passed;
            item.Failed = GetInt32(root, "FailedTests") ?? item.Failed;
            item.Skipped = GetInt32(root, "SkippedTests") ?? item.Skipped;
        }

        var commands = byItem.Values.Select(item => new TestCommandEvidence
        {
            Command = item.Command,
            ExternalItemId = item.ItemId,
            ExitCode = item.ExitCode,
            Status = item.ExitCode switch
            {
                0 => EvidenceTestStatus.Passed,
                null => EvidenceTestStatus.Incomplete,
                _ => EvidenceTestStatus.Failed
            },
            TotalTests = item.Total,
            PassedTests = item.Passed,
            FailedTests = item.Failed,
            SkippedTests = item.Skipped
        })
            .ToArray();
        var status = commands.Length == 0
            ? EvidenceTestStatus.NotRun
            : commands.Any(item => item.Status == EvidenceTestStatus.Failed)
                ? EvidenceTestStatus.Failed
                : commands.Any(item => item.Status == EvidenceTestStatus.Incomplete)
                    ? EvidenceTestStatus.Incomplete
                    : EvidenceTestStatus.Passed;
        var allHaveCounts = commands.Length > 0 && commands.All(item => item.TotalTests is not null);
        return new TaskTestEvidence
        {
            Status = status,
            Commands = commands,
            TotalTests = allHaveCounts ? commands.Sum(item => item.TotalTests!.Value) : null,
            PassedTests = allHaveCounts ? commands.Sum(item => item.PassedTests ?? 0) : null,
            FailedTests = allHaveCounts ? commands.Sum(item => item.FailedTests ?? 0) : null,
            SkippedTests = allHaveCounts ? commands.Sum(item => item.SkippedTests ?? 0) : null,
            HasRealExecutionEvidence = commands.Length > 0
        };
    }

    private static EvidenceVerificationStatus DetermineVerification(
        AgentTaskStatus status,
        GitTaskEvidence git,
        TaskTestEvidence tests,
        bool contradiction) =>
        status switch
        {
            AgentTaskStatus.Cancelled => EvidenceVerificationStatus.Cancelled,
            AgentTaskStatus.Failed => EvidenceVerificationStatus.Failed,
            AgentTaskStatus.Interrupted => EvidenceVerificationStatus.Interrupted,
            AgentTaskStatus.Succeeded when contradiction
                || tests.Status is EvidenceTestStatus.Failed or EvidenceTestStatus.Incomplete =>
                EvidenceVerificationStatus.VerificationFailed,
            AgentTaskStatus.Succeeded when tests.Status == EvidenceTestStatus.Passed
                                                && git.IsGitRepository
                                                && git.DiffStatVerified =>
                EvidenceVerificationStatus.Verified,
            _ => EvidenceVerificationStatus.Unverified
        };

    private static IReadOnlyList<string> BuildReasons(
        AgentTask task,
        GitTaskEvidence git,
        TaskTestEvidence tests,
        ConnectorCompatibilityEvidence connector,
        bool contradiction,
        bool fileClaimMismatch)
    {
        var reasons = new List<string>();
        if (!git.IsGitRepository || !git.DiffStatVerified)
        {
            reasons.Add(git.UnavailableReason ?? "Git diff 未完全验证。");
        }

        if (tests.Status == EvidenceTestStatus.NotRun)
        {
            reasons.Add("没有实际测试命令证据。");
        }
        else if (tests.Status == EvidenceTestStatus.Failed)
        {
            reasons.Add("至少一个实际测试命令退出码非零。");
        }
        else if (tests.Status == EvidenceTestStatus.Incomplete)
        {
            reasons.Add("测试命令没有完整退出码证据。");
        }

        if (fileClaimMismatch)
        {
            reasons.Add("Codex 声称的修改文件与任务前后 Git 证据不一致。");
        }
        else if (contradiction)
        {
            reasons.Add("Codex 最终说明与实际测试证据不一致。");
        }

        if (!connector.VersionVerified)
        {
            reasons.Add(connector.Decision);
        }

        if (task.Status is AgentTaskStatus.Cancelled or AgentTaskStatus.Failed or AgentTaskStatus.Interrupted)
        {
            reasons.Add($"任务真实终态为 {task.Status}。");
        }

        return reasons;
    }

    private static IReadOnlyList<string> ReadClaimedTests(JsonElement root)
    {
        if (!TryGetProperty(root, "tests", out var tests) || tests.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return tests.EnumerateArray()
            .Select(item => $"{GetString(item, "name") ?? "未命名测试"}:{GetString(item, "status") ?? "unknown"}")
            .ToArray();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static bool TryReadJson(string? json, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetProperty(
        JsonElement root,
        string name,
        out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static bool IsTestCommand(string command) => TestCommandPattern().IsMatch(command);

    private static string RedactCommand(string command)
    {
        var normalized = command.ReplaceLineEndings(" ").Trim();
        normalized = SecretArgumentPattern().Replace(normalized, "$1<redacted>");
        return normalized.Length <= 1000 ? normalized : normalized[..1000];
    }

    private static string NormalizePath(string value)
    {
        var normalized = value.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    private static string Sanitize(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }

    [GeneratedRegex(
        @"(?i)(^|[;&|]\s*)(dotnet\s+test\b|python(?:\.exe)?\s+-m\s+pytest\b|pytest\b|npm(?:\.cmd)?\s+(?:run\s+)?test\b|pnpm\s+(?:run\s+)?test\b|yarn\s+test\b|cargo\s+test\b|go\s+test\b|mvnw?(?:\.cmd)?\s+test\b|gradlew?(?:\.bat)?\b.*\btest\b|ctest\b)")]
    private static partial Regex TestCommandPattern();

    [GeneratedRegex(@"(?i)(--?(?:api[-_]?key|token|password|secret)(?:=|\s+))[^\s]+")]
    private static partial Regex SecretArgumentPattern();

    private sealed class MutableCommand(string itemId, string command)
    {
        public string ItemId { get; } = itemId;

        public string Command { get; } = command;

        public int? ExitCode { get; set; }

        public int? Total { get; set; }

        public int? Passed { get; set; }

        public int? Failed { get; set; }

        public int? Skipped { get; set; }
    }
}

internal static class TaskEvidenceSummaryGenerator
{
    public static string Generate(
        AgentTaskStatus taskStatus,
        GitTaskEvidence git,
        TaskTestEvidence tests,
        EvidenceVerificationStatus verification)
    {
        var fileCount = git.ChangedFiles.Count;
        if (taskStatus == AgentTaskStatus.Cancelled)
        {
            return fileCount == 0
                ? "Codex 任务已取消，未检测到本次文件变化。"
                : $"Codex 任务已取消，取消前共修改 {fileCount} 个文件。";
        }

        if (taskStatus is AgentTaskStatus.Failed or AgentTaskStatus.Interrupted)
        {
            return fileCount == 0
                ? $"Codex 任务{(taskStatus == AgentTaskStatus.Failed ? "失败" : "已中断")}，未检测到本次文件变化。"
                : $"Codex 任务{(taskStatus == AgentTaskStatus.Failed ? "失败" : "已中断")}，已产生 {fileCount} 个文件变化，请人工检查。";
        }

        if (!git.IsGitRepository)
        {
            return "Codex 已结束任务，但项目不是 Git 仓库，文件变化和完成结果尚未验证。";
        }

        if (tests.Status == EvidenceTestStatus.Failed)
        {
            return tests.TotalTests is { } total
                ? $"Codex 已完成代码修改，但测试失败。{total} 项测试中 {tests.FailedTests ?? 0} 项失败，任务不能标记为已验证完成。"
                : "Codex 已完成代码修改，但实际测试命令失败，任务不能标记为已验证完成。";
        }

        if (verification == EvidenceVerificationStatus.VerificationFailed)
        {
            return $"Codex 声称任务已完成，但实际证据不一致。本次检测到 {fileCount} 个文件变化，任务不能标记为已验证完成。";
        }

        if (tests.Status == EvidenceTestStatus.NotRun)
        {
            return fileCount == 0
                ? "Codex 已完成任务，未检测到本次文件变化。测试结果尚未验证。"
                : $"Codex 已完成代码修改，共修改 {fileCount} 个文件。测试结果尚未验证。";
        }

        if (tests.Status == EvidenceTestStatus.Passed)
        {
            var fileText = fileCount == 0 ? "未检测到本次文件变化" : $"修改 {fileCount} 个文件";
            return tests.TotalTests is { } total
                ? $"Codex 已完成任务，{fileText}。执行 {total} 项测试，全部通过。"
                : $"Codex 已完成任务，{fileText}。实际测试命令全部通过。";
        }

        return $"Codex 已结束任务，本次检测到 {fileCount} 个文件变化。测试结果尚未验证。";
    }
}
