using ScreenGuide.Core.Tasking;

namespace ScreenGuide.Persistence.Runtime;

public sealed class TaskRecoveryService(ILocalTaskStore store)
{
    public Task<RecoveryResult> RecoverAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default) =>
        store.RecoverInterruptedTasksAsync(recoveredAtUtc, cancellationToken);
}
