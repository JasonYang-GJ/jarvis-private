using System.Runtime.InteropServices;

namespace ScreenGuide.Stage4.RealUsageRunner;

internal static class EvaluationEnvironmentFactory
{
    public static EvaluationEnvironment Create(
        bool capabilityAvailable,
        string microphoneBucket) =>
        new(
            Environment.OSVersion.Version.ToString(),
            RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                _ => "unknown"
            },
            capabilityAvailable,
            microphoneBucket);
}
