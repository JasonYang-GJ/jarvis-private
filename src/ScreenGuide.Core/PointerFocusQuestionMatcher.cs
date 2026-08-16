namespace ScreenGuide.Core;

public static class PointerFocusQuestionMatcher
{
    private static readonly string[] PointerCues =
    [
        "鼠标",
        "指针",
        "我指着",
        "指着的",
        "这道题",
        "这题",
        "这一题",
        "这个题",
        "当前这题"
    ];

    public static bool IsMatch(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return false;
        }

        return PointerCues.Any(cue => question.Contains(cue, StringComparison.Ordinal));
    }
}
