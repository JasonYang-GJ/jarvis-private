using ScreenGuide.Core;

namespace ScreenGuide.Core.Tests;

public sealed class VoiceTurnRecoveryPolicyTests
{
    private readonly VoiceTurnRecoveryPolicy _policy = VoiceTurnRecoveryPolicy.Default;

    [Fact]
    public void DefaultTimeoutIsThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), _policy.TurnTimeout);
    }

    [Fact]
    public void TurnCancellationIsARecoverableTimeoutWhenSessionIsStillRunning()
    {
        Assert.True(_policy.IsTurnTimeout(
            sessionCancellationRequested: false,
            turnCancellationRequested: true));
    }

    [Fact]
    public void SessionCancellationIsNotReportedAsTurnTimeout()
    {
        Assert.False(_policy.IsTurnTimeout(
            sessionCancellationRequested: true,
            turnCancellationRequested: true));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void ResumeDecisionRequiresAnActiveUncancelledSession(
        bool backgroundEnabled,
        bool sessionCancellationRequested,
        bool expected)
    {
        Assert.Equal(
            expected,
            _policy.ShouldResumeListening(backgroundEnabled, sessionCancellationRequested));
    }
}
