using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace ScreenGuide.App.Services;

internal sealed class BailianCosyVoiceService : ICloudSpeechService
{
    private readonly BailianConfiguration _configuration;
    private readonly string _apiKey;

    public BailianCosyVoiceService(BailianConfiguration configuration, string apiKey)
    {
        _configuration = configuration;
        _apiKey = apiKey;
    }

    public async Task SpeakStreamAsync(
        IAsyncEnumerable<string> textChunks,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        using var webSocket = new ClientWebSocket();
        webSocket.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        webSocket.Options.SetRequestHeader("user-agent", "ScreenGuideTeacher/0.1");

        progress?.Invoke("正在连接百炼语音…");
        await webSocket.ConnectAsync(_configuration.SpeechWebSocketUri, cancellationToken).ConfigureAwait(false);

        var taskId = Guid.NewGuid().ToString();
        await SendJsonAsync(webSocket, new
        {
            header = new { action = "run-task", task_id = taskId, streaming = "duplex" },
            payload = new
            {
                task_group = "audio",
                task = "tts",
                function = "SpeechSynthesizer",
                model = _configuration.VoiceModel,
                parameters = new
                {
                    text_type = "PlainText",
                    voice = _configuration.VoiceName,
                    format = "pcm",
                    sample_rate = 24000,
                    volume = 75,
                    rate = 1.18,
                    pitch = 0.92,
                    language_hints = new[] { "zh" },
                    instruction = "沉稳、清晰、克制的中文智能助理语气，语速稍快"
                },
                input = new { }
            }
        }, cancellationToken).ConfigureAwait(false);

        await WaitForTaskStartedAsync(webSocket, taskId, cancellationToken).ConfigureAwait(false);

        var audio = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
        {
            BufferDuration = TimeSpan.FromMinutes(5),
            DiscardOnBufferOverflow = true
        };
        using var output = new WaveOutEvent { DesiredLatency = 100 };
        output.Init(audio);
        output.Play();

        var receiveTask = ReceiveAudioAsync(webSocket, audio, output, taskId, cancellationToken, progress);
        var anyText = false;
        await foreach (var segment in BufferTextSegmentsAsync(textChunks, cancellationToken).ConfigureAwait(false))
        {
            anyText = true;
            await SendJsonAsync(webSocket, new
            {
                header = new { action = "continue-task", task_id = taskId, streaming = "duplex" },
                payload = new { input = new { text = segment } }
            }, cancellationToken).ConfigureAwait(false);
        }

        if (!anyText)
        {
            await SendJsonAsync(webSocket, new
            {
                header = new { action = "finish-task", task_id = taskId, streaming = "duplex" },
                payload = new { input = new { directive = "cancel" } }
            }, CancellationToken.None).ConfigureAwait(false);
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch
            {
                // The useful error is that no answer text reached speech synthesis.
            }
            throw new InvalidOperationException("AI 没有返回可以朗读的文字。");
        }

        await SendJsonAsync(webSocket, new
        {
            header = new { action = "finish-task", task_id = taskId, streaming = "duplex" },
            payload = new { input = new { } }
        }, cancellationToken).ConfigureAwait(false);

        await receiveTask.ConfigureAwait(false);

        await WaitForPlaybackToFinishAsync(audio, cancellationToken, progress).ConfigureAwait(false);

        output.Stop();
        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await webSocket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "finished",
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task WaitForPlaybackToFinishAsync(
        BufferedWaveProvider audio,
        CancellationToken cancellationToken,
        Action<string>? progress)
    {
        if (audio.BufferedBytes <= 0)
        {
            return;
        }

        progress?.Invoke("正在播放剩余语音…");
        var previousBufferedBytes = audio.BufferedBytes;
        var noProgressTimer = Stopwatch.StartNew();

        while (audio.BufferedBytes > 0)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            var currentBufferedBytes = audio.BufferedBytes;
            if (currentBufferedBytes != previousBufferedBytes)
            {
                previousBufferedBytes = currentBufferedBytes;
                noProgressTimer.Restart();
                continue;
            }

            if (noProgressTimer.Elapsed >= TimeSpan.FromSeconds(10))
            {
                throw new InvalidOperationException("语音播放设备连续10秒没有进度。");
            }
        }
    }

