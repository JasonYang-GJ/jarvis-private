using NAudio;
using NAudio.Wave;
using SherpaOnnx;

namespace ScreenGuide.Voice.Windows;

public enum ContinuousVoiceState
{
    Stopped,
    Listening,
    HearingSpeech,
    Faulted
}

public sealed record ContinuousVoiceStatus(
    ContinuousVoiceState State,
    string Message);

public sealed record VoiceUtterance(
    string Text,
    TimeSpan Duration);

public interface IContinuousVoiceListener : IAsyncDisposable
{
    bool IsListening { get; }

    event EventHandler<string>? PartialTextChanged;

    event EventHandler<VoiceUtterance>? UtteranceRecognized;

    event EventHandler<ContinuousVoiceStatus>? StateChanged;

    VoiceCapabilityReport GetCapabilityReport();

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

/// <summary>
/// Keeps the microphone open while the visible desktop client is active. Audio is decoded
/// locally in memory, is never written to disk, and each endpoint is emitted as one utterance.
/// </summary>
public sealed class OfflineContinuousVoiceListener : IContinuousVoiceListener
{
    private readonly object _sync = new();
    private readonly LocalVoiceModelPaths _paths;
    private WaveInEvent? _microphone;
    private OnlineRecognizer? _recognizer;
    private OnlineStream? _stream;
    private DateTimeOffset? _utteranceStartedAt;
    private string _partialText = string.Empty;
    private bool _stopping;

    public OfflineContinuousVoiceListener(LocalVoiceModelPaths? paths = null)
    {
        _paths = paths ?? LocalVoiceModelPaths.Create();
    }

    public bool IsListening
    {
        get
        {
            lock (_sync)
            {
                return _microphone is not null && !_stopping;
            }
        }
    }

    public event EventHandler<string>? PartialTextChanged;

    public event EventHandler<VoiceUtterance>? UtteranceRecognized;

    public event EventHandler<ContinuousVoiceStatus>? StateChanged;

    public VoiceCapabilityReport GetCapabilityReport()
    {
        var microphones = WaveInEvent.DeviceCount;
        var outputAvailable = WindowsSpeechOutput.HasChineseVoice();
        var modelAvailable = _paths.IsComplete;
        var message = !modelAvailable
            ? "本地中文语音模型不完整。"
            : microphones == 0
                ? "没有检测到可用麦克风。"
                : "本机离线语音识别已就绪，音频只在内存中处理。";
        return new VoiceCapabilityReport(
            modelAvailable,
            modelAvailable ? "本机离线中文识别" : "不可用",
            microphones,
            outputAvailable,
            message);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_microphone is not null)
            {
                return Task.CompletedTask;
            }

            if (!_paths.IsComplete)
            {
                throw new FileNotFoundException(
                    "本地中文语音模型不完整："
                    + string.Join("、", _paths.MissingFiles.Select(Path.GetFileName)));
            }

            if (WaveInEvent.DeviceCount == 0)
            {
                throw new InvalidOperationException("没有检测到可用麦克风。 ");
            }

            try
            {
                _recognizer = OfflineVoiceRecognizerFactory.Create(_paths);
                _stream = _recognizer.CreateStream();
                _partialText = string.Empty;
                _utteranceStartedAt = null;
                _stopping = false;
                cancellationToken.ThrowIfCancellationRequested();
                _microphone = new WaveInEvent
                {
                    DeviceNumber = 0,
                    WaveFormat = new WaveFormat(OfflineVoiceRecognizerFactory.SampleRate, 16, 1),
                    BufferMilliseconds = 80,
                    NumberOfBuffers = 4
                };
                _microphone.DataAvailable += OnAudio;
                _microphone.RecordingStopped += OnRecordingStopped;
                _microphone.StartRecording();
            }
            catch
            {
                DisposeCapture();
                throw;
            }
        }

        RaiseState(ContinuousVoiceState.Listening, "正在聆听");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        WaveInEvent? microphone;
        lock (_sync)
        {
            microphone = _microphone;
            _stopping = true;
        }

        if (microphone is not null)
        {
            try
            {
                microphone.StopRecording();
            }
            catch (MmException)
            {
                // The input device may already have stopped or been unplugged.
            }
        }

        lock (_sync)
        {
            DisposeCapture();
        }

        RaiseState(ContinuousVoiceState.Stopped, "语音监听已停止");
        return Task.CompletedTask;
    }

    private void OnAudio(object? sender, WaveInEventArgs eventArgs)
    {
        string? changedText = null;
        VoiceUtterance? completed = null;
        lock (_sync)
        {
            if (_stream is null || _recognizer is null || _stopping)
            {
                return;
            }

            var samples = new float[eventArgs.BytesRecorded / 2];
            for (var index = 0; index < samples.Length; index++)
            {
                var offset = index * 2;
                var value = (short)(eventArgs.Buffer[offset] | (eventArgs.Buffer[offset + 1] << 8));
                samples[index] = value / 32768F;
            }

            _stream.AcceptWaveform(OfflineVoiceRecognizerFactory.SampleRate, samples);
            while (_recognizer.IsReady(_stream))
            {
                _recognizer.Decode(_stream);
            }

            var text = _recognizer.GetResult(_stream).Text.Trim();
            if (!string.Equals(text, _partialText, StringComparison.Ordinal))
            {
                _partialText = text;
                changedText = text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _utteranceStartedAt ??= DateTimeOffset.UtcNow;
                }
            }

            if (_recognizer.IsEndpoint(_stream))
            {
                var finalText = _recognizer.GetResult(_stream).Text.Trim();
                var duration = _utteranceStartedAt is { } startedAt
                    ? DateTimeOffset.UtcNow - startedAt
                    : TimeSpan.Zero;
                _recognizer.Reset(_stream);
                _partialText = string.Empty;
                _utteranceStartedAt = null;
                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    completed = new VoiceUtterance(finalText, duration);
                }
            }
        }

        if (changedText is not null)
        {
            PartialTextChanged?.Invoke(this, changedText);
            if (!string.IsNullOrWhiteSpace(changedText))
            {
                RaiseState(ContinuousVoiceState.HearingSpeech, "正在听你说话");
            }
        }

        if (completed is not null)
        {
            UtteranceRecognized?.Invoke(this, completed);
            RaiseState(ContinuousVoiceState.Listening, "正在聆听");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is null || _stopping)
        {
            return;
        }

        lock (_sync)
        {
            DisposeCapture();
        }

        RaiseState(ContinuousVoiceState.Faulted, "麦克风连接已中断，重新打开元枢后会再次连接。");
    }

    private void DisposeCapture()
    {
        var microphone = _microphone;
        _microphone = null;
        if (microphone is not null)
        {
            microphone.DataAvailable -= OnAudio;
            microphone.RecordingStopped -= OnRecordingStopped;
            microphone.Dispose();
        }

        _stream?.Dispose();
        _recognizer?.Dispose();
        _stream = null;
        _recognizer = null;
        _partialText = string.Empty;
        _utteranceStartedAt = null;
        _stopping = false;
    }

    private void RaiseState(ContinuousVoiceState state, string message) =>
        StateChanged?.Invoke(this, new ContinuousVoiceStatus(state, message));

    public async ValueTask DisposeAsync() =>
        await StopAsync().ConfigureAwait(false);
}
