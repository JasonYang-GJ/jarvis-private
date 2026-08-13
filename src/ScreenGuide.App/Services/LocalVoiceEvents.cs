namespace ScreenGuide.App.Services;

internal sealed class VoiceStatusEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

internal sealed class VoiceQuestionEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}
