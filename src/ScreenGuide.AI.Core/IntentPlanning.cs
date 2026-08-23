using System.Text.RegularExpressions;

namespace ScreenGuide.AI.Core;

public enum UniversalIntentKind
{
    Conversation,
    OpenApplication,
    OpenWebsite,
    OpenFile,
    SearchForeground,
    DescribeForeground,
    CodingTask,
    Unsupported
}

public enum IntentPlanReadiness
{
    Ready,
    NeedsContext,
    Unsupported
}

public sealed record ForegroundApplicationContext(
    long WindowHandle,
    string WindowTitle,
    string ProcessName);

public sealed record IntentPlanningContext(
    string? SelectedFilePath = null,
    Guid? SelectedProjectId = null,
    string? SelectedProjectName = null,
    ForegroundApplicationContext? ForegroundApplication = null,
    bool ForegroundObservationConsent = false);

public sealed record IntentPlan(
    Guid Id,
    UniversalIntentKind Kind,
    IntentPlanReadiness Readiness,
    string OriginalText,
    string UserSummary,
    string? Target = null,
    string? Capability = null,
    bool RequiresConfirmation = false,
    string? ConfirmationText = null,
    string? MissingContext = null,
    DateTimeOffset? ExpiresAtUtc = null,
    string? PreferredApplicationName = null);

public interface IIntentPlanner
{
    IntentPlan Plan(string text, IntentPlanningContext context);
}

