using ScreenGuide.Core;

namespace ScreenGuide.Core.Tests;

public sealed class AssistantUiCommandParserTests
{
    [Theory]
    [InlineData("设置", AssistantUiCommand.OpenSettings)]
    [InlineData("请打开设置。", AssistantUiCommand.OpenSettings)]
    [InlineData("缩小", AssistantUiCommand.Compact)]
    [InlineData("缩写", AssistantUiCommand.Compact)]
    [InlineData("展开贾维斯", AssistantUiCommand.Expand)]
    [InlineData("隐藏悬浮窗", AssistantUiCommand.Hide)]
    [InlineData("退出贾维斯", AssistantUiCommand.ExitApplication)]
    public void TryParse_RecognizesExactVoiceCommands(string text, AssistantUiCommand expected)
    {
        var matched = AssistantUiCommandParser.TryParse(text, out var command);

        Assert.True(matched);
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData("退出")]
    [InlineData("怎么缩小图片")]
    [InlineData("帮我打开系统设置页面")]
    [InlineData("关闭这个网页")]
    public void TryParse_DoesNotCaptureConversationOrUnrelatedActions(string text)
    {
        Assert.False(AssistantUiCommandParser.TryParse(text, out _));
    }
}
