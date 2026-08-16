using ScreenGuide.Core;

namespace ScreenGuide.Core.Tests;

public sealed class DesktopActionIntentParserTests
{
    [Theory]
    [InlineData("打开抖音", "抖音", DesktopBrowserPreference.Default)]
    [InlineData("打开谷歌抖音", "抖音", DesktopBrowserPreference.GoogleChrome)]
    [InlineData("用谷歌打开抖音", "抖音", DesktopBrowserPreference.GoogleChrome)]
    [InlineData("打开谷歌浏览器的抖音", "抖音", DesktopBrowserPreference.GoogleChrome)]
    [InlineData("在谷歌浏览器里打开抖音", "抖音", DesktopBrowserPreference.GoogleChrome)]
    [InlineData("用Chrome打开抖音", "抖音", DesktopBrowserPreference.GoogleChrome)]
    [InlineData("打开谷歌浏览器", "谷歌浏览器", DesktopBrowserPreference.GoogleChrome)]
    public void OpenCommands_AreParsed(
        string command,
        string target,
        DesktopBrowserPreference browser)
    {
        Assert.True(DesktopActionIntentParser.TryParse(command, out var intent));
        Assert.NotNull(intent);
        Assert.Equal(DesktopActionKind.OpenTarget, intent.Kind);
        Assert.Equal(target, intent.Target);
        Assert.Equal(browser, intent.Browser);
    }

    [Theory]
    [InlineData("搜索周杰伦", "周杰伦")]
    [InlineData("帮我搜索 周杰伦", "周杰伦")]
    [InlineData("搜一下周杰伦。", "周杰伦")]
    public void SearchCommands_AreParsed(string command, string query)
    {
        Assert.True(DesktopActionIntentParser.TryParse(command, out var intent));
        Assert.NotNull(intent);
        Assert.Equal(DesktopActionKind.SearchForeground, intent.Kind);
        Assert.Equal(query, intent.Target);
    }

    [Theory]
    [InlineData("打开此电脑，找到C盘里面的照片，把第一张照片发给我", "C盘图片")]
    [InlineData("找到图片文件夹里的第一张图片", "图片")]
    [InlineData("查找桌面的第一张照片", "桌面")]
    public void ImagePreparationCommands_AreParsed(string command, string location)
    {
        Assert.True(DesktopActionIntentParser.TryParse(command, out var intent));
        Assert.NotNull(intent);
        Assert.Equal(DesktopActionKind.PrepareFirstImage, intent.Kind);
        Assert.Equal(location, intent.Target);
    }

    [Theory]
    [InlineData("点击发送", "发送")]
    [InlineData("请选择同意", "同意")]
    [InlineData("确认发送", "发送")]
    public void ForegroundSelectionCommands_AreParsed(string command, string target)
    {
        Assert.True(DesktopActionIntentParser.TryParse(command, out var intent));
        Assert.NotNull(intent);
        Assert.Equal(DesktopActionKind.InvokeForeground, intent.Kind);
        Assert.Equal(target, intent.Target);
    }

    [Theory]
    [InlineData("怎么在抖音里搜索周杰伦")]
    [InlineData("打开是什么意思")]
    [InlineData("打开抖音可以吗")]
    [InlineData("给我讲讲周杰伦")]
    public void Explanations_DoNotTriggerActions(string command)
    {
        Assert.False(DesktopActionIntentParser.TryParse(command, out _));
    }
}
