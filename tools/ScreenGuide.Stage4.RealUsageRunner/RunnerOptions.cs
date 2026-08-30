using System.Text.RegularExpressions;

namespace ScreenGuide.Stage4.RealUsageRunner;

public sealed record RunnerOptions(string Mode, string ExpectedSha)
{
    private static readonly Regex ShaPattern = new(
        "^[0-9a-f]{40}$",
        RegexOptions.CultureInvariant);

    public static RunnerOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count != 4)
        {
            throw new ArgumentException("只接受 --mode 和 --expected-sha。", nameof(args));
        }

        string? mode = null;
        string? expectedSha = null;
        for (var index = 0; index < args.Count; index += 2)
        {
            var value = args[index + 1];
            switch (args[index])
            {
                case "--mode" when mode is null:
                    mode = value;
                    break;
                case "--expected-sha" when expectedSha is null:
                    expectedSha = value;
                    break;
                default:
                    throw new ArgumentException("参数重复或不受支持。", nameof(args));
            }
        }

        if (mode is not ("voice" or "voice-diagnostic" or "vision" or "vision-diagnostic" or "vision-identity-change"))
        {
            throw new ArgumentException("评测模式不受支持。", nameof(args));
        }

        if (!ShaPattern.IsMatch(expectedSha ?? string.Empty))
        {
            throw new ArgumentException("必须提供 40 位小写 exact SHA。", nameof(args));
        }

        return new RunnerOptions(mode, expectedSha!);
    }
}
