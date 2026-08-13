using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Synthesis;

namespace ScreenGuide.App.Services;

internal sealed class WindowsSpeechService
{
    private static readonly CultureInfo ChineseCulture = CultureInfo.GetCultureInfo("zh-CN");

    public SpeechCapabilityReport GetCapabilityReport()
    {
        RecognizerInfo? recognizer = null;
        InstalledVoice? voice = null;

        try
        {
            recognizer = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(candidate => candidate.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // The report below explains that recognition is unavailable.
        }

        try
        {
            using var synthesizer = new SpeechSynthesizer();
            voice = synthesizer.GetInstalledVoices()
                .FirstOrDefault(candidate => candidate.Enabled
                                             && candidate.VoiceInfo.Culture.Name.StartsWith(
                                                 "zh",
                                                 StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // The report below explains that speech output is unavailable.
        }

        return new SpeechCapabilityReport(
            recognizer is not null,
            recognizer?.Name ?? "未检测到中文语音识别",
            voice is not null,
            voice?.VoiceInfo.Name ?? "未检测到中文语音"
            );
    }

    public async Task<string> RecognizeChineseOnceAsync(CancellationToken cancellationToken)
    {
        var recognizerInfo = SpeechRecognitionEngine.InstalledRecognizers()
            .FirstOrDefault(candidate => candidate.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Windows 没有安装中文语音识别组件。");

        using var recognizer = new SpeechRecognitionEngine(recognizerInfo.Id)
        {
            InitialSilenceTimeout = TimeSpan.FromSeconds(6),
            BabbleTimeout = TimeSpan.FromSeconds(4),
            EndSilenceTimeout = TimeSpan.FromSeconds(1.1),
            EndSilenceTimeoutAmbiguous = TimeSpan.FromSeconds(1.5)
        };

        recognizer.LoadGrammar(new DictationGrammar());
        recognizer.SetInputToDefaultAudioDevice();

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void RecognitionCompleted(object? sender, RecognizeCompletedEventArgs args)
        {
            if (args.Error is not null)
            {
                completion.TrySetException(args.Error);
            }
            else if (args.Cancelled)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                completion.TrySetResult(args.Result?.Text?.Trim() ?? string.Empty);
            }
        }

        recognizer.RecognizeCompleted += RecognitionCompleted;

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                recognizer.RecognizeAsyncCancel();
            }
            catch (InvalidOperationException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
        });

        try
        {
            recognizer.RecognizeAsync(RecognizeMode.Single);
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            recognizer.RecognizeCompleted -= RecognitionCompleted;
        }
    }

    public async Task SpeakChineseAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        using var synthesizer = new SpeechSynthesizer();
        var chineseVoice = synthesizer.GetInstalledVoices()
            .FirstOrDefault(candidate => candidate.Enabled
                                         && candidate.VoiceInfo.Culture.Name.StartsWith(
                                             "zh",
                                             StringComparison.OrdinalIgnoreCase));

        if (chineseVoice is not null)
        {
            synthesizer.SelectVoice(chineseVoice.VoiceInfo.Name);
        }

        synthesizer.SetOutputToDefaultAudioDevice();
        synthesizer.Rate = 0;
        synthesizer.Volume = 100;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void SpeakCompleted(object? sender, SpeakCompletedEventArgs args)
        {
            if (args.Error is not null)
            {
                completion.TrySetException(args.Error);
            }
            else if (args.Cancelled)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                completion.TrySetResult();
            }
        }

        synthesizer.SpeakCompleted += SpeakCompleted;
        using var cancellationRegistration = cancellationToken.Register(synthesizer.SpeakAsyncCancelAll);

        try
        {
            synthesizer.SpeakAsync(text);
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            synthesizer.SpeakCompleted -= SpeakCompleted;
        }
    }
}
