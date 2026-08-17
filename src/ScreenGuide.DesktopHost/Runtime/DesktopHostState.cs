using ScreenGuide.Core.Tasking;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed record DesktopHostSnapshot(
    bool IsStarted,
    DeviceRecord? LocalDevice,
    IReadOnlyList<ProjectRecord> AuthorizedProjects,
    IReadOnlyList<Guid> RecoveredTaskIds,
    IReadOnlyList<string> ConnectorIds);

public sealed class DesktopHostState
{
    private readonly object _gate = new();
    private DesktopHostSnapshot _snapshot = EmptySnapshot();

    public DesktopHostSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    internal void MarkStarted(
        DeviceRecord localDevice,
        IReadOnlyList<ProjectRecord> authorizedProjects,
        IReadOnlyList<Guid> recoveredTaskIds,
        IReadOnlyList<string> connectorIds)
    {
        lock (_gate)
        {
            _snapshot = new DesktopHostSnapshot(
                true,
                localDevice,
                authorizedProjects.ToArray(),
                recoveredTaskIds.ToArray(),
                connectorIds.ToArray());
        }
    }

    internal void MarkStopped()
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { IsStarted = false };
        }
    }

    private static DesktopHostSnapshot EmptySnapshot() => new(
        false,
        null,
        Array.Empty<ProjectRecord>(),
        Array.Empty<Guid>(),
        Array.Empty<string>());
}
