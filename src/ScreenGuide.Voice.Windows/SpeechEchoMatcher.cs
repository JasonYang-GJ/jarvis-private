using System.Text;

namespace ScreenGuide.Voice.Windows;

public static class SpeechEchoMatcher
{
    private static readonly string[] ImmediateInterruptions =
    [
        "停",
        "停一下",
        "等一下",
        "不对",
        "不是",
        "你说错了"
    ];

    public static bool CanInterrupt(string? recognizedText)
    {
        var normalized = Normalize(recognizedText);
        return normalized.Length >= 3
            || ImmediateInterruptions.Any(phrase => string.Equals(
                normalized,
                Normalize(phrase),
                StringComparison.Ordinal));
    }

    public static bool IsLikelyAssistantEcho(string? recognizedText, string? assistantSpeech)
    {
        var recognized = Normalize(recognizedText);
        var assistant = Normalize(assistantSpeech);
        if (recognized.Length < 2 || assistant.Length < 2)
        {
            return false;
        }

        if (assistant.Contains(recognized, StringComparison.Ordinal)
            || recognized.Contains(assistant, StringComparison.Ordinal))
        {
            return true;
        }

        var recognizedCharacters = recognized.ToHashSet();
        var overlap = recognizedCharacters.Count(assistant.Contains);
        return recognized.Length >= 4
            && overlap / (double)recognizedCharacters.Count >= 0.8;
    }

    private static string Normalize(string? text)
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
