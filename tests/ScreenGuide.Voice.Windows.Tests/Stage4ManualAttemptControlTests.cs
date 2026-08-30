using System.Threading.Channels;
using ScreenGuide.Stage4.RealUsageRunner;

namespace ScreenGuide.Voice.Windows.Tests;

public sealed class Stage4ManualAttemptControlTests
{
    [Fact]
    public async Task VoiceAttemptAnnouncesReadyOnlyAfterListenerStarted()
    {
        var order = new List<string>();
        var input = new ChannelLineInput();
        var listenerReady = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var controlledTask = VoiceReadyAttemptControl.RunAsync(
            input,
            _ =>
            {
                order.Add("listener-starting");
                return listenerReady.Task;
            },
            () =>
            {
                order.Add("listener-stopped");
                return Task.CompletedTask;
            },
            () => order.Add("ready-announced"),
            _ =>
            {
                order.Add("capture-started");
                return Task.FromResult(Success());
            },
            CancellationToken.None);

        Assert.Equal(["listener-starting"], order);
        listenerReady.TrySetResult(true);
        var controlled = await controlledTask;

        Assert.False(controlled.StopRequested);
        Assert.Equal(
            ["listener-starting", "ready-announced", "capture-started", "listener-stopped"],
            order);
    }

    [Fact]
    public async Task QueuedStopCancelsBeforeSynchronousVoiceStartupAndStillCleansUp()
    {
        var input = new ChannelLineInput();
        await input.WriteAsync("STOP");
        var listenerStarted = false;
        var readyAnnounced = false;
        var captureStarted = false;
        var cleanupCompleted = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => VoiceReadyAttemptControl.RunAsync(
                input,
                _ =>
                {
                    listenerStarted = true;
                    return Task.CompletedTask;
                },
                () =>
                {
                    cleanupCompleted = true;
                    return Task.CompletedTask;
                },
                () => readyAnnounced = true,
                _ =>
                {
                    captureStarted = true;
                    return Task.FromResult(Success());
                },
                CancellationToken.None));

        Assert.False(listenerStarted);
        Assert.False(readyAnnounced);
        Assert.False(captureStarted);
        Assert.True(cleanupCompleted);
    }

    [Fact]
    public async Task StopDuringActiveAttemptCancelsOperationAndEndsBatch()
    {
        var input = new ChannelLineInput();
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var controlled = ManualAttemptControl.RunAsync(
            input,
            async cancellationToken =>
            {
                started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Success();
                }
                catch (OperationCanceledException)
                {
                    return new EvaluationAttempt(
                        false,
                        EvaluationTerminalState.Cancelled,
                        TimeSpan.Zero,
                        "evaluation.cancelled");
                }
            },
            CancellationToken.None);

        await started.Task;
        await input.WriteAsync("STOP");

        var result = await controlled;

        Assert.True(result.StopRequested);
        Assert.Equal(EvaluationTerminalState.Cancelled, result.Value.State);
    }

    [Theory]
    [InlineData(EvaluationTerminalState.Cancelled, "evaluation.cancelled", true)]
    [InlineData(EvaluationTerminalState.Failure, "voice.listener_faulted", true)]
    [InlineData(EvaluationTerminalState.Cancelled, "vision.identity_changed", true)]
    [InlineData(EvaluationTerminalState.Failure, "voice.text_mismatch", false)]
    [InlineData(EvaluationTerminalState.Success, null, false)]
    public void BatchTerminationPolicyStopsOnlyAuthorityOrListenerTerminalCases(
        EvaluationTerminalState state,
        string? errorCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            BatchTerminationPolicy.ShouldEndBatch(
                new EvaluationAttempt(false, state, TimeSpan.Zero, errorCode)));
    }

    private static EvaluationAttempt Success() =>
        new(false, EvaluationTerminalState.Success, TimeSpan.Zero);

    private sealed class ChannelLineInput : IManualLineInput
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ReadAsync(cancellationToken);

        public ValueTask WriteAsync(string value) => _lines.Writer.WriteAsync(value);

        private async ValueTask<string?> ReadAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _lines.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }
}
