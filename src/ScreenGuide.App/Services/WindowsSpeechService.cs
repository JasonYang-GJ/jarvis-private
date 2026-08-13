using System.Globalization;
using System.Speech.Synthesis;

namespace ScreenGuide.App.Services;

internal sealed class WindowsSpeechService
{
    private static readonly CultureInfo ChineseCulture = CultureInfo.GetCultureInfo("zh-CN");

    public SpeechCapabilityReport GetCapabilityReport()
    {
        InstalledVoice? voice = null;
        var modelPaths = LocalVoiceModelPaths.Create();

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
            modelPaths.IsComplete,
            modelPaths.IsComplete ? "本机 sherpa-onnx 中文模型" : "本地模型不完整",
            voice is not null,
            voice?.VoiceInfo.Name ?? "未检测到中文语音"
            );
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
        synthesizer.Rate = 3;
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
