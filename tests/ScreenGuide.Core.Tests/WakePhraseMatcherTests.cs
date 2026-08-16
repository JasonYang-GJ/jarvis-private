using ScreenGuide.Core;

namespace ScreenGuide.Core.Tests;

public sealed class WakePhraseMatcherTests
{
    [Theory]
    [InlineData("你好贾维斯")]
    [InlineData("你好，贾维斯。")]
    [InlineData(" 你好 贾维斯 ")]
    [InlineData("你好贾维思")]
    [InlineData("你好加维斯")]
    [InlineData("你好佳维斯，我有问题")]
    [InlineData("贾维斯")]
    public void AcceptsWakePhraseAndCommonRecognitionVariants(string text)
    {
        Assert.True(WakePhraseMatcher.IsMatch(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("你好")]
    [InlineData("我现在在哪个界面")]
    public void RejectsOrdinarySpeech(string? text)
    {
        Assert.False(WakePhraseMatcher.IsMatch(text));
    }

    [Theory]
    [InlineData("你好贾维斯")]
    [InlineData("你好，贾维斯。")]
    [InlineData("贾维斯")]
    [InlineData("贾维思")]
    public void IdentifiesWakePhraseWithoutAQuestion(string text)
    {
        Assert.True(WakePhraseMatcher.IsOnlyWakePhrase(text));
    }

    [Theory]
    [InlineData("你好贾维斯，我现在在哪个界面")]
    [InlineData("贾维斯帮我看看这个页面")]
    [InlineData("我现在在哪个界面")]
    public void DoesNotTreatQuestionAsWakePhraseOnly(string text)
    {
        Assert.False(WakePhraseMatcher.IsOnlyWakePhrase(text));
    }
}
