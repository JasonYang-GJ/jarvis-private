namespace ScreenGuide.Core;

public enum DesktopActionKind
{
    OpenTarget,
    SearchForeground,
    PrepareFirstImage,
    InvokeForeground
}

public enum DesktopBrowserPreference
{
    Default,
    GoogleChrome
}

public sealed record DesktopActionIntent(
    DesktopActionKind Kind,
    string Target,
    DesktopBrowserPreference Browser = DesktopBrowserPreference.Default);

public static class DesktopActionIntentParser
{
    private static readonly string[] SearchPrefixes =
    [
        "请帮我搜索",
        "帮我搜索",
        "请搜索",
        "搜索",
        "请帮我搜一下",
        "帮我搜一下",
        "搜一下"
    ];

    private static readonly string[] OpenPrefixes =
    [
        "请帮我打开",
        "帮我打开",
        "请在谷歌浏览器里打开",
        "在谷歌浏览器里打开",
        "请在谷歌浏览器打开",
        "在谷歌浏览器打开",
        "请用Chrome打开",
        "用Chrome打开",
        "请用谷歌打开",
        "用谷歌打开",
        "请打开",
        "打开"
    ];

    private static readonly string[] InvokePrefixes =
    [
        "请帮我点击",
        "帮我点击",
        "请点击",
        "点击",
        "请帮我选择",
        "帮我选择",
        "请选择",
        "选择"
    ];

    public static bool TryParse(string? command, out DesktopActionIntent? intent)
    {
        intent = null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var normalized = Normalize(command);
        if (LooksLikeImagePreparation(normalized))
        {
            intent = new DesktopActionIntent(DesktopActionKind.PrepareFirstImage, DetectImageLocation(normalized));
            return true;
        }

        if (normalized is "发送" or "确认发送")
        {
            intent = new DesktopActionIntent(DesktopActionKind.InvokeForeground, "发送");
            return true;
        }

        foreach (var prefix in InvokePrefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var target = normalized[prefix.Length..].TrimStart('按', '钮', '：', ':');
            if (!string.IsNullOrWhiteSpace(target) && !LooksLikeExplanation(target))
            {
                intent = new DesktopActionIntent(DesktopActionKind.InvokeForeground, target);
                return true;
            }
        }

        foreach (var prefix in SearchPrefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var query = normalized[prefix.Length..].TrimStart('一', '下', '：', ':');
            if (!string.IsNullOrWhiteSpace(query))
            {
                intent = new DesktopActionIntent(DesktopActionKind.SearchForeground, query);
                return true;
            }
        }

        foreach (var prefix in OpenPrefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var target = normalized[prefix.Length..];
            var useChrome = prefix.Contains("谷歌", StringComparison.Ordinal)
                || prefix.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("谷歌浏览器", StringComparison.Ordinal)
                || target.StartsWith("谷歌", StringComparison.Ordinal)
                || target.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase);
            target = TrimBrowserName(target);
            if (string.IsNullOrWhiteSpace(target) && useChrome)
            {
                target = "谷歌浏览器";
            }

            if (!string.IsNullOrWhiteSpace(target) && !LooksLikeExplanation(target))
            {
                intent = new DesktopActionIntent(
                    DesktopActionKind.OpenTarget,
                    target,
                    useChrome ? DesktopBrowserPreference.GoogleChrome : DesktopBrowserPreference.Default);
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string command)
    {
        return command.Trim()
            .TrimEnd('。', '！', '!', '？', '?')
            .Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static string TrimBrowserName(string target)
    {
        target = target.TrimStart('的', '里', '面');
        foreach (var browserName in new[] { "谷歌浏览器", "谷歌", "Chrome", "chrome" })
        {
            if (target.StartsWith(browserName, StringComparison.Ordinal))
            {
                return target[browserName.Length..].TrimStart('的', '里', '面', '打', '开');
            }
        }

        return target;
    }

    private static bool LooksLikeExplanation(string target)
    {
        return target.Contains("什么", StringComparison.Ordinal)
            || target.Contains("怎么", StringComparison.Ordinal)
            || target.Contains("为什么", StringComparison.Ordinal)
            || target.Contains("意思", StringComparison.Ordinal)
            || target.Contains("是否", StringComparison.Ordinal)
            || target.Contains("能不能", StringComparison.Ordinal)
            || target.Contains("可以", StringComparison.Ordinal)
            || target.EndsWith("吗", StringComparison.Ordinal);
    }

    private static bool LooksLikeImagePreparation(string command)
    {
        return (command.Contains("照片", StringComparison.Ordinal)
                || command.Contains("图片", StringComparison.Ordinal))
            && (command.Contains("找到", StringComparison.Ordinal)
                || command.Contains("查找", StringComparison.Ordinal)
                || command.Contains("第一张", StringComparison.Ordinal))
            && !command.Contains("怎么", StringComparison.Ordinal)
            && !command.EndsWith("吗", StringComparison.Ordinal);
    }

    private static string DetectImageLocation(string command)
    {
        if (command.Contains("桌面", StringComparison.Ordinal))
        {
            return "桌面";
        }
        if (command.Contains("下载", StringComparison.Ordinal))
        {
            return "下载";
        }
        if (command.Contains("C盘", StringComparison.OrdinalIgnoreCase)
            || command.Contains("C：", StringComparison.OrdinalIgnoreCase)
            || command.Contains("C:", StringComparison.OrdinalIgnoreCase))
        {
            return "C盘图片";
        }

        return "图片";
    }
}
