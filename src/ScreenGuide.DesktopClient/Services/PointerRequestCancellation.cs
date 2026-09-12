using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenGuide.DesktopClient.Services;

/// <summary>Cleanup for one UI-originated pointer request; SessionCoordinator still owns the Turn.</summary>
public sealed class PointerRequestCancellation
{
    private readonly object _sync = new();
    private Func<CancellationToken, Task>? _cancel;
    private readonly List<Func<CancellationToken, Task>> _failed = new();
    private Task _completion = Task.CompletedTask;

    public Task BindAsync(Func<CancellationToken, Task> cancel, CancellationToken operationToken)
    {
        lock (_sync)
        {
            if (!operationToken.IsCancellationRequested)
            {
                _cancel = cancel;
                return Task.CompletedTask;
            }
            // Track late replies in the same wait/retry chain without replacing a new binding.
            _completion = CancelAfterAsync(_completion, cancel);
            return _completion;
        }
    }

    public Task CancelAsync()
    {
        lock (_sync)
        {
            var cancel = _cancel;
            if (cancel is null && _failed.Count == 0) return _completion;
            _cancel = null;
            _completion = CancelAfterAsync(_completion, cancel);
            return _completion;
        }
    }

    private async Task CancelAfterAsync(Task previous, Func<CancellationToken, Task>? cancel)
    {
        await Task.Yield();
        try { await previous; } catch { /* A previous failure must not suppress the next cancellation. */ }
        List<Func<CancellationToken, Task>> batch;
        lock (_sync)
        {
            batch = new List<Func<CancellationToken, Task>>(_failed);
            _failed.Clear();
        }
        if (cancel is not null) batch.Add(cancel);
        Exception? failure = null;
        foreach (var item in batch)
        {
            try { await InvokeAsync(item); }
            catch (Exception exception)
            {
                lock (_sync) { _failed.Add(item); }
                failure ??= exception;
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task InvokeAsync(Func<CancellationToken, Task> cancel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await cancel(timeout.Token).WaitAsync(timeout.Token);
    }
}
