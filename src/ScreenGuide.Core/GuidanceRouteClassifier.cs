namespace ScreenGuide.Core;

public enum GuidanceRoute
{
    Text,
    Vision
}

public static class GuidanceRouteClassifier
{
    private static readonly string[] ExplicitTextCues =
    [
        "不要看屏幕",
        "不用看屏幕",
        "不需要看屏幕",
        "只回答问题"
    ];

    private static readonly string[] VisionCues =
    [
        "看屏幕",
        "看一下",
        "看下",
        "帮我看",
        "屏幕上",
        "画面里",
        "截图",
        "图中",
        "图片",
        "鼠标",
        "指针",
        "我指着",
        "指着的",
        "这道题",
        "这一题",
        "这个界面",
        "这个页面",
        "哪个界面",
        "什么界面",
        "哪个页面",
        "什么页面",
        "当前界面",
        "当前页面",
        "当前窗口",
        "这个窗口",
        "哪个窗口",
        "什么窗口",
        "哪个网站",
        "什么网站",
        "这是什么网站",
        "哪个软件",
        "什么软件",
        "这是什么软件",
        "这个按钮",
        "那个按钮",
        "这个图标",
        "这个提示",
        "这个报错",
        "这个弹窗",
        "点哪里",
        "点击哪里",
        "下一步点",
        "下一步怎么操作",
        "这个怎么用",
        "这个是什么",
        "这里怎么",
        "上面这个",
        "下面这个",
        "左边这个",
        "右边这个"
    ];

    public static GuidanceRoute Classify(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return GuidanceRoute.Text;
        }

        var normalized = question.Trim();
        if (ExplicitTextCues.Any(cue => normalized.Contains(cue, StringComparison.Ordinal)))
        {
            return GuidanceRoute.Text;
        }

        return VisionCues.Any(cue => normalized.Contains(cue, StringComparison.Ordinal))
            ? GuidanceRoute.Vision
            : GuidanceRoute.Text;
    }
}
