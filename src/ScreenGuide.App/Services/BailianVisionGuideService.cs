using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ScreenGuide.App.Services;

internal sealed class BailianVisionGuideService : IGuidanceProvider
{
    private const string SystemPrompt = """
        你叫贾维斯，是一个面向 Windows 新手的屏幕陪练助手。你只能观察，不得声称已经替用户点击、输入或控制电脑。
        根据用户授权窗口的当前画面回答问题。先用一句话说明当前界面或结论，再只给出一个最合适的下一步。
        指导必须使用清楚、简短的中文，指出按钮或区域的可见名称；不确定时直接说不确定并要求用户核对。
        遇到付款、删除、发布、提交、授权或隐私相关操作，必须提醒用户先确认，不能催促操作。
        回答适合语音朗读，不使用 Markdown 表格，尽量控制在 120 个汉字内。
        """;

    private readonly HttpClient _httpClient;
    private readonly BailianConfiguration _configuration;
    private readonly string _apiKey;

    public BailianVisionGuideService(
        BailianConfiguration configuration,
        string apiKey,
        HttpClient? httpClient = null)
    {
        _configuration = configuration;
        _apiKey = apiKey;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public async IAsyncEnumerable<string> StreamAnswerAsync(
        GuidanceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var userContent = new List<object>();
        if (request.JpegImage is { Length: > 0 })
        {
            userContent.Add(new
            {
                type = "image_url",
                image_url = new
                {
                    url = $"data:image/jpeg;base64,{Convert.ToBase64String(request.JpegImage)}"
                },
                max_pixels = 1_572_864
            });
        }

        userContent.Add(new
        {
            type = "text",
            text = $"授权窗口标题：{request.WindowTitle}\n用户问题：{request.Question}"
        });

        var body = new
        {
            model = _configuration.VisionModel,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = userContent }
            },
            stream = true,
            stream_options = new { include_usage = true },
            enable_thinking = false,
            temperature = 0.2,
            max_tokens = 320
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, _configuration.ChatCompletionsUri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"百炼返回 {(int)response.StatusCode}：{ExtractSafeError(details)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line[5..].Trim();
            if (json == "[DONE]")
            {
                yield break;
            }

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("delta", out var delta)
                || !delta.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = content.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }
    }

    private static string ExtractSafeError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? "请求失败";
            }
        }
        catch (JsonException)
        {
            // Use a generic message rather than echoing arbitrary server output.
        }

        return "请求失败，请检查 API Key、账户余额和网络。";
    }
}
