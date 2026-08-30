using System.Diagnostics;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Stage4.RealUsageRunner;

public sealed record VisionAttemptResult(
    EvaluationAttempt Attempt,
    bool CleanupConfirmed);

public sealed class VisionAttemptEvaluator(
    IWindowCaptureService captureService,
    IWindowVisionProvider visionProvider)
{
    public async Task<VisionAttemptResult> EvaluateAsync(
        WindowCaptureTarget target,
        string expectedCanary,
        bool isWarmup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(expectedCanary))
        {
            throw new ArgumentException("固定视觉评测标记不能为空。", nameof(expectedCanary));
        }

        if (visionProvider.SendsImageOffDevice)
        {
            return Result(
                isWarmup,
                EvaluationTerminalState.Blocked,
                TimeSpan.Zero,
                "vision.provider_not_local",
                cleanupConfirmed: true);
        }

        var started = Stopwatch.GetTimestamp();
        CapturedWindowFrame? frame = null;
        TimeSpan? captureElapsed = null;
        try
        {
            try
            {
                frame = await captureService.CaptureAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                captureElapsed = Stopwatch.GetElapsedTime(started);
            }
            catch (WindowIdentityException)
            {
                return Result(
                    isWarmup,
                    EvaluationTerminalState.Cancelled,
                    Stopwatch.GetElapsedTime(started),
                    "vision.identity_changed",
                    cleanupConfirmed: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Result(
                    isWarmup,
                    EvaluationTerminalState.Cancelled,
                    Stopwatch.GetElapsedTime(started),
                    "evaluation.cancelled",
                    cleanupConfirmed: true);
            }
            catch
            {
                return Result(
                    isWarmup,
                    EvaluationTerminalState.Failure,
                    Stopwatch.GetElapsedTime(started),
                    "vision.capture_failed",
                    cleanupConfirmed: true);
            }

            var analysisStarted = Stopwatch.GetTimestamp();
            try
            {
                var result = await visionProvider.AnalyzeAsync(
                        new WindowVisionRequest(target, frame),
                        cancellationToken)
                    .ConfigureAwait(false);
                var analysisElapsed = Stopwatch.GetElapsedTime(analysisStarted);
                var endToEndElapsed = Stopwatch.GetElapsedTime(started);
                var found = result.UserSummary.Contains(expectedCanary, StringComparison.Ordinal);
                return new VisionAttemptResult(
                    new EvaluationAttempt(
                        isWarmup,
                        found ? EvaluationTerminalState.Success : EvaluationTerminalState.Failure,
                        endToEndElapsed,
                        found ? null : "vision.canary_missing",
                        captureElapsed,
                        analysisElapsed),
                    CleanupConfirmed: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new VisionAttemptResult(
                    new EvaluationAttempt(
                        isWarmup,
                        EvaluationTerminalState.Cancelled,
                        Stopwatch.GetElapsedTime(started),
                        "evaluation.cancelled",
                        captureElapsed,
                        Stopwatch.GetElapsedTime(analysisStarted)),
                    CleanupConfirmed: true);
            }
            catch
            {
                return new VisionAttemptResult(
                    new EvaluationAttempt(
                        isWarmup,
                        EvaluationTerminalState.Failure,
                        Stopwatch.GetElapsedTime(started),
                        "vision.analysis_failed",
                        captureElapsed,
                        Stopwatch.GetElapsedTime(analysisStarted)),
                    CleanupConfirmed: true);
            }
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private static VisionAttemptResult Result(
        bool isWarmup,
        EvaluationTerminalState state,
        TimeSpan elapsed,
        string errorCode,
        bool cleanupConfirmed) =>
        new(
            new EvaluationAttempt(isWarmup, state, elapsed, errorCode),
            cleanupConfirmed);
}
