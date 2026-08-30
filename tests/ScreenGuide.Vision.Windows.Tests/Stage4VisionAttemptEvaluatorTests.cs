using ScreenGuide.Skills.Windows;
using ScreenGuide.Stage4.RealUsageRunner;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Vision.Windows.Tests;

public sealed class Stage4VisionAttemptEvaluatorTests
{
    [Fact]
    public async Task SuccessfulAnalysisClearsCapturedBytesAndKeepsOnlyTimingEvidence()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var evaluator = new VisionAttemptEvaluator(
            new StubCaptureService(() => new CapturedWindowFrame(bytes, 2, 2, "test")),
            new StubVisionProvider("safe canary"));

        var result = await evaluator.EvaluateAsync(
            Target(),
            "safe canary",
            isWarmup: false,
            CancellationToken.None);

        Assert.Equal(EvaluationTerminalState.Success, result.Attempt.State);
        Assert.True(result.CleanupConfirmed);
        Assert.NotNull(result.Attempt.CaptureElapsed);
        Assert.NotNull(result.Attempt.AnalysisElapsed);
        Assert.All(bytes, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task WhitespaceInsertedByLocalOcrStillMatchesExactCanaryCharacters()
    {
        var evaluator = new VisionAttemptEvaluator(
            new StubCaptureService(() => new CapturedWindowFrame([1, 2, 3, 4], 2, 2, "test")),
            new StubVisionProvider("这是 无 个人 数据 的 本机 单窗口 评测 标记"));

        var result = await evaluator.EvaluateAsync(
            Target(),
            "这是无个人数据的本机单窗口评测标记",
            isWarmup: false,
            CancellationToken.None);

        Assert.Equal(EvaluationTerminalState.Success, result.Attempt.State);
        Assert.Null(result.Attempt.ErrorCode);
    }

    [Fact]
    public async Task MissingCanaryCharacterStillFailsClosed()
    {
        var evaluator = new VisionAttemptEvaluator(
            new StubCaptureService(() => new CapturedWindowFrame([1, 2, 3, 4], 2, 2, "test")),
            new StubVisionProvider("这是 无 个人 数据 的 本机 单窗口 评测"));

        var result = await evaluator.EvaluateAsync(
            Target(),
            "这是无个人数据的本机单窗口评测标记",
            isWarmup: false,
            CancellationToken.None);

        Assert.Equal(EvaluationTerminalState.Failure, result.Attempt.State);
        Assert.Equal("vision.canary_missing", result.Attempt.ErrorCode);
    }

    [Fact]
    public async Task FormalMixedTokenStillRejectsAOneCharacterMismatch()
    {
        var evaluator = new VisionAttemptEvaluator(
            new StubCaptureService(() => new CapturedWindowFrame([1, 2, 3, 4], 2, 2, "test")),
            new StubVisionProvider("元枢4821"));

        var result = await evaluator.EvaluateAsync(
            Target(),
            VisionEvaluationContract.FormalCanary,
            isWarmup: false,
            CancellationToken.None);

        Assert.Equal(EvaluationTerminalState.Failure, result.Attempt.State);
        Assert.Equal("vision.canary_missing", result.Attempt.ErrorCode);
    }

    [Fact]
    public async Task IdentityChangeCancelsWithoutCallingAnalysis()
    {
        var provider = new StubVisionProvider("safe canary");
        var evaluator = new VisionAttemptEvaluator(
            new ThrowingCaptureService(
                new WindowIdentityException(WindowIdentityErrorCodes.Changed, "sensitive title")),
            provider);

        var result = await evaluator.EvaluateAsync(
            Target(),
            "safe canary",
            isWarmup: false,
            CancellationToken.None);

        Assert.Equal(EvaluationTerminalState.Cancelled, result.Attempt.State);
        Assert.Equal("vision.identity_changed", result.Attempt.ErrorCode);
        Assert.Equal(0, provider.CallCount);
        Assert.True(result.CleanupConfirmed);
    }

    private static WindowCaptureTarget Target() => new(
        1,
        "test",
        "test",
        1,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    private sealed class StubCaptureService(Func<CapturedWindowFrame> factory) : IWindowCaptureService
    {
        public Task<CapturedWindowFrame> CaptureAsync(
            WindowCaptureTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(factory());
    }

    private sealed class ThrowingCaptureService(Exception exception) : IWindowCaptureService
    {
        public Task<CapturedWindowFrame> CaptureAsync(
            WindowCaptureTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CapturedWindowFrame>(exception);
    }

    private sealed class StubVisionProvider(string summary) : IWindowVisionProvider
    {
        public int CallCount { get; private set; }

        public string ProviderId => "test";

        public bool SendsImageOffDevice => false;

        public Task<WindowVisionResult> AnalyzeAsync(
            WindowVisionRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new WindowVisionResult(summary, "test", [], []));
        }
    }
}
