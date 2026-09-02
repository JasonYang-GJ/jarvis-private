using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopSearchErrorMappingTests
{
    [Theory]
    [InlineData(DesktopSearchErrorCodes.InvalidQuery)]
    [InlineData(DesktopSearchErrorCodes.CandidateNotFound)]
    [InlineData(DesktopSearchErrorCodes.CandidateAmbiguous)]
    [InlineData(DesktopSearchErrorCodes.TargetChanged)]
    [InlineData(DesktopSearchErrorCodes.WriteVerificationFailed)]
    [InlineData(DesktopSearchErrorCodes.FocusChanged)]
    public void StableSearchErrorsPassThroughWithoutRawUiContent(string code)
    {
        const string safeMessage = "搜索操作已安全停止。";
        var error = DesktopApiErrors.FromException(new DesktopSearchException(code, safeMessage));

        Assert.Equal(code, error.Code);
        Assert.Equal(safeMessage, error.UserMessage);
        Assert.DoesNotContain("query-secret", error.TechnicalDetail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-control-name", error.TechnicalDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("应用可能已经启动，但窗口验证失败。")]
    [InlineData("application launch failed before window verification")]
    [InlineData("desktop start failed")]
    public void DesktopFailureTextNeverSelectsCodexErrorCategory(string message)
    {
        var error = DesktopApiErrors.FromException(new InvalidOperationException(message));

        Assert.Equal("operation_invalid", error.Code);
        Assert.DoesNotContain("Codex", error.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypedApplicationWindowVerificationFailureKeepsStableDesktopError()
    {
        var error = DesktopApiErrors.FromException(new InstalledApplicationResolutionException(
            InstalledApplicationErrorCodes.TargetChanged,
            "目标应用窗口未通过可信身份验证，本次操作已安全停止。"));

        Assert.Equal(InstalledApplicationErrorCodes.TargetChanged, error.Code);
        Assert.DoesNotContain("Codex", error.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypedCodexTaskStartFailureKeepsCodexError()
    {
        var error = DesktopApiErrors.FromException(new CodexTaskStartException(
            new InvalidOperationException("process failed without diagnostic keywords")));

        Assert.Equal("codex_start_failed", error.Code);
        Assert.Equal("Codex 没有成功启动。", error.UserMessage);
    }

}
