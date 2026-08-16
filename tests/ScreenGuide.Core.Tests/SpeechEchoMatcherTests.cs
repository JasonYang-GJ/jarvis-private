using ScreenGuide.Core;

namespace ScreenGuide.Core.Tests;

public sealed class SpeechEchoMatcherTests
{
    [Theory]
    [InlineData("停")]
    [InlineData("等一下")]
    [InlineData("你说错了")]
    [InlineData("我想问另外一个问题")]
    public void CanInterrupt_AcceptsUsefulBargeInPhrases(string text)
    {
        Assert.True(SpeechEchoMatcher.CanInterrupt(text));
    }

    [Theory]
    [InlineData("啊")]
    [InlineData("嗯")]
    [InlineData("")]
    public void CanInterrupt_RejectsShortNoise(string text)
    {
        Assert.False(SpeechEchoMatcher.CanInterrupt(text));
    }

    [Theory]
    [InlineData("正在打开微信", "好的，正在打开微信。")]
    [InlineData("这个页面是闲鱼发布界面", "我看到这个页面是闲鱼发布界面，你可以填写商品信息。")]
    public void IsLikelyAssistantEcho_DetectsPlaybackText(string recognized, string assistant)
    {
        Assert.True(SpeechEchoMatcher.IsLikelyAssistantEcho(recognized, assistant));
    }

    [Fact]
    public void IsLikelyAssistantEcho_DoesNotHideUserCorrection()
    {
        Assert.False(SpeechEchoMatcher.IsLikelyAssistantEcho(
            "不对我问的是第三题",
            "这是一张数学试卷，第一题的答案是A。"));
    }
}
