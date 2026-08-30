using System.Diagnostics;
using System.Reflection;

namespace ScreenGuide.Stage4.RealUsageRunner;

public static class BuildIdentityVerifier
{
    public static bool Matches(string? productVersion, string expectedSha) =>
        !string.IsNullOrWhiteSpace(productVersion)
        && productVersion.EndsWith("+" + expectedSha, StringComparison.Ordinal);

    public static bool MatchesCurrentExecutable(string expectedSha)
    {
        var location = Assembly.GetExecutingAssembly().Location;
        var productVersion = FileVersionInfo.GetVersionInfo(location).ProductVersion;
        return Matches(productVersion, expectedSha);
    }
}
