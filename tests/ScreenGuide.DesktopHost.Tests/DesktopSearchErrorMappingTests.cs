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
}
