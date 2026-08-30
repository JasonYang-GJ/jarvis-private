using ScreenGuide.Stage4.RealUsageRunner;
using System.Text.Json;

namespace ScreenGuide.Voice.Windows.Tests;

public sealed class Stage4EvaluationMetricsTests
{
    [Fact]
    public void WarmupAndNonAttemptStatesDoNotDistortFailureRateOrLatency()
    {
        var aggregate = EvaluationAggregator.Build(
        [
            new EvaluationAttempt(true, EvaluationTerminalState.Failure, TimeSpan.FromMilliseconds(999), "voice.timeout"),
            new EvaluationAttempt(false, EvaluationTerminalState.Success, TimeSpan.FromMilliseconds(10)),
            new EvaluationAttempt(false, EvaluationTerminalState.Success, TimeSpan.FromMilliseconds(20)),
            new EvaluationAttempt(false, EvaluationTerminalState.Failure, TimeSpan.FromMilliseconds(30), "voice.text_mismatch"),
            new EvaluationAttempt(false, EvaluationTerminalState.Cancelled, TimeSpan.FromMilliseconds(40), "evaluation.cancelled"),
            new EvaluationAttempt(false, EvaluationTerminalState.Blocked, TimeSpan.Zero, "voice.model_missing"),
            new EvaluationAttempt(false, EvaluationTerminalState.Success, TimeSpan.FromMilliseconds(100))
        ]);

        Assert.Equal(1, aggregate.WarmupCount);
        Assert.Equal(6, aggregate.FormalCount);
        Assert.Equal(3, aggregate.SuccessCount);
        Assert.Equal(1, aggregate.FailureCount);
        Assert.Equal(1, aggregate.CancelledCount);
        Assert.Equal(1, aggregate.BlockedCount);
        Assert.Equal(0.25, aggregate.FailureRate);
        Assert.Equal(new LatencySummary(4, 10, 20, 100, 100), aggregate.EndToEndLatency);
        Assert.Equal(1, aggregate.ErrorCounts["voice.text_mismatch"]);
        Assert.Equal(1, aggregate.ErrorCounts["evaluation.cancelled"]);
        Assert.Equal(1, aggregate.ErrorCounts["voice.model_missing"]);
        Assert.DoesNotContain("voice.timeout", aggregate.ErrorCounts.Keys);
    }

    [Fact]
    public void SafeJsonUsesAnAllowlistAndDropsArbitraryDiagnosticContent()
    {
        const string sensitiveCanary = "C:\\Users\\Alice\\secret.txt transcript=PRIVATE_CANARY";
        var aggregate = EvaluationAggregator.Build(
        [
            new EvaluationAttempt(
                false,
                EvaluationTerminalState.Failure,
                TimeSpan.FromSeconds(15),
                sensitiveCanary)
        ]);
        var report = EvaluationReport.Create(
            "voice",
            "941c2d8635939bd1329daa81f34b6829bd447750",
            "completed",
            aggregate,
            new EvaluationEnvironment("10.0.26100", "x64", true, "one"),
            cleanupConfirmed: true);

        var json = SafeEvaluationReportWriter.Serialize(report);
        using var document = JsonDocument.Parse(json);

        Assert.DoesNotContain("PRIVATE_CANARY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", json, StringComparison.Ordinal);
        Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("windowTitle", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("processId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evaluation.unexpected", json, StringComparison.Ordinal);
        Assert.Equal(
            [
                "aggregate",
                "cleanupConfirmed",
                "contractVersion",
                "environment",
                "exactSha",
                "mode",
                "networkRequests",
                "providerRequests",
                "stage",
                "visionDiagnostic"
            ],
            document.RootElement.EnumerateObject().Select(item => item.Name).Order().ToArray());
    }

    [Fact]
    public void SafeJsonAggregatesVoiceSimilarityWithoutTranscriptContent()
    {
        const string sensitiveTranscript = "元叔今天练习中文语音";
        var warmup = new VoiceAttemptTracker(
            "元枢今天练习中文语音",
            isWarmup: true,
            attemptStartedTimestamp: 10_000);
        warmup.OnFinal(sensitiveTranscript, 20_000);
        var shorter = new VoiceAttemptTracker(
            "元枢今天练习中文语音",
            isWarmup: false,
            attemptStartedTimestamp: 30_000);
        shorter.OnFinal("今天练习中文语音", 40_000);
        var aggregate = EvaluationAggregator.Build(
        [
            warmup.Complete(25_000),
            shorter.Complete(45_000)
        ]);
        var report = EvaluationReport.Create(
            "voice-diagnostic",
            "941c2d8635939bd1329daa81f34b6829bd447750",
            "completed",
            aggregate,
            new EvaluationEnvironment("10.0.26100", "x64", true, "one"),
            cleanupConfirmed: true);

        var json = SafeEvaluationReportWriter.Serialize(report);

        Assert.DoesNotContain(sensitiveTranscript, json, StringComparison.Ordinal);
        Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"voiceTextMatch\":{\"sampleCount\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"oneEditCount\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"twoEditCount\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"sameLengthMismatchCount\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"shorterCount\":1", json, StringComparison.Ordinal);
    }
}