/// <summary>
/// V0.2 安全子集的确定性规划器。它只识别明确、低风险的电脑动作；
/// 其他内容保持为普通咨询，不能从模型输出、网页或窗口文字推导操作授权。
/// </summary>
public sealed partial class DeterministicIntentPlanner(TimeProvider timeProvider) : IIntentPlanner
{
    private static readonly IReadOnlyDictionary<string, string> KnownWebsites =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["百度"] = "https://www.baidu.com/",
            ["必应"] = "https://www.bing.com/",
            ["B站"] = "https://www.bilibili.com/",
            ["哔哩哔哩"] = "https://www.bilibili.com/",
            ["知乎"] = "https://www.zhihu.com/",
            ["抖音"] = "https://www.douyin.com/",
            ["GitHub"] = "https://github.com/"
        };

    public IntentPlan Plan(string text, IntentPlanningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return Unsupported(text ?? string.Empty, "请说出或输入你希望电脑完成的事情。");
        }

        if (IsCodingRequest(normalized))
        {
            return context.SelectedProjectId is null
                ? NeedsContext(normalized, UniversalIntentKind.CodingTask,
                    "这是一个编程任务，请先选择已授权项目。", "已授权项目")
                : Ready(normalized, UniversalIntentKind.CodingTask,
                    $"让编程助手在“{context.SelectedProjectName ?? "所选项目"}”中处理这个任务",
                    context.SelectedProjectId.Value.ToString("D"), "coding.execute", true,
                    $"确认后，编程助手只会在“{context.SelectedProjectName ?? "所选项目"}”的授权范围内执行。 ");
        }

        if (context.SelectedFilePath is not null && IsOpenFileRequest(normalized))
        {
            var fileName = Path.GetFileName(context.SelectedFilePath);
            return Ready(normalized, UniversalIntentKind.OpenFile,
                $"用默认应用打开“{fileName}”", context.SelectedFilePath,
                "file.open", true, $"确认打开你刚刚选择的文件“{fileName}”。");
        }

        if (TryParseWebsite(normalized, out var website, out var websiteName))
        {
            if (TryParseBrowserHint(normalized, out var browserName))
            {
                return Ready(normalized, UniversalIntentKind.OpenWebsite,
                    $"用{browserName}打开{websiteName}，并把浏览器窗口显示在前台",
                    website, "browser.open.visible", true,
                    $"确认用{browserName}在新的可见窗口中打开{websiteName}。",
                    preferredApplicationName: browserName);
            }

            return Ready(normalized, UniversalIntentKind.OpenWebsite,
                $"用默认浏览器打开{websiteName}，并把浏览器窗口显示在前台",
                website, "browser.open", true,
                $"确认用默认浏览器打开 {websiteName}，并将窗口显示在前台。");
        }

        if (TryParseForegroundSearch(normalized, out var query))
        {
            if (context.ForegroundApplication is null)
            {
                return NeedsContext(normalized, UniversalIntentKind.SearchForeground,
                    "没有识别到你刚才使用的软件，请先切回目标软件。", "前台应用");
            }

            return Ready(normalized, UniversalIntentKind.SearchForeground,
                $"在“{context.ForegroundApplication.WindowTitle}”的可靠搜索框中搜索“{query}”",
                query, "desktop.search.submit", true,
                $"确认只在“{context.ForegroundApplication.WindowTitle}”中可靠识别的搜索框输入并提交“{query}”。");
        }

        if (IsDescribeForegroundRequest(normalized))
        {
            if (context.ForegroundApplication is null)
            {
                return NeedsContext(normalized, UniversalIntentKind.DescribeForeground,
                    "没有识别到你正在询问的窗口。", "前台窗口");
            }

            if (!context.ForegroundObservationConsent)
            {
                return NeedsContext(normalized, UniversalIntentKind.DescribeForeground,
                    "读取当前窗口前，需要你明确同意本次查看。", "本次窗口查看同意");
            }

            return Ready(normalized, UniversalIntentKind.DescribeForeground,
                $"查看并识别“{context.ForegroundApplication.WindowTitle}”这个窗口",
                context.ForegroundApplication.WindowHandle.ToString(), "desktop.window.describe", true,
                $"确认仅查看“{context.ForegroundApplication.WindowTitle}”这个窗口。画面只在本机内存中识别，不会截取整个桌面、不会保存截图，也不会发送到云端。 ");
        }

        if (TryParseApplication(normalized, out var applicationName))
        {
            return Ready(normalized, UniversalIntentKind.OpenApplication,
                $"打开“{applicationName}”", applicationName, "desktop.application.open", true,
                $"确认打开“{applicationName}”。");
        }

        return Ready(normalized, UniversalIntentKind.Conversation,
            "回答你的问题，不操作电脑", normalized, "conversation.reply", false);
    }

    private IntentPlan Ready(
        string text,
        UniversalIntentKind kind,
        string summary,
        string? target,
        string capability,
        bool confirmation,
        string? confirmationText = null,
        string? preferredApplicationName = null) =>
        new(Guid.NewGuid(), kind, IntentPlanReadiness.Ready, text, summary, target,
            capability, confirmation, confirmationText,
            ExpiresAtUtc: timeProvider.GetUtcNow().AddMinutes(2),
            PreferredApplicationName: preferredApplicationName);

    private IntentPlan NeedsContext(
        string text,
        UniversalIntentKind kind,
        string summary,
        string missingContext) =>
        new(Guid.NewGuid(), kind, IntentPlanReadiness.NeedsContext, text, summary,
            MissingContext: missingContext,
            ExpiresAtUtc: timeProvider.GetUtcNow().AddMinutes(2));

    private IntentPlan Unsupported(string text, string summary) =>
        new(Guid.NewGuid(), UniversalIntentKind.Unsupported, IntentPlanReadiness.Unsupported,
            text, summary, ExpiresAtUtc: timeProvider.GetUtcNow().AddMinutes(2));

    private static string Normalize(string? text) =>
        Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ");

    private static bool IsCodingRequest(string text) =>
        (text.Contains("代码", StringComparison.OrdinalIgnoreCase)
         || text.Contains("项目", StringComparison.OrdinalIgnoreCase)
         || text.Contains("bug", StringComparison.OrdinalIgnoreCase)
         || text.Contains("测试", StringComparison.OrdinalIgnoreCase)
         || text.Contains("编译", StringComparison.OrdinalIgnoreCase))
        && (text.Contains("修改", StringComparison.OrdinalIgnoreCase)
            || text.Contains("修复", StringComparison.OrdinalIgnoreCase)
            || text.Contains("检查", StringComparison.OrdinalIgnoreCase)
            || text.Contains("运行", StringComparison.OrdinalIgnoreCase)
            || text.Contains("重构", StringComparison.OrdinalIgnoreCase));

    private static bool IsOpenFileRequest(string text) =>
        text.Contains("打开", StringComparison.OrdinalIgnoreCase)
        || text.Contains("查看", StringComparison.OrdinalIgnoreCase);

    private static bool IsDescribeForegroundRequest(string text) =>
        (text.Contains("这个窗口", StringComparison.Ordinal)
         || text.Contains("这个页面", StringComparison.Ordinal)
         || text.Contains("当前窗口", StringComparison.Ordinal)
         || text.Contains("现在打开", StringComparison.Ordinal)
         || text.Contains("现在的界面", StringComparison.Ordinal)
         || text.Contains("当前界面", StringComparison.Ordinal)
         || text.Contains("屏幕上", StringComparison.Ordinal))
        && (text.Contains("看看", StringComparison.Ordinal)
            || text.Contains("看一下", StringComparison.Ordinal)
            || text.Contains("是什么", StringComparison.Ordinal)
            || text.Contains("告诉我", StringComparison.Ordinal)
            || text.Contains("读取", StringComparison.Ordinal)
            || text.Contains("识别", StringComparison.Ordinal));

    private static bool TryParseWebsite(string text, out string website, out string displayName)
    {
        website = string.Empty;
        displayName = string.Empty;
        var match = UrlRegex().Match(text);
        if (match.Success
            && Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            website = uri.AbsoluteUri;
            displayName = uri.Host;
            return text.Contains("打开", StringComparison.Ordinal)
                   || text.Contains("访问", StringComparison.Ordinal)
                   || text.Equals(match.Value, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var item in KnownWebsites)
        {
            if (text.Contains(item.Key, StringComparison.OrdinalIgnoreCase)
                && (text.Contains("打开", StringComparison.Ordinal)
                    || text.Contains("访问", StringComparison.Ordinal)
                    || text.Contains("进入", StringComparison.Ordinal)))
            {
                website = item.Value;
                displayName = item.Key;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseApplication(string text, out string applicationName)
    {
        applicationName = string.Empty;
        var match = OpenApplicationRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var value = match.Groups[1].Value.Trim(' ', '。', '！', '!', '？', '?', '"');
        if (value.Length is < 1 or > 80 || value.Contains("文件", StringComparison.Ordinal))
        {
            return false;
        }

        applicationName = value;
        return true;
    }

    private static bool TryParseBrowserHint(string text, out string browserName)
    {
        if (text.Contains("谷歌浏览器", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Google Chrome", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
        {
            browserName = "Google Chrome";
            return true;
        }

        if (text.Contains("微软浏览器", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Edge", StringComparison.OrdinalIgnoreCase))
        {
            browserName = "Microsoft Edge";
            return true;
        }

        browserName = string.Empty;
        return false;
    }

    private static bool TryParseForegroundSearch(string text, out string query)
    {
        query = string.Empty;
        var match = SearchRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        query = match.Groups[1].Value.Trim(' ', '。', '！', '!', '？', '?', '"');
        return query.Length is > 0 and <= 200;
    }

    [GeneratedRegex(@"https://[^\s，。！？]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^(?:请|帮我|请帮我)?(?:打开|启动|运行)(?:一下)?“?(.+?)”?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenApplicationRegex();

    [GeneratedRegex(@"^(?:请|帮我|请帮我)?(?:在(?:(?:当前|这个|刚才的)?(?:窗口|软件|页面|浏览器)(?:的)?(?:(?:搜索|文字|地址|输入)栏)?|(?:搜索|文字|地址|输入)栏)(?:里|中|内|里面)?)?(?:搜索|查找|搜一下|搜)(?:一下)?“?(.+?)”?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchRegex();
}
