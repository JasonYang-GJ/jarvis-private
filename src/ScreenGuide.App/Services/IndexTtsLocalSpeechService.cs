using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using NAudio.Wave;

namespace ScreenGuide.App.Services;

internal sealed class IndexTtsLocalSpeechService
{
    private readonly HttpClient _client;

    public IndexTtsLocalSpeechService(Uri serviceUri)
    {
        _client = new HttpClient
        {
            BaseAddress = serviceUri,
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    public async Task SpeakTextAsync(
        string text,
        CancellationToken cancellationToken,
        Action<string>? progress)
    {
        using var healthCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        healthCancellation.CancelAfter(TimeSpan.FromMilliseconds(800));
        using var health = await _client.GetAsync("health", healthCancellation.Token).ConfigureAwait(false);
        health.EnsureSuccessStatusCode();

        progress?.Invoke("正在用本机 IndexTTS2.5 生成语音…");
        using var response = await _client.PostAsJsonAsync(
            "synthesize",
            new { text, lang = "ZH", duration_factor = 0.88 },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var audioStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new WaveFileReader(audioStream);
        using var output = new WaveOutEvent { DesiredLatency = 100 };
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, eventArgs) =>
        {
            if (eventArgs.Exception is not null)
            {
                finished.TrySetException(eventArgs.Exception);
            }
            else
            {
                finished.TrySetResult();
            }
        };
        using var cancellationRegistration = cancellationToken.Register(output.Stop);
        output.Init(reader);
        progress?.Invoke("正在用本机 IndexTTS2.5 回答…");
        output.Play();
        await finished.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class LocalFirstSpeechService : ICloudSpeechService
{
    private readonly IndexTtsLocalSpeechService _local;
    private readonly ICloudSpeechService _fallback;

    public LocalFirstSpeechService(IndexTtsLocalSpeechService local, ICloudSpeechService fallback)
    {
        _local = local;
        _fallback = fallback;
    }

    public async Task SpeakStreamAsync(
        IAsyncEnumerable<string> textChunks,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        var text = new StringBuilder();
        await foreach (var chunk in textChunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            text.Append(chunk);
        }

        if (text.Length == 0)
        {
            throw new InvalidOperationException("AI 没有返回可以朗读的文字。");
        }

        try
        {
            await _local.SpeakTextAsync(text.ToString(), cancellationToken, progress).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            progress?.Invoke("本机新音色暂未就绪，正在改用百炼语音…");
            await _fallback.SpeakStreamAsync(
                OneChunk(text.ToString()),
                cancellationToken,
                progress).ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<string> OneChunk(string text)
    {
        yield return text;
        await Task.CompletedTask;
    }
}
