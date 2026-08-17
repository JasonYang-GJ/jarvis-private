namespace ScreenGuide.Core.Tasking;

public sealed record CommandRegistrationResult(bool Accepted, CommandRecord Command);

public sealed record RecoveryResult(IReadOnlyList<Guid> InterruptedTaskIds);

public interface ILocalTaskStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task UpsertDeviceAsync(DeviceRecord device, CancellationToken cancellationToken = default);

    Task SetProjectAuthorizationAsync(ProjectRecord project, CancellationToken cancellationToken = default);

    Task<DeviceRecord?> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);

    Task<ProjectRecord?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<AgentTask?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<CommandRecord?> GetCommandAsync(Guid commandId, CancellationToken cancellationToken = default);

    Task<CommandRegistrationResult> RegisterCommandAsync(
        CommandRecord command,
        CancellationToken cancellationToken = default);

    Task CreateTaskAsync(
        AgentTask task,
        Guid sourceCommandId,
        CancellationToken cancellationToken = default);

    Task<AgentTask> TransitionTaskAsync(
        Guid taskId,
        TaskStatus newStatus,
        TaskEventSource source,
        string message,
        Guid? sourceDeviceId = null,
        Guid? commandId = null,
        string? dataJson = null,
        string? failureCode = null,
        string? failureMessage = null,
        CancellationToken cancellationToken = default);

    Task<bool> RequestCancellationAsync(
        Guid taskId,
        Guid sourceDeviceId,
        Guid? commandId = null,
        CancellationToken cancellationToken = default);

    Task<RecoveryResult> RecoverInterruptedTasksAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskEventRecord>> GetTaskEventsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditLogEntry>> GetAuditLogAsync(
        CancellationToken cancellationToken = default);
}
