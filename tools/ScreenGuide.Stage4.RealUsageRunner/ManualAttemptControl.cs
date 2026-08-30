using System.Threading.Channels;

namespace ScreenGuide.Stage4.RealUsageRunner;

public interface IManualLineInput
{
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);
}

public sealed record ControlledAttemptResult<T>(T Value, bool StopRequested);

public static class ManualAttemptControl
{
    public static async Task<ControlledAttemptResult<T>> RunAsync<T>(
        IManualLineInput input,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(operation);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using var stopWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var stopTask = WaitForStopAsync(input, stopWaitCancellation.Token);
        var cancelOperationOnStop = stopTask.ContinueWith(
            static (completed, state) =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
            },
            operationCancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var operationTask = operation(operationCancellation.Token);
        var completed = await Task.WhenAny(operationTask, stopTask).ConfigureAwait(false);
        if (completed == operationTask && !stopTask.IsCompletedSuccessfully)
        {
            stopWaitCancellation.Cancel();
            await ObserveCancellationAsync(stopTask).ConfigureAwait(false);
            await cancelOperationOnStop.ConfigureAwait(false);
            return new ControlledAttemptResult<T>(
                await operationTask.ConfigureAwait(false),
                StopRequested: false);
        }

        operationCancellation.Cancel();
        await cancelOperationOnStop.ConfigureAwait(false);
        return new ControlledAttemptResult<T>(
            await operationTask.ConfigureAwait(false),
            StopRequested: true);
    }

    private static async Task WaitForStopAsync(
        IManualLineInput input,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null
                || string.Equals(line, "STOP", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public static class VoiceReadyAttemptControl
{
    public static Task<ControlledAttemptResult<T>> RunAsync<T>(
        IManualLineInput input,
        Func<CancellationToken, Task> startListening,
        Func<Task> stopListening,
        Action announceReady,
        Func<CancellationToken, Task<T>> capture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(startListening);
        ArgumentNullException.ThrowIfNull(stopListening);
        ArgumentNullException.ThrowIfNull(announceReady);
        ArgumentNullException.ThrowIfNull(capture);
        return ManualAttemptControl.RunAsync(
            input,
            async attemptCancellation =>
            {
                try
                {
                    attemptCancellation.ThrowIfCancellationRequested();
                    await startListening(attemptCancellation).ConfigureAwait(false);
                    attemptCancellation.ThrowIfCancellationRequested();
                    announceReady();
                    return await capture(attemptCancellation).ConfigureAwait(false);
                }
                finally
                {
                    await stopListening().ConfigureAwait(false);
                }
            },
            cancellationToken);
    }
}

public sealed class ConsoleLineInput : IManualLineInput, IDisposable
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
    private readonly Thread _readerThread;
    private volatile bool _disposed;

    public ConsoleLineInput()
    {
        _readerThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "Yuanshu-S4-R2-ConsoleInput"
        };
        _readerThread.Start();
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private void ReadLoop()
    {
        while (!_disposed)
        {
            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            if (!_lines.Writer.TryWrite(line))
            {
                break;
            }
        }

        _lines.Writer.TryComplete();
    }

    public void Dispose()
    {
        _disposed = true;
        _lines.Writer.TryComplete();
    }
}

public static class BatchTerminationPolicy
{
    public static bool ShouldEndBatch(EvaluationAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return attempt.State is EvaluationTerminalState.Cancelled or EvaluationTerminalState.Blocked
            || attempt.ErrorCode is "voice.listener_faulted" or "vision.identity_changed";
    }
}
