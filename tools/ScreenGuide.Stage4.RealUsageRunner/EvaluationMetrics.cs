namespace ScreenGuide.Stage4.RealUsageRunner;

public enum EvaluationTerminalState
{
    Success,
    Failure,
    Cancelled,
    Blocked
}

public sealed record EvaluationAttempt(
    bool IsWarmup,
    EvaluationTerminalState State,
    TimeSpan EndToEndElapsed,
    string? ErrorCode = null,
    TimeSpan? CaptureElapsed = null,
    TimeSpan? AnalysisElapsed = null,
    VoiceTextMatchDiagnostic? VoiceTextMatch = null);

public sealed record VoiceTextMatchDiagnostic(
    int ExpectedLength,
    int ActualLength,
    int EditDistance);

public sealed record VoiceTextMatchSummary(
    int SampleCount,
    int ExactCount,
    int OneEditCount,
    int TwoEditCount,
    int ThreePlusEditCount,
    int SameLengthMismatchCount,
    int ShorterCount,
    int LongerCount);

public sealed record LatencySummary(
    int Count,
    long MinimumMilliseconds,
    long P50Milliseconds,
    long P95Milliseconds,
    long MaximumMilliseconds);

public sealed record EvaluationAggregate(
    int WarmupCount,
    int FormalCount,
    int SuccessCount,
    int FailureCount,
    int CancelledCount,
    int BlockedCount,
    double? FailureRate,
    LatencySummary CaptureLatency,
    LatencySummary AnalysisLatency,
    LatencySummary EndToEndLatency,
    IReadOnlyDictionary<string, int> ErrorCounts,
    VoiceTextMatchSummary VoiceTextMatch);

public static class EvaluationAggregator
{
    public static EvaluationAggregate Build(IReadOnlyCollection<EvaluationAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var formal = attempts.Where(item => !item.IsWarmup).ToArray();
        var successCount = formal.Count(item => item.State == EvaluationTerminalState.Success);
        var failureCount = formal.Count(item => item.State == EvaluationTerminalState.Failure);
        var measured = formal
            .Where(item => item.State is EvaluationTerminalState.Success or EvaluationTerminalState.Failure)
            .Select(item => Math.Max(0, (long)Math.Round(item.EndToEndElapsed.TotalMilliseconds)))
            .Order()
            .ToArray();
        var errors = formal
            .Where(item => !string.IsNullOrWhiteSpace(item.ErrorCode))
            .GroupBy(item => StableEvaluationErrors.Normalize(item.ErrorCode), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var attempted = successCount + failureCount;

        return new EvaluationAggregate(
            attempts.Count(item => item.IsWarmup),
            formal.Length,
            successCount,
            failureCount,
            formal.Count(item => item.State == EvaluationTerminalState.Cancelled),
            formal.Count(item => item.State == EvaluationTerminalState.Blocked),
            attempted == 0 ? null : (double)failureCount / attempted,
            SummarizeElapsed(formal, item => item.CaptureElapsed),
            SummarizeElapsed(formal, item => item.AnalysisElapsed),
            Summarize(measured),
            errors,
            SummarizeVoiceTextMatch(attempts));
    }

    private static VoiceTextMatchSummary SummarizeVoiceTextMatch(
        IEnumerable<EvaluationAttempt> attempts)
    {
        var samples = attempts
            .Select(item => item.VoiceTextMatch)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        return new VoiceTextMatchSummary(
            samples.Length,
            samples.Count(item => item.EditDistance == 0),
            samples.Count(item => item.EditDistance == 1),
            samples.Count(item => item.EditDistance == 2),
            samples.Count(item => item.EditDistance >= 3),
            samples.Count(item => item.EditDistance > 0 && item.ActualLength == item.ExpectedLength),
            samples.Count(item => item.ActualLength < item.ExpectedLength),
            samples.Count(item => item.ActualLength > item.ExpectedLength));
    }

    private static LatencySummary SummarizeElapsed(
        IEnumerable<EvaluationAttempt> attempts,
        Func<EvaluationAttempt, TimeSpan?> selector)
    {
        var measured = attempts
            .Where(item => item.State is EvaluationTerminalState.Success or EvaluationTerminalState.Failure)
            .Select(selector)
            .Where(item => item is not null)
            .Select(item => Math.Max(0, (long)Math.Round(item!.Value.TotalMilliseconds)))
            .Order()
            .ToArray();
        return Summarize(measured);
    }

    public static LatencySummary Summarize(IReadOnlyList<long> sortedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(sortedMilliseconds);
        if (sortedMilliseconds.Count == 0)
        {
            return new LatencySummary(0, 0, 0, 0, 0);
        }

        return new LatencySummary(
            sortedMilliseconds.Count,
            sortedMilliseconds[0],
            NearestRank(sortedMilliseconds, 0.50),
            NearestRank(sortedMilliseconds, 0.95),
            sortedMilliseconds[^1]);
    }

    private static long NearestRank(IReadOnlyList<long> sortedValues, double percentile)
    {
        var rank = Math.Max(1, (int)Math.Ceiling(percentile * sortedValues.Count));
        return sortedValues[rank - 1];
    }
}

public static class StableEvaluationErrors
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "evaluation.cancelled",
        "evaluation.build_identity_mismatch",
        "evaluation.consent_missing",
        "evaluation.unexpected",
        "voice.listener_faulted",
        "voice.model_missing",
        "voice.microphone_missing",
        "voice.multiple_final",
        "voice.no_final",
        "voice.text_mismatch",
        "voice.timeout",
        "vision.analysis_failed",
        "vision.canary_missing",
        "vision.capture_failed",
        "vision.capture_unavailable",
        "vision.identity_changed",
        "vision.provider_not_local"
    };

    public static string Normalize(string? value) =>
        value is not null && Allowed.Contains(value)
            ? value
            : "evaluation.unexpected";
}
