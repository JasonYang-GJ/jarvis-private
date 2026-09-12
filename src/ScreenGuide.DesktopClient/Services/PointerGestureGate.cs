using System;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenGuide.DesktopClient.Services;

/// <summary>One transient UI gesture; never owns Session/Turn state or capture authority.</summary>
public sealed class PointerGestureGate(TimeProvider clock) : IDisposable
{
    private readonly object _sync = new();
    private Pending? _pending;
    private Pending? _running;
    private bool _disposed;

    public bool IsArmed { get { lock (_sync) return _pending is not null; } }

    public void Arm(DateTimeOffset expiresAt, Func<CancellationToken, Task> action, CancellationToken lifetime)
    {
        Cancel();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = clock.GetUtcNow();
            if (expiresAt <= now) throw new InvalidOperationException("这次指针操作已过期，请重新提问。");
            _pending = new Pending(now, expiresAt, action, CancellationTokenSource.CreateLinkedTokenSource(lifetime));
        }
    }

    public bool Expire()
    {
        Pending? pending;
        lock (_sync)
        {
            pending = _pending;
            if (pending is null || (clock.GetUtcNow() >= pending.Started && clock.GetUtcNow() < pending.Expires)) return false;
            _pending = null;
        }
        pending.Cancellation.Cancel();
        pending.Cancellation.Dispose();
        return true;
    }

    public async Task ExecuteAsync()
    {
        if (Expire()) return;
        Pending? pending;
        lock (_sync)
        {
            pending = _pending;
            _pending = null;
            if (pending is null) return;
            _running = pending;
        }
        try
        {
            pending.Cancellation.Token.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            if (now < pending.Started || now >= pending.Expires) return;
            await pending.Action(pending.Cancellation.Token);
        }
        finally
        {
            lock (_sync) { if (ReferenceEquals(_running, pending)) _running = null; }
            pending.Cancellation.Dispose();
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _pending?.Cancellation.Cancel();
            _pending?.Cancellation.Dispose();
            _pending = null;
            _running?.Cancellation.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; }
        Cancel();
    }

    private sealed record Pending(DateTimeOffset Started, DateTimeOffset Expires,
        Func<CancellationToken, Task> Action, CancellationTokenSource Cancellation);
}
