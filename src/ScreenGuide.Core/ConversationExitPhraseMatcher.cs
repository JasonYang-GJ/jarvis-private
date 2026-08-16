namespace ScreenGuide.Core;

public static class ConversationExitPhraseMatcher
{
    private static readonly string[] ExitPhrases =
    [
        "你退下吧",
        "退下吧",
        "退下",
        "退出",
        "退出对话",
        "结束对话",
        "停止对话",
        "你可以退下了",
        "你可以退出了"
    ];

    public static bool IsMatch(string? recognizedText)
    {
        var normalized = WakePhraseMatcher.Normalize(recognizedText);
        return ExitPhrases.Any(phrase => string.Equals(
            normalized,
            WakePhraseMatcher.Normalize(phrase),
            StringComparison.Ordinal));
    }
}
