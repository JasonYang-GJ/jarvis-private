using ScreenGuide.Core;

namespace ScreenGuide.Core.Tests;

public sealed class GuidanceRouteClassifierTests
{
    [Theory]
    [InlineData("这个界面下一步点哪里")]
    [InlineData("帮我看一下这个报错")]
    [InlineData("屏幕上的按钮是什么意思")]
    [InlineData("这个怎么用")]
    [InlineData("我现在在哪个界面")]
    [InlineData("这是什么网站")]
    [InlineData("告诉我图片是什么界面")]
    [InlineData("当前是什么软件")]
    [InlineData("我鼠标指着的这道题怎么写")]
    [InlineData("这一题怎么做")]
    public void ScreenQuestions_UseVision(string question)
    {
        Assert.Equal(GuidanceRoute.Vision, GuidanceRouteClassifier.Classify(question));
    }

    [Theory]
    [InlineData("GitHub 是什么")]
    [InlineData("给我讲讲人工智能")]
    [InlineData("不用看屏幕，解释一下分支")]
    [InlineData("")]
    public void GeneralQuestions_UseText(string question)
    {
        Assert.Equal(GuidanceRoute.Text, GuidanceRouteClassifier.Classify(question));
    }
}
