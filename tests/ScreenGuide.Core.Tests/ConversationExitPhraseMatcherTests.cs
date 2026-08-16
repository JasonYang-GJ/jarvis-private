using ScreenGuide.Core;

namespace ScreenGuide.Core.Tests;

public sealed class ConversationExitPhraseMatcherTests
{
    [Theory]
    [InlineData("你退下吧")]
    [InlineData("退下")]
    [InlineData("退出")]
    [InlineData("退出对话")]
    [InlineData("结束对话。")]
    public void ExactExitCommands_EndContinuousConversation(string text)
    {
        Assert.True(ConversationExitPhraseMatcher.IsMatch(text));
    }

    [Theory]
    [InlineData("怎么退出这个页面")]
    [InlineData("退出按钮在哪里")]
    [InlineData("你不要退出")]
    [InlineData("继续回答")]
    public void OrdinaryQuestions_DoNotEndContinuousConversation(string text)
    {
        Assert.False(ConversationExitPhraseMatcher.IsMatch(text));
    }
}
