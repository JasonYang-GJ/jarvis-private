using System.Threading.Channels;
using ScreenGuide.Stage4.RealUsageRunner;

namespace ScreenGuide.Voice.Windows.Tests;

public sealed class Stage4ManualAttemptControlTests
{
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
