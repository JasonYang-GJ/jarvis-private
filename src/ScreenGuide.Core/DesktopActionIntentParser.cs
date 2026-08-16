namespace ScreenGuide.Core;

public enum DesktopActionKind
{
    OpenTarget,
    SearchForeground
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
        "请用谷歌打开",
        "用谷歌打开",
        "请打开",
        "打开"
    ];

    public static bool TryParse(string? command, out DesktopActionIntent? intent)
    {
        intent = null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var normalized = Normalize(command);
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
        foreach (var browserName in new[] { "谷歌浏览器", "谷歌", "Chrome", "chrome" })
        {
            if (target.StartsWith(browserName, StringComparison.Ordinal))
            {
                return target[browserName.Length..].TrimStart('打', '开');
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
}
