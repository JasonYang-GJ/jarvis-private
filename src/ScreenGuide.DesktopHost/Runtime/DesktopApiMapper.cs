using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Runtime;

internal static class DesktopApiMapper
{
    public static ProjectDto Project(ProjectRecord project, IReadOnlyList<AgentTask>? tasks = null) =>
        new(
            project.Id,
            project.Name,
            project.RootPath,
            project.AuthorizationState.ToString(),
            Directory.Exists(Path.Combine(project.RootPath, ".git")),
            false,
            "尚未读取",
            tasks?.Count ?? 0,
            tasks?.Any(IsActive) ?? false,
            project.AuthorizedAtUtc);

    public static TaskSummaryDto Summary(
        AgentTask task,
        ProjectRecord project,
        TaskEvidence? evidence = null) =>
        new(
            task.Id,
            task.ProjectId,
            project.Name,
            task.Title,
            task.Status.ToString(),
            evidence?.VerificationStatus.ToString(),
            evidence?.UserSummary ?? SystemSummary(task),
            task.CreatedAtUtc,
            task.UpdatedAtUtc,
            task.StartedAtUtc,
            task.CompletedAtUtc,
            task.FailureMessage,
            task.Phase.ToString());

    public static TaskDetailsDto Details(LocalTaskDetails details)
    {
        var summary = Summary(details.Task, details.Project, details.Evidence);
        return new TaskDetailsDto(
            summary,
            Project(details.Project),
            details.Task.Instruction,
            details.Task.WorkingDirectoryRelativePath,
            details.AgentRun?.ExternalRunId,
            details.AgentRun?.ConnectorVersion,
            details.Attempts.LastOrDefault()?.AttemptNumber ?? 0,
            details.Attempts.Select(Attempt).ToArray(),
            details.PendingDecision is null
                ? null
                : new DecisionRequestDto(
                    details.PendingDecision.Id,
                    details.PendingDecision.Question,
                    details.PendingDecision.CreatedAtUtc),
            details.Events.Select(Event).ToArray(),
            details.Evidence is null ? null : Evidence(details.Evidence),
            details.ResourceScopes.Select(ResourceScope).ToArray(),
            details.SkillInvocations.Select(SkillInvocation).ToArray());
    }

    private static TaskAttemptDto Attempt(AgentAttemptRecord attempt) =>
        new(
            attempt.AttemptNumber,
            attempt.Operation.ToString(),
            attempt.Status.ToString(),
            attempt.ProcessId,
            attempt.StartedAtUtc,
            attempt.TerminalEventAtUtc ?? attempt.ProcessExitedAtUtc,
            attempt.ExitCode);

    private static TaskEventDto Event(TaskEventRecord item) =>
        new(
            item.SequenceNumber,
            item.EventType.ToString(),
            item.FromStatus?.ToString(),
            item.ToStatus?.ToString(),
            item.Source.ToString(),
            item.OccurredAtUtc,
            item.Message,
            item.FromPhase?.ToString(),
            item.ToPhase?.ToString());

    private static ResourceScopeDto ResourceScope(ResourceScopeRecord scope) =>
        new(
            scope.Id,
            scope.ScopeType.ToString(),
            scope.ResourceId,
            scope.ScopeValue,
            scope.AccessMode.ToString(),
            scope.GrantedAtUtc,
            scope.ExpiresAtUtc,
            scope.RevokedAtUtc);

    private static SkillInvocationDto SkillInvocation(SkillInvocationRecord invocation) =>
        new(
            invocation.Id,
            invocation.SequenceNumber,
            invocation.SkillId,
            invocation.SkillVersion,
            invocation.Capability,
            invocation.Status.ToString(),
            invocation.CreatedAtUtc,
            invocation.StartedAtUtc,
            invocation.CompletedAtUtc,
            invocation.FailureCode,
            invocation.FailureMessage);

    private static TaskEvidenceDto Evidence(TaskEvidence evidence) =>
        new(
            evidence.VerificationStatus.ToString(),
            evidence.UserSummary,
            evidence.AgentClaimContradictedByEvidence,
            evidence.AgentClaim.Status.ToString(),
            evidence.AgentClaim.FinalExplanation,
            evidence.Git.IsGitRepository,
            evidence.Git.AddedFileCount,
            evidence.Git.ModifiedFileCount,
            evidence.Git.DeletedFileCount,
            evidence.Git.AddedLineCount,
            evidence.Git.DeletedLineCount,
            evidence.Git.DiffStatVerified,
            evidence.Git.ChangedFiles.Select(file => new EvidenceFileDto(
                file.RelativePath,
                file.ChangeType.ToString(),
                file.HadPreExistingChanges,
                file.MixedWithPreExistingChanges,
                file.AddedLines,
                file.DeletedLines,
                file.IsBinary)).ToArray(),
            evidence.Tests.Status.ToString(),
            evidence.Tests.HasRealExecutionEvidence,
            evidence.Tests.TotalTests,
            evidence.Tests.PassedTests,
            evidence.Tests.FailedTests,
            evidence.Tests.SkippedTests,
            evidence.Tests.Commands.Select(command => new TestCommandDto(
                command.Command,
                command.ExitCode,
                command.Status.ToString(),
                command.TotalTests,
                command.PassedTests,
                command.FailedTests,
                command.SkippedTests)).ToArray(),
            evidence.Connector.DetectedVersion,
            evidence.Connector.VersionVerified,
            evidence.Connector.Decision,
            evidence.VerificationReasons,
            evidence.GeneratedAtUtc);

    public static bool IsActive(AgentTask task) => task.Status is
        Core.Tasking.TaskStatus.Pending or
        Core.Tasking.TaskStatus.Running or
        Core.Tasking.TaskStatus.WaitingForUser or
        Core.Tasking.TaskStatus.CancellationRequested;

    private static string? SystemSummary(AgentTask task) => task.Status switch
    {
        Core.Tasking.TaskStatus.WaitingForUser => "任务需要你的决定才能继续。",
        Core.Tasking.TaskStatus.Running => "任务正在执行。",
        Core.Tasking.TaskStatus.Failed => "任务执行失败，请打开任务查看原因。",
        Core.Tasking.TaskStatus.Interrupted => "任务意外中断，没有自动重新执行。",
        Core.Tasking.TaskStatus.Cancelled => "任务已取消。",
        _ => null
    };
}
