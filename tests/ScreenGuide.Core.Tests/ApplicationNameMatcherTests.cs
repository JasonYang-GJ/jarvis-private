using ScreenGuide.Core;

namespace ScreenGuide.Core.Tests;

public sealed class ApplicationNameMatcherTests
{
    [Theory]
    [InlineData("微信", "微信", 100)]
    [InlineData("剪映", "剪映专业版", 85)]
    [InlineData("GitHub Desktop", "GitHub Desktop", 100)]
    [InlineData("Chrome", "Google Chrome", 70)]
    public void Score_MatchesInstalledApplicationNames(string requested, string candidate, int expected)
    {
        Assert.Equal(expected, ApplicationNameMatcher.Score(requested, candidate));
    }

    [Theory]
    [InlineData("微信", "卸载微信")]
    [InlineData("剪映", "卸载剪映专业版")]
    [InlineData("记事本", "计算器")]
    [InlineData("记事本附加无关语义", "记事本")]
    [InlineData("把运行作为附加说明的其他目标", "运行")]
    public void Score_RejectsUninstallersAndUnrelatedApps(string requested, string candidate)
    {
        Assert.Equal(0, ApplicationNameMatcher.Score(requested, candidate));
    }
}
