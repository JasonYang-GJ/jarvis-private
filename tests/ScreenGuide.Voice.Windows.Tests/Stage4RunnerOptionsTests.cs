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

    [Fact]
    public void AcceptsOneShotVoiceDiagnosticMode()
    {
        var options = RunnerOptions.Parse(
        [
            "--mode", "voice-diagnostic",
            "--expected-sha", "941c2d8635939bd1329daa81f34b6829bd447750"
        ]);

        Assert.Equal("voice-diagnostic", options.Mode);
    }

    [Theory]
    [InlineData("--mode", "voice", "--expected-sha", "bad")]
    [InlineData("--mode", "unsupported", "--expected-sha", "941c2d8635939bd1329daa81f34b6829bd447750")]
    [InlineData("--mode", "voice", "--expected-sha", "941c2d8635939bd1329daa81f34b6829bd447750", "--output", "secret.json")]
    public void RejectsInvalidOrExpansiveArguments(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(args));
    }

    [Fact]
    public void BuildIdentityMustContainTheExactExpectedSha()
    {
        const string sha = "941c2d8635939bd1329daa81f34b6829bd447750";

        Assert.True(BuildIdentityVerifier.Matches("0.5.0+" + sha, sha));
        Assert.False(BuildIdentityVerifier.Matches("0.5.0+ca724dfc558808a88063ba817e093a9703f38b71", sha));
        Assert.False(BuildIdentityVerifier.Matches("0.5.0", sha));
    }
}
