namespace ScreenGuide.DesktopClient.Services;

public sealed class DesktopHostStartupException(
    string code,
    string userMessage,
    string technicalDetail) : Exception(userMessage)
{
    public string Code { get; } = code;

    public string UserMessage { get; } = userMessage;

    public string TechnicalDetail { get; } = technicalDetail;
}
