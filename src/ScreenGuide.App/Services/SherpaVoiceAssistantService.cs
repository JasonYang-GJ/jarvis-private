using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using NAudio;
using NAudio.Wave;
using ScreenGuide.Core;
using SherpaOnnx;

namespace ScreenGuide.App.Services;

internal sealed class SherpaVoiceAssistantService : IAsyncDisposable
{
    private const int SampleRate = 16000;
    private const int QuestionTimeoutSeconds = 10;
    private const string WakeWordTokens = "n ǐ h ǎo j iǎ w éi s ī :2.0 #0.18 @你好贾维斯";

    private readonly object _lifecycleLock = new();
    private readonly object _modelLock = new();
    private readonly LocalVoiceModelPaths _paths;

    private CancellationTokenSource? _cancellation;
    private Channel<float[]>? _audioChannel;
    private Task? _workerTask;
    private WaveInEvent? _microphone;
    private KeywordSpotter? _keywordSpotter;
    private OnlineRecognizer? _recognizer;
    private OnlineStream? _keywordStream;
    private OnlineStream? _recognitionStream;
    private volatile VoiceListeningState _state = VoiceListeningState.Stopped;
    private readonly Stopwatch _questionTimer = new();
    private int _microphoneSignalReported;

    public SherpaVoiceAssistantService(LocalVoiceModelPaths paths)
    {
        _paths = paths;
    }

    public event EventHandler? WakeWordDetected;
    public event EventHandler<VoiceQuestionEventArgs>? QuestionRecognized;
    public event EventHandler<VoiceStatusEventArgs>? StatusChanged;
    public event EventHandler<VoiceStatusEventArgs>? Failed;

