namespace ScreenGuide.Voice.Windows;

public sealed record VoiceCapabilityReport(
    bool RecognitionModelAvailable,
    string RecognitionProvider,
    int MicrophoneCount,
    bool ChineseSpeechOutputAvailable,
    string Message);
