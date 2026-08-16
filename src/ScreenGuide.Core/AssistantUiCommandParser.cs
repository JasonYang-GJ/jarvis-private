namespace ScreenGuide.Core;

public enum AssistantUiCommand
{
    OpenSettings,
    Compact,
    Expand,
    Hide,
    ExitApplication
}

public static class AssistantUiCommandParser
{
    private static readonly IReadOnlyDictionary<string, AssistantUiCommand> Commands =
        new Dictionary<string, AssistantUiCommand>(StringComparer.Ordinal)
        {
            ["设置"] = AssistantUiCommand.OpenSettings,
            ["打开设置"] = AssistantUiCommand.OpenSettings,
            ["请打开设置"] = AssistantUiCommand.OpenSettings,
            ["进入设置"] = AssistantUiCommand.OpenSettings,
            ["显示设置"] = AssistantUiCommand.OpenSettings,
            ["缩小"] = AssistantUiCommand.Compact,
            ["缩写"] = AssistantUiCommand.Compact,
            ["缩小贾维斯"] = AssistantUiCommand.Compact,
            ["贾维斯缩小"] = AssistantUiCommand.Compact,
            ["变小"] = AssistantUiCommand.Compact,
            ["小一点"] = AssistantUiCommand.Compact,
            ["展开"] = AssistantUiCommand.Expand,
            ["展开贾维斯"] = AssistantUiCommand.Expand,
            ["贾维斯展开"] = AssistantUiCommand.Expand,
            ["放大"] = AssistantUiCommand.Expand,
            ["变大"] = AssistantUiCommand.Expand,
            ["恢复大小"] = AssistantUiCommand.Expand,
            ["隐藏"] = AssistantUiCommand.Hide,
            ["隐藏贾维斯"] = AssistantUiCommand.Hide,
            ["隐藏悬浮窗"] = AssistantUiCommand.Hide,
            ["退出贾维斯"] = AssistantUiCommand.ExitApplication,
            ["关闭贾维斯"] = AssistantUiCommand.ExitApplication,
            ["彻底退出"] = AssistantUiCommand.ExitApplication,
            ["退出软件"] = AssistantUiCommand.ExitApplication,
            ["关闭软件"] = AssistantUiCommand.ExitApplication
        };

    public static bool TryParse(string? recognizedText, out AssistantUiCommand command)
    {
        var normalized = WakePhraseMatcher.Normalize(recognizedText);
        return Commands.TryGetValue(normalized, out command);
    }
}
