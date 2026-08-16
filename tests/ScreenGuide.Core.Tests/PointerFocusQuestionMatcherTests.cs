using ScreenGuide.Core;

namespace ScreenGuide.Core.Tests;

public sealed class PointerFocusQuestionMatcherTests
{
    [Theory]
    [InlineData("这道题怎么写")]
    [InlineData("我鼠标指着的题选什么")]
    [InlineData("这一题答案是什么")]
    public void PointerQuestions_UseFocusedCrop(string question)
    {
        Assert.True(PointerFocusQuestionMatcher.IsMatch(question));
    }

    [Theory]
    [InlineData("这是什么网站")]
    [InlineData("当前页面在哪里")]
    [InlineData("")]
    public void GeneralScreenQuestions_KeepFullFrame(string question)
    {
        Assert.False(PointerFocusQuestionMatcher.IsMatch(question));
    }
}
