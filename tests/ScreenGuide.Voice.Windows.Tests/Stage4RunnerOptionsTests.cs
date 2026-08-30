using ScreenGuide.Stage4.RealUsageRunner;

namespace ScreenGuide.Voice.Windows.Tests;

public sealed class Stage4RunnerOptionsTests
{
    [Fact]
    public void AcceptsOnlyModeAndExactSha()
    {
        var options = RunnerOptions.Parse(
        [
            "--mode", "voice",
            "--expected-sha", "941c2d8635939bd1329daa81f34b6829bd447750"
        ]);

        Assert.Equal("voice", options.Mode);
        Assert.Equal("941c2d8635939bd1329daa81f34b6829bd447750", options.ExpectedSha);
    }

    [Theory]
    [InlineData("--mode", "voice", "--expected-sha", "bad")]
    [InlineData("--mode", "unsupported", "--expected-sha", "941c2d8635939bd1329daa81f34b6829bd447750")]
    [InlineData("--mode", "voice", "--expected-sha", "941c2d8635939bd1329daa81f34b6829bd447750", "--output", "secret.json")]
    public void RejectsInvalidOrExpansiveArguments(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(args));
    }
}
