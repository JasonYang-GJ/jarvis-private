namespace ScreenGuide.Core;

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
        var normalized = SpeechTextNormalizer.Normalize(recognizedText);
        return normalized.Length >= 3
            || ImmediateInterruptions.Any(phrase => string.Equals(
                normalized,
                SpeechTextNormalizer.Normalize(phrase),
                StringComparison.Ordinal));
    }

    public static bool IsLikelyAssistantEcho(string? recognizedText, string? assistantSpeech)
    {
        var recognized = SpeechTextNormalizer.Normalize(recognizedText);
        var assistant = SpeechTextNormalizer.Normalize(assistantSpeech);
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
}
