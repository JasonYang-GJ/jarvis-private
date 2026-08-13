namespace ScreenGuide.App.Services;

internal sealed record SpeechCapabilityReport(
    bool HasChineseRecognizer,
    string RecognizerName,
    bool HasChineseVoice,
    string VoiceName)
{
    public bool IsReady => HasChineseRecognizer && HasChineseVoice;
}
