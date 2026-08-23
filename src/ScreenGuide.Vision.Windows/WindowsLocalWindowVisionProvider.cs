using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Vision.Windows;

public sealed class WindowsLocalOcrTextExtractor : ILocalOcrTextExtractor
{
    public async Task<string> ExtractAsync(
        ReadOnlyMemory<byte> pngBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            return string.Empty;
        }

        using var stream = new InMemoryRandomAccessStream();
        var encodedCopy = pngBytes.ToArray();
        try
        {
            using var writer = new DataWriter(stream.GetOutputStreamAt(0));
            writer.WriteBytes(encodedCopy);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedCopy);
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await engine.RecognizeAsync(bitmap);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Text ?? string.Empty;
    }
}

/// <summary>
/// Local-only baseline. It combines exact-window pixels, Windows OCR and the window's UIA
/// summary. It does not pretend to understand arbitrary icons, photographs or diagrams.
/// </summary>
public sealed class WindowsLocalWindowVisionProvider(ILocalOcrTextExtractor ocr) : IWindowVisionProvider
{
    public string ProviderId => "windows-local-ocr-uia-v1";

    public bool SendsImageOffDevice => false;

    public async Task<WindowVisionResult> AnalyzeAsync(
        WindowVisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = await ocr.ExtractAsync(request.Frame.PngBytes, cancellationToken)
            .ConfigureAwait(false);
        var cleanedText = Clean(text, 700);
        var appName = FriendlyApplicationName(request.Target.ProcessName);
        var interfaceKind = InferInterfaceKind(request.Target.WindowTitle, cleanedText);

        var summary = $"你现在打开的是“{request.Target.WindowTitle}”窗口，所属应用是{appName}。";
        if (!string.IsNullOrWhiteSpace(interfaceKind))
        {
            summary += $" 根据本机识别，它看起来是{interfaceKind}。";
        }

        if (!string.IsNullOrWhiteSpace(cleanedText))
        {
            summary += $" 画面中可读到的主要文字包括：{cleanedText}";
        }
        else if (!string.IsNullOrWhiteSpace(request.StructuredInterfaceSummary))
        {
            summary += $" Windows 公开的界面信息显示：{Clean(request.StructuredInterfaceSummary, 700)}";
        }
        else
        {
            summary += " 这个窗口没有提供足够的可读文字，因此暂时只能确认应用和窗口标题。";
        }

        return new WindowVisionResult(
            summary,
            string.IsNullOrWhiteSpace(cleanedText) ? "Limited" : "LocalTextRecognized",
            ["单个目标窗口画面", "Windows 本机文字识别", "Windows 界面结构（若可用）"],
            ["当前本地模式不能可靠理解纯图片、图标、视频内容或复杂空间关系", "识别结果可能遗漏被遮挡、动画中或特殊渲染的文字"]);
    }

    private static string FriendlyApplicationName(string processName) => processName.ToLowerInvariant() switch
    {
        "explorer" => "文件资源管理器",
        "msedge" => "Microsoft Edge",
        "chrome" => "Google Chrome",
        "firefox" => "Firefox",
        "winword" => "Microsoft Word",
        "excel" => "Microsoft Excel",
        "powerpnt" => "Microsoft PowerPoint",
        "notepad" => "记事本",
        "wechat" => "微信",
        _ => processName
    };

    private static string? InferInterfaceKind(string title, string text)
    {
        var source = $"{title} {text}";
        if (ContainsAny(source, "设置", "Settings")) return "设置页面";
        if (ContainsAny(source, "登录", "Sign in", "Log in")) return "登录页面（元枢不会读取或填写密码）";
        if (ContainsAny(source, "搜索结果", "Search results", "百度一下")) return "搜索或搜索结果页面";
        if (ContainsAny(source, "文件资源管理器", "此电脑", "快速访问")) return "文件浏览界面";
        if (ContainsAny(source, "收件箱", "Inbox", "邮件")) return "邮件列表或邮件阅读界面";
        if (ContainsAny(source, "新建文档", "文档", "Document")) return "文档编辑界面";
        if (ContainsAny(source, "错误", "Error", "Exception", "失败")) return "包含错误信息的界面";
        return null;
    }

    private static bool ContainsAny(string source, params string[] values) =>
        values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string Clean(string? value, int maxLength)
    {
        var normalized = Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }
}