    private static async Task WaitForTaskStartedAsync(
        ClientWebSocket webSocket,
        string taskId,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var message = await ReceiveMessageAsync(webSocket, buffer, cancellationToken).ConfigureAwait(false);
            if (message.Type == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("百炼语音连接在任务开始前关闭。");
            }

            if (message.Type != WebSocketMessageType.Text)
            {
                continue;
            }

            using var document = JsonDocument.Parse(message.Text!);
            var header = document.RootElement.GetProperty("header");
            var eventName = header.TryGetProperty("event", out var eventElement)
                ? eventElement.GetString()
                : null;
            if (eventName == "task-started"
                && header.TryGetProperty("task_id", out var receivedTaskId)
                && receivedTaskId.GetString() == taskId)
            {
                return;
            }

            if (eventName == "task-failed")
            {
                throw CreateTaskFailure(header);
            }
        }
    }

    private static async Task ReceiveAudioAsync(
        ClientWebSocket webSocket,
        BufferedWaveProvider audio,
        WaveOutEvent output,
        string taskId,
        CancellationToken cancellationToken,
        Action<string>? progress)
    {
        var buffer = new byte[64 * 1024];
        var announcedPlayback = false;
        while (true)
        {
            var message = await ReceiveMessageAsync(webSocket, buffer, cancellationToken).ConfigureAwait(false);
            if (message.Type == WebSocketMessageType.Binary)
            {
                audio.AddSamples(message.Bytes!, 0, message.Bytes!.Length);
                if (!announcedPlayback)
                {
                    announcedPlayback = true;
                    progress?.Invoke("正在用百炼语音回答…");
                }
                continue;
            }

            if (message.Type == WebSocketMessageType.Close)
            {
                if (output.PlaybackState == PlaybackState.Playing && audio.BufferedBytes > 0)
                {
                    return;
                }
                throw new InvalidOperationException("百炼语音连接意外关闭。");
            }

            using var document = JsonDocument.Parse(message.Text!);
            var header = document.RootElement.GetProperty("header");
            var eventName = header.TryGetProperty("event", out var eventElement)
                ? eventElement.GetString()
                : null;
            if (eventName == "task-failed")
            {
                throw CreateTaskFailure(header);
            }

            if (eventName == "task-finished"
                && header.TryGetProperty("task_id", out var receivedTaskId)
                && receivedTaskId.GetString() == taskId)
            {
                return;
            }
        }
    }

    private static InvalidOperationException CreateTaskFailure(JsonElement header)
    {
        var message = header.TryGetProperty("error_message", out var error)
            ? error.GetString()
            : "未知错误";
        return new InvalidOperationException($"百炼语音合成失败：{message}");
    }

    private static async IAsyncEnumerable<string> BufferTextSegmentsAsync(
        IAsyncEnumerable<string> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            buffer.Append(chunk);
            var flushAt = FindFlushPosition(buffer);
            while (flushAt >= 0)
            {
                var length = flushAt + 1;
                var segment = buffer.ToString(0, length);
                buffer.Remove(0, length);
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    yield return segment;
                }
                flushAt = FindFlushPosition(buffer);
            }
        }

        if (buffer.Length > 0 && !string.IsNullOrWhiteSpace(buffer.ToString()))
        {
            yield return buffer.ToString();
        }
    }

    private static int FindFlushPosition(StringBuilder text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is '。' or '！' or '？' or '；' or '\n')
            {
                return index;
            }

            if (index >= 24 && text[index] is ('，' or ',' or '、'))
            {
                return index;
            }
        }

        return -1;
    }

    private static Task SendJsonAsync(
        ClientWebSocket webSocket,
        object message,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        return webSocket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task<WebSocketPayload> ReceiveMessageAsync(
        ClientWebSocket webSocket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.Count > 0)
            {
                stream.Write(buffer, 0, result.Count);
            }
        }
        while (!result.EndOfMessage);

        var bytes = stream.ToArray();
        return result.MessageType switch
        {
            WebSocketMessageType.Text => new WebSocketPayload(result.MessageType, Encoding.UTF8.GetString(bytes), null),
            WebSocketMessageType.Binary => new WebSocketPayload(result.MessageType, null, bytes),
            _ => new WebSocketPayload(result.MessageType, null, null)
        };
    }

    private sealed record WebSocketPayload(WebSocketMessageType Type, string? Text, byte[]? Bytes);
}
