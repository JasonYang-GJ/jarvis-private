using ScreenGuide.Stage4.RealUsageRunner;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class Stage4VisionEvaluationMetricsTests
{
    [Fact]
    public void VisionAggregateKeepsCaptureAnalysisAndEndToEndLatencySeparate()
    {
        var aggregate = EvaluationAggregator.Build(
        [
            new EvaluationAttempt(
                true,
                EvaluationTerminalState.Success,
                TimeSpan.FromMilliseconds(900),
                CaptureElapsed: TimeSpan.FromMilliseconds(300),
                AnalysisElapsed: TimeSpan.FromMilliseconds(600)),
            new EvaluationAttempt(
                false,
                EvaluationTerminalState.Success,
                TimeSpan.FromMilliseconds(100),
                CaptureElapsed: TimeSpan.FromMilliseconds(30),
                AnalysisElapsed: TimeSpan.FromMilliseconds(70)),
            new EvaluationAttempt(
                false,
                EvaluationTerminalState.Failure,
                TimeSpan.FromMilliseconds(200),
                "vision.canary_missing",
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(150))
        ]);

        Assert.Equal(new LatencySummary(2, 30, 30, 50, 50), aggregate.CaptureLatency);
        Assert.Equal(new LatencySummary(2, 70, 70, 150, 150), aggregate.AnalysisLatency);
        Assert.Equal(new LatencySummary(2, 100, 100, 200, 200), aggregate.EndToEndLatency);
    }
}
