using System.Text;

namespace ScreenGuide.Core;

public static class WakePhraseMatcher
{
    private static readonly string[] WakePhraseRemainders =
    [
        "斯",
        "维斯",
        "维思",
        "维丝",
        "贾维斯",
        "贾维思",
        "加维斯",
        "佳维斯"
    ];

    public static IReadOnlyList<string> AcceptedPhrases { get; } = new[]
    {
        "你好贾维斯",
        "你好贾维思",
        "你好贾维丝",
        "你好加维斯",
        "你好佳维斯",
        "贾维斯",
        "贾维思",
        "加维斯",
        "佳维斯"
    };

    public static bool IsMatch(string? recognizedText)
    {
        var normalized = Normalize(recognizedText);
        return AcceptedPhrases.Any(normalized.Contains);
    }

    public static bool IsOnlyWakePhrase(string? recognizedText)
    {
        var normalized = Normalize(recognizedText);
        return AcceptedPhrases.Any(phrase => string.Equals(
            normalized,
            Normalize(phrase),
            StringComparison.Ordinal));
    }

    public static bool IsOnlyWakePhraseRemainder(string? recognizedText)
    {
        var normalized = Normalize(recognizedText);
        return WakePhraseRemainders.Any(remainder => string.Equals(
            normalized,
            Normalize(remainder),
            StringComparison.Ordinal));
    }

    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
        }

        return normalized.ToString();
    }
}
