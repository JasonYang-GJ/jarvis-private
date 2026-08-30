namespace ScreenGuide.DesktopHost.Runtime;

/// <summary>
/// Owns only short-lived keyed operation gates. Session and Turn state remain owned by
/// <see cref="SessionCoordinator"/> and the persistent stores.
/// </summary>
internal sealed class SessionOperationGateRegistry
{
    private readonly object _accountingLock = new();
    private readonly Dictionary<Guid, GateEntry> _sessionGates = [];
    private readonly Dictionary<Guid, GateEntry> _turnGates = [];

    internal int SessionGateCount
    {
        get
        {
            lock (_accountingLock)
            {
                return _sessionGates.Count;
            }
        }
    }

    internal int TurnGateCount
    {
        get
        {
            lock (_accountingLock)
            {
                return _turnGates.Count;
            }
        }
    }

    internal int GetSessionReferenceCount(Guid sessionId) =>
        GetReferenceCount(_sessionGates, sessionId);

    internal int GetTurnReferenceCount(Guid turnId) =>
        GetReferenceCount(_turnGates, turnId);

    public ValueTask<IAsyncDisposable> AcquireSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        AcquireAsync(_sessionGates, sessionId, cancellationToken);

    public ValueTask<IAsyncDisposable> AcquireTurnAsync(
        Guid turnId,
        CancellationToken cancellationToken = default) =>
        AcquireAsync(_turnGates, turnId, cancellationToken);

    private async ValueTask<IAsyncDisposable> AcquireAsync(
        Dictionary<Guid, GateEntry> gates,
        Guid key,
        CancellationToken cancellationToken)
    {
        GateEntry entry;
        lock (_accountingLock)
        {
            if (!gates.TryGetValue(key, out entry!))
            {
                entry = new GateEntry();
                gates.Add(key, entry);
            }

            checked
            {
                entry.ReferenceCount++;
            }
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new GateLease(this, gates, key, entry);
        }
        catch
        {
            ReleaseReference(gates, key, entry);
            throw;
        }
    }

    private int GetReferenceCount(Dictionary<Guid, GateEntry> gates, Guid key)
    {
        lock (_accountingLock)
        {
            return gates.TryGetValue(key, out var entry) ? entry.ReferenceCount : 0;
        }
    }

    private void ReleaseReference(
        Dictionary<Guid, GateEntry> gates,
        Guid key,
        GateEntry entry)
    {
        var dispose = false;
        lock (_accountingLock)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount < 0)
            {
                throw new InvalidOperationException("Session operation gate reference count became invalid.");
            }

            if (entry.ReferenceCount == 0
                && gates.TryGetValue(key, out var current)
                && ReferenceEquals(current, entry))
            {
                gates.Remove(key);
                dispose = true;
            }
        }

        if (dispose)
        {
            entry.Semaphore.Dispose();
        }
    }

    private sealed class GateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }

    private sealed class GateLease(
        SessionOperationGateRegistry owner,
        Dictionary<Guid, GateEntry> gates,
        Guid key,
        GateEntry entry) : IAsyncDisposable
    {
        private SessionOperationGateRegistry? _owner = owner;

        public ValueTask DisposeAsync()
        {
            var currentOwner = Interlocked.Exchange(ref _owner, null);
            if (currentOwner is null)
            {
                return ValueTask.CompletedTask;
            }

            try
            {
                entry.Semaphore.Release();
            }
            finally
            {
                currentOwner.ReleaseReference(gates, key, entry);
            }

            return ValueTask.CompletedTask;
        }
    }
}
