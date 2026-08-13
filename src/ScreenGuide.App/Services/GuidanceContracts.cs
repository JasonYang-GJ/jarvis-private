namespace ScreenGuide.App.Services;

internal sealed record GuidanceRequest(
    string Question,
    string WindowTitle,
    byte[]? JpegImage);

internal interface IGuidanceProvider
{
    IAsyncEnumerable<string> StreamAnswerAsync(GuidanceRequest request, CancellationToken cancellationToken);
}

internal interface ICloudSpeechService
{
    Task SpeakStreamAsync(
        IAsyncEnumerable<string> textChunks,
        CancellationToken cancellationToken,
        Action<string>? progress = null);
}
