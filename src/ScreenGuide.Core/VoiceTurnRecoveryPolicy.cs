namespace ScreenGuide.Core;

public sealed record VoiceTurnRecoveryPolicy(TimeSpan TurnTimeout)
{
    public static VoiceTurnRecoveryPolicy Default { get; } = new(TimeSpan.FromSeconds(30));

    public bool IsTurnTimeout(bool sessionCancellationRequested, bool turnCancellationRequested) =>
        !sessionCancellationRequested && turnCancellationRequested;

    public bool ShouldResumeListening(bool backgroundEnabled, bool sessionCancellationRequested) =>
        backgroundEnabled && !sessionCancellationRequested;
}
