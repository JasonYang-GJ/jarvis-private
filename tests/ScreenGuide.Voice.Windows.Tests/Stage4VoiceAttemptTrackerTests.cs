using System.Diagnostics;
using ScreenGuide.Stage4.RealUsageRunner;

namespace ScreenGuide.Voice.Windows.Tests;

public sealed class Stage4VoiceAttemptTrackerTests
{
    [Fact]
    public void EvaluationPhraseUsesOfflineModelValidatedCommonWords()
    {
        Assert.Equal("今天我们一起练习中文语音", VoiceEvaluationContract.FixedPhrase);
        Assert.Equal(12, VoiceTextNormalizer.Normalize(VoiceEvaluationContract.FixedPhrase).Length);
    }

    [Fact]
    public void UniqueNormalizedFinalProducesOneSuccessfulAttempt()
    {
        const string expected = "元枢语音评测";
        const long started = 10_000;
        var tracker = new VoiceAttemptTracker(expected, isWarmup: false, started);

        tracker.OnPartial("元枢", started + Stopwatch.Frequency);
        tracker.OnFinal("元 枢，语音评测。", started + (2 * Stopwatch.Frequency));
        var attempt = tracker.Complete(started + (3 * Stopwatch.Frequency));

        Assert.Equal(EvaluationTerminalState.Success, attempt.State);
        Assert.Equal(TimeSpan.FromSeconds(1), attempt.EndToEndElapsed);
        Assert.Null(attempt.ErrorCode);
        Assert.Equal(new VoiceTextMatchDiagnostic(6, 6, 0), attempt.VoiceTextMatch);
    }

    [Theory]
    [InlineData("元叔今天练习中文语音", 10, 10, 1)]
    [InlineData("今天练习中文语音", 10, 8, 2)]
    [InlineData("完全不同", 10, 4, 10)]
    public void MismatchKeepsOnlySafeLengthAndEditDistanceEvidence(
        string recognized,
        int expectedLength,
        int actualLength,
        int editDistance)
    {
        const long started = 15_000;
        var tracker = new VoiceAttemptTracker("元枢今天练习中文语音", isWarmup: true, started);

        tracker.OnFinal(recognized, started + Stopwatch.Frequency);
        var attempt = tracker.Complete(started + (2 * Stopwatch.Frequency));

        Assert.Equal(EvaluationTerminalState.Failure, attempt.State);
        Assert.Equal("voice.text_mismatch", attempt.ErrorCode);
        Assert.Equal(
            new VoiceTextMatchDiagnostic(expectedLength, actualLength, editDistance),
            attempt.VoiceTextMatch);
        Assert.DoesNotContain(recognized, attempt.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationClosesAttemptAndRejectsLateFinal()
    {
        const long started = 20_000;
        var tracker = new VoiceAttemptTracker("元枢语音评测", isWarmup: false, started);

        tracker.OnPartial("元枢", started + Stopwatch.Frequency);
        var attempt = tracker.Complete(
            started + (2 * Stopwatch.Frequency),
            cancelled: true);
        tracker.OnFinal("元枢语音评测", started + (3 * Stopwatch.Frequency));

        Assert.Equal(EvaluationTerminalState.Cancelled, attempt.State);
        Assert.Equal("evaluation.cancelled", attempt.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(1), attempt.EndToEndElapsed);
        Assert.Throws<InvalidOperationException>(() => tracker.Complete(started + (4 * Stopwatch.Frequency)));
    }

    [Theory]
    [InlineData("multiple", "voice.multiple_final")]
    [InlineData("mismatch", "voice.text_mismatch")]
    [InlineData("timeout", "voice.timeout")]
    [InlineData("no-final", "voice.no_final")]
    [InlineData("fault", "voice.listener_faulted")]
    public void FailurePathsUseOnlyStableErrorCodes(string scenario, string expectedError)
    {
        const long started = 30_000;
        var tracker = new VoiceAttemptTracker("元枢语音评测", isWarmup: false, started);

        switch (scenario)
        {
            case "multiple":
                tracker.OnFinal("元枢语音评测", started + Stopwatch.Frequency);
                tracker.OnFinal("元枢语音评测", started + (2 * Stopwatch.Frequency));
                break;
            case "mismatch":
                tracker.OnFinal("别的内容", started + Stopwatch.Frequency);
                break;
            case "timeout":
                tracker.OnPartial("元枢", started + Stopwatch.Frequency);
                break;
            case "fault":
                tracker.OnListenerFaulted();
                break;
        }

        var attempt = tracker.Complete(
            started + (3 * Stopwatch.Frequency),
            timedOut: scenario is "timeout" or "no-final");

        Assert.Equal(EvaluationTerminalState.Failure, attempt.State);
        Assert.Equal(expectedError, attempt.ErrorCode);
    }
}
