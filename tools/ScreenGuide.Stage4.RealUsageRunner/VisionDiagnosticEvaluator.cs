using System.Diagnostics;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Stage4.RealUsageRunner;

public sealed record VisionDiagnosticAttemptResult(
    VisionAttemptResult AttemptResult,
    VisionDiagnosticSummary Diagnostic);

public sealed class VisionDiagnosticEvaluator(
    IWindowCaptureService captureService,
    IWindowVisionProvider visionProvider)
{
    public async Task<VisionDiagnosticAttemptResult> EvaluateAsync(
        WindowCaptureTarget target,
        IReadOnlyList<VisionDiagnosticCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidates);
        if (visionProvider.SendsImageOffDevice)
        {
            return Result(
                EvaluationTerminalState.Blocked,
                TimeSpan.Zero,
                "vision.provider_not_local");
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
                    EvaluationTerminalState.Cancelled,
                    Stopwatch.GetElapsedTime(started),
                    "vision.identity_changed");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Result(
                    EvaluationTerminalState.Cancelled,
                    Stopwatch.GetElapsedTime(started),
                    "evaluation.cancelled");
            }
            catch
            {
                return Result(
                    EvaluationTerminalState.Failure,
                    Stopwatch.GetElapsedTime(started),
                    "vision.capture_failed");
            }

            var analysisStarted = Stopwatch.GetTimestamp();
            try
            {
                var frameShape = VisionFrameShapeSummary.Empty;
                try
                {
                    frameShape = VisionFrameShapeAnalyzer.Analyze(frame);
                }
                catch (ArgumentException)
                {
                    // Shape evidence is diagnostic-only. An unsupported test/frame encoding
                    // must not change the existing OCR result or terminal state.
                }
                var result = await visionProvider.AnalyzeAsync(
                        new WindowVisionRequest(target, frame),
                        cancellationToken)
                    .ConfigureAwait(false);
                var analysisElapsed = Stopwatch.GetElapsedTime(analysisStarted);
                var diagnostic = VisionDiagnosticAnalyzer.Analyze(
                    result.UserSummary,
                    candidates,
                    frameShape with
                    {
                        OcrTextDetected = string.Equals(
                            result.Confidence,
                            "LocalTextRecognized",
                            StringComparison.Ordinal)
                    });
                return new VisionDiagnosticAttemptResult(
                    new VisionAttemptResult(
                        new EvaluationAttempt(
                            false,
                            EvaluationTerminalState.Success,
                            Stopwatch.GetElapsedTime(started),
                            CaptureElapsed: captureElapsed,
                            AnalysisElapsed: analysisElapsed),
                        CleanupConfirmed: true),
                    diagnostic);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Result(
                    EvaluationTerminalState.Cancelled,
                    Stopwatch.GetElapsedTime(started),
                    "evaluation.cancelled",
                    captureElapsed,
                    Stopwatch.GetElapsedTime(analysisStarted));
            }
            catch
            {
                return Result(
                    EvaluationTerminalState.Failure,
                    Stopwatch.GetElapsedTime(started),
                    "vision.analysis_failed",
                    captureElapsed,
                    Stopwatch.GetElapsedTime(analysisStarted));
            }
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private static VisionDiagnosticAttemptResult Result(
        EvaluationTerminalState state,
        TimeSpan elapsed,
        string errorCode,
        TimeSpan? captureElapsed = null,
        TimeSpan? analysisElapsed = null) =>
        new(
            new VisionAttemptResult(
                new EvaluationAttempt(
                    false,
                    state,
                    elapsed,
                    errorCode,
                    captureElapsed,
                    analysisElapsed),
                CleanupConfirmed: true),
            VisionDiagnosticSummary.Empty);
}
