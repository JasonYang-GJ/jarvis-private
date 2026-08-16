using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ScreenGuide.Core;

namespace ScreenGuide.App.Services;

internal sealed class BailianHybridGuideService : IGuidanceProvider
{
    private const int TextHistoryTokenBudget = 900_000;
    private const int VisionHistoryTokenBudget = 900_000;
    private const int ReservedRequestTokens = 20_000;
    private const string TextSystemPrompt = """
        你叫贾维斯，是一个面向 Windows 新手的中文语音助手。当前请求没有提供屏幕画面，不得声称看到了用户的电脑。
        先直接回答结论，再补充最必要的说明。使用自然、清楚、简短的口语，不使用 Markdown 表格。
        第一句话必须在 28 个汉字以内并以句号结束，让语音能够尽快开口；全文尽量控制在 120 个汉字内。
        遇到付款、删除、发布、提交、授权或隐私相关操作，必须提醒用户先确认。
        """;

    private const string VisionSystemPrompt = """
        你叫贾维斯，是一个面向 Windows 新手的屏幕陪练助手。你只能观察，不得声称已经替用户点击、输入或控制电脑。
        根据用户授权窗口的当前画面回答问题。先用一句话说明当前界面或结论，再只给出一个最合适的下一步。
        识别浏览器页面时，必须先读取地址栏域名、网站标志、页面主标题和关键表单文字；至少两项可见证据相互吻合后，才能判断网站或软件名称。
        不得只根据配色、相似图标或窗口标题猜测，也不得把网页中的聊天入口误认为整个网站。若证据冲突或文字看不清，直接说明看到了什么、哪里不确定。
        指导必须使用清楚、简短的中文，指出按钮或区域的可见名称；回答“这是什么界面”时，简要说出支撑判断的域名或页面文字。
        遇到付款、删除、发布、提交、授权或隐私相关操作，必须提醒用户先确认，不能催促操作。
        第一句话必须在 28 个汉字以内并以句号结束；回答适合语音朗读，不使用 Markdown 表格，尽量控制在 120 个汉字内。
        """;

    private readonly HttpClient _httpClient;
    private readonly BailianConfiguration _configuration;
    private readonly string _apiKey;
    private readonly ConversationHistory _conversationHistory = new(TextHistoryTokenBudget);

    public BailianHybridGuideService(
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
        var hasImage = request.JpegImage is { Length: > 0 };
        object userContent;
        if (hasImage)
        {
            userContent = new object[]
            {
                new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = $"data:image/jpeg;base64,{Convert.ToBase64String(request.JpegImage!)}"
                    },
                    max_pixels = 2_073_600
                },
                new
                {
                    type = "text",
                    text = $"授权窗口标题：{request.WindowTitle}\n用户问题：{request.Question}"
                }
            };
        }
        else
        {
            userContent = $"用户问题：{request.Question}";
        }

        var historyBudget = hasImage ? VisionHistoryTokenBudget : TextHistoryTokenBudget;
        var history = _conversationHistory.GetRecentTurns(
            historyBudget,
            request.Question,
            ReservedRequestTokens);
        var messages = new List<object>
        {
            new { role = "system", content = hasImage ? VisionSystemPrompt : TextSystemPrompt }
        };
        foreach (var turn in history)
        {
            messages.Add(new { role = "user", content = turn.UserText });
            messages.Add(new { role = "assistant", content = turn.AssistantText });
        }
        messages.Add(new { role = "user", content = userContent });

        var body = new
        {
            model = hasImage ? _configuration.VisionModel : _configuration.TextModel,
            messages,
            stream = true,
            stream_options = new { include_usage = true },
            enable_thinking = false,
            temperature = hasImage ? 0.2 : 0.35,
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
        var completedAnswer = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                RememberCompletedTurn(request.Question, completedAnswer);
                yield break;
            }
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line[5..].Trim();
            if (json == "[DONE]")
            {
                RememberCompletedTurn(request.Question, completedAnswer);
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
                completedAnswer.Append(text);
                yield return text;
            }
        }
    }

    private void RememberCompletedTurn(string question, StringBuilder answer)
    {
        if (answer.Length > 0)
        {
            _conversationHistory.AddTurn(question, answer.ToString());
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
