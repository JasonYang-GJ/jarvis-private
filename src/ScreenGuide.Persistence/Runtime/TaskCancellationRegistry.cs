using System.Collections.Concurrent;

namespace ScreenGuide.Persistence.Runtime;

public sealed class TaskCancellationRegistry : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();
    private bool _disposed;

    public CancellationToken GetOrCreateToken(Guid taskId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sources.GetOrAdd(taskId, static _ => new CancellationTokenSource()).Token;
    }

    public bool RequestCancellation(Guid taskId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sources.TryGetValue(taskId, out var source))
        {
            return false;
        }

        source.Cancel();
        return true;
    }

    public void Complete(Guid taskId)
    {
        if (_sources.TryRemove(taskId, out var source))
        {
            source.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _sources)
        {
            if (_sources.TryRemove(entry.Key, out var source))
            {
                source.Cancel();
                source.Dispose();
            }
        }
    }
}