    public bool IsRunning => _state is not VoiceListeningState.Stopped;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                return;
            }

            if (!_paths.IsComplete)
            {
                throw new FileNotFoundException(
                    $"本地语音模型不完整：{string.Join(", ", _paths.MissingFiles.Select(Path.GetFileName))}");
            }

            _state = VoiceListeningState.Starting;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        try
        {
            await Task.Run(InitializeModels, cancellationToken).ConfigureAwait(false);

            var channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(80)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

            var microphone = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 80,
                NumberOfBuffers = 4
            };
            microphone.DataAvailable += Microphone_DataAvailable;
            microphone.RecordingStopped += Microphone_RecordingStopped;

            lock (_lifecycleLock)
            {
                _audioChannel = channel;
                _microphone = microphone;
                _microphoneSignalReported = 0;
                _state = VoiceListeningState.WaitingForWakeWord;
                _workerTask = ProcessAudioAsync(channel.Reader, _cancellation!.Token);
            }

            microphone.StartRecording();
            RaiseStatus("正在本机等待“你好贾维斯”");
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void ResumeWakeWordListening()
    {
        if (_state is VoiceListeningState.Stopped or VoiceListeningState.Starting)
        {
            return;
        }

        lock (_modelLock)
        {
            _recognizer?.Reset(_recognitionStream!);
            _keywordSpotter?.Reset(_keywordStream!);
            _questionTimer.Reset();
            Interlocked.Exchange(ref _microphoneSignalReported, 0);
            _state = VoiceListeningState.WaitingForWakeWord;
        }
        RaiseStatus("正在本机等待“你好贾维斯”");
    }

    public void BeginQuestionListening()
    {
        if (_state is VoiceListeningState.Stopped or VoiceListeningState.Starting)
        {
            return;
        }

        lock (_modelLock)
        {
            _state = VoiceListeningState.Paused;
            _recognizer?.Reset(_recognitionStream!);
            _questionTimer.Restart();
            _state = VoiceListeningState.ListeningForQuestion;
        }

        WakeWordDetected?.Invoke(this, EventArgs.Empty);
        RaiseStatus("已开始听，请直接说问题");
    }

    public async Task StopAsync()
    {
        WaveInEvent? microphone;
        CancellationTokenSource? cancellation;
        Task? worker;

        lock (_lifecycleLock)
        {
            microphone = _microphone;
            cancellation = _cancellation;
            worker = _workerTask;
            _microphone = null;
            _cancellation = null;
            _workerTask = null;
            _state = VoiceListeningState.Stopped;
        }

        if (microphone is not null)
        {
            microphone.DataAvailable -= Microphone_DataAvailable;
            microphone.RecordingStopped -= Microphone_RecordingStopped;
            try
            {
                microphone.StopRecording();
            }
            catch (MmException)
            {
                // It may already have stopped after a device change.
            }
            microphone.Dispose();
        }

        cancellation?.Cancel();
        _audioChannel?.Writer.TryComplete();

        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        _audioChannel = null;
        DisposeModels();
        cancellation?.Dispose();
        RaiseStatus("麦克风已停止");
    }

    private void InitializeModels()
    {
        var keywordDirectory = Path.GetDirectoryName(LocalVoiceModelPaths.WakeKeywordFile)!;
        Directory.CreateDirectory(keywordDirectory);
        File.WriteAllText(
            LocalVoiceModelPaths.WakeKeywordFile,
            WakeWordTokens + Environment.NewLine,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var keywordConfig = new KeywordSpotterConfig();
        keywordConfig.FeatConfig.SampleRate = SampleRate;
        keywordConfig.FeatConfig.FeatureDim = 80;
        keywordConfig.ModelConfig.Transducer.Encoder = _paths.KeywordEncoder;
        keywordConfig.ModelConfig.Transducer.Decoder = _paths.KeywordDecoder;
        keywordConfig.ModelConfig.Transducer.Joiner = _paths.KeywordJoiner;
        keywordConfig.ModelConfig.Tokens = _paths.KeywordTokens;
        keywordConfig.ModelConfig.Provider = "cpu";
        keywordConfig.ModelConfig.NumThreads = 2;
        keywordConfig.ModelConfig.Debug = 0;
        keywordConfig.KeywordsThreshold = 0.18F;
        keywordConfig.KeywordsScore = 2.0F;
        keywordConfig.KeywordsFile = LocalVoiceModelPaths.WakeKeywordFile;

        var recognizerConfig = new OnlineRecognizerConfig();
        recognizerConfig.FeatConfig.SampleRate = SampleRate;
        recognizerConfig.FeatConfig.FeatureDim = 80;
        recognizerConfig.ModelConfig.Transducer.Encoder = _paths.RecognitionEncoder;
        recognizerConfig.ModelConfig.Transducer.Decoder = _paths.RecognitionDecoder;
        recognizerConfig.ModelConfig.Transducer.Joiner = _paths.RecognitionJoiner;
        recognizerConfig.ModelConfig.Tokens = _paths.RecognitionTokens;
        recognizerConfig.ModelConfig.Provider = "cpu";
        recognizerConfig.ModelConfig.NumThreads = 2;
        recognizerConfig.ModelConfig.Debug = 0;
        recognizerConfig.DecodingMethod = "greedy_search";
        recognizerConfig.EnableEndpoint = 1;
        recognizerConfig.Rule1MinTrailingSilence = 4.0F;
        recognizerConfig.Rule2MinTrailingSilence = 1.0F;
        recognizerConfig.Rule3MinUtteranceLength = 15.0F;

        _keywordSpotter = new KeywordSpotter(keywordConfig);
        _recognizer = new OnlineRecognizer(recognizerConfig);
        _keywordStream = _keywordSpotter.CreateStream();
        _recognitionStream = _recognizer.CreateStream();
    }

    private void Microphone_DataAvailable(object? sender, WaveInEventArgs e)
    {
        var samples = new float[e.BytesRecorded / 2];
        var peak = 0F;
        for (var index = 0; index < samples.Length; index++)
        {
            var offset = index * 2;
            var value = (short)(e.Buffer[offset] | (e.Buffer[offset + 1] << 8));
            samples[index] = value / 32768F;
            peak = Math.Max(peak, Math.Abs(samples[index]));
        }

        if (_state == VoiceListeningState.WaitingForWakeWord
            && peak >= 0.006F
            && Interlocked.CompareExchange(ref _microphoneSignalReported, 1, 0) == 0)
        {
            RaiseStatus("已检测到麦克风声音，正在识别唤醒词");
        }

        _audioChannel?.Writer.TryWrite(samples);
    }

    private void Microphone_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null && IsRunning)
        {
            RaiseFailure($"麦克风意外停止：{e.Exception.Message}");
        }
    }

    private async Task ProcessAudioAsync(ChannelReader<float[]> reader, CancellationToken cancellationToken)
    {
        await foreach (var samples in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (_state)
            {
                case VoiceListeningState.WaitingForWakeWord:
                    lock (_modelLock)
                    {
                        ProcessWakeWordSamples(samples);
                    }
                    break;
                case VoiceListeningState.ListeningForQuestion:
                    lock (_modelLock)
                    {
                        ProcessQuestionSamples(samples);
                    }
                    break;
                case VoiceListeningState.Paused:
                case VoiceListeningState.Starting:
                case VoiceListeningState.Stopped:
                    break;
            }
        }
    }

    private void ProcessWakeWordSamples(float[] samples)
    {
        _keywordStream!.AcceptWaveform(SampleRate, samples);
        while (_keywordSpotter!.IsReady(_keywordStream))
        {
            _keywordSpotter.Decode(_keywordStream);
            var keyword = _keywordSpotter.GetResult(_keywordStream).Keyword;
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            ActivateQuestionListening();
            return;
        }

        // The dedicated wake-word model can be sensitive to pronunciation and microphone tone.
        // Run the full local Chinese recognizer in parallel as a second, more tolerant path.
        _recognitionStream!.AcceptWaveform(SampleRate, samples);
        while (_recognizer!.IsReady(_recognitionStream))
        {
            _recognizer.Decode(_recognitionStream);
        }

        var recognizedText = _recognizer.GetResult(_recognitionStream).Text;
        if (WakePhraseMatcher.IsMatch(recognizedText))
        {
            ActivateQuestionListening();
            return;
        }

        if (_recognizer.IsEndpoint(_recognitionStream))
        {
            _recognizer.Reset(_recognitionStream);
        }
    }

    private void ActivateQuestionListening()
    {
        _keywordSpotter!.Reset(_keywordStream!);
        _recognizer!.Reset(_recognitionStream!);
        _questionTimer.Restart();
        _state = VoiceListeningState.ListeningForQuestion;
        WakeWordDetected?.Invoke(this, EventArgs.Empty);
        RaiseStatus("已唤醒，请直接说问题");
    }

    private void ProcessQuestionSamples(float[] samples)
    {
        _recognitionStream!.AcceptWaveform(SampleRate, samples);
        while (_recognizer!.IsReady(_recognitionStream))
        {
            _recognizer.Decode(_recognitionStream);
        }

        var result = _recognizer.GetResult(_recognitionStream).Text.Trim();
        var endpoint = _recognizer.IsEndpoint(_recognitionStream);
        var timedOut = _questionTimer.Elapsed >= TimeSpan.FromSeconds(QuestionTimeoutSeconds);

        if (!endpoint && !timedOut)
        {
            return;
        }

        _questionTimer.Reset();
        _recognizer.Reset(_recognitionStream);

        if (string.IsNullOrWhiteSpace(result))
        {
            _keywordSpotter!.Reset(_keywordStream!);
            _state = VoiceListeningState.WaitingForWakeWord;
            RaiseStatus("没有听清，已继续等待“你好贾维斯”");
            return;
        }

        _state = VoiceListeningState.Paused;
        QuestionRecognized?.Invoke(this, new VoiceQuestionEventArgs(result));
    }

    private void DisposeModels()
    {
        _keywordStream?.Dispose();
        _recognitionStream?.Dispose();
        _keywordSpotter?.Dispose();
        _recognizer?.Dispose();
        _keywordStream = null;
        _recognitionStream = null;
        _keywordSpotter = null;
        _recognizer = null;
    }

    private void RaiseStatus(string message) =>
        StatusChanged?.Invoke(this, new VoiceStatusEventArgs(message));

    private void RaiseFailure(string message) =>
        Failed?.Invoke(this, new VoiceStatusEventArgs(message));

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private enum VoiceListeningState
    {
        Stopped,
        Starting,
        WaitingForWakeWord,
        ListeningForQuestion,
        Paused
    }
}
