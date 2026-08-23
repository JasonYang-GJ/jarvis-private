using System.Security.Cryptography;

namespace ScreenGuide.Vision.Abstractions;

public sealed record WindowCaptureTarget(
    long WindowHandle,
    string WindowTitle,
    string ProcessName,
    DateTimeOffset ObservedAtUtc);

public sealed record SensitiveWindowAssessment(bool IsSensitive, string? Reason = null);

public interface ISensitiveWindowPolicy
{
    SensitiveWindowAssessment Assess(WindowCaptureTarget target);
}

public interface IWindowCaptureService
{
    Task<CapturedWindowFrame> CaptureAsync(
        WindowCaptureTarget target,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the only in-memory copy of a captured PNG. Disposing clears the byte buffer.
/// Implementations and callers must never persist it.
/// </summary>
public sealed class CapturedWindowFrame : IAsyncDisposable, IDisposable
{
    private byte[]? _pngBytes;

    public CapturedWindowFrame(byte[] pngBytes, int pixelWidth, int pixelHeight, string captureTechnology)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
        {
            throw new ArgumentException("窗口图像不能为空。", nameof(pngBytes));
        }

        _pngBytes = pngBytes;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        CaptureTechnology = captureTechnology;
    }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public string CaptureTechnology { get; }

    public ReadOnlyMemory<byte> PngBytes => _pngBytes
        ?? throw new ObjectDisposedException(nameof(CapturedWindowFrame));

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _pngBytes, null);
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed record WindowVisionRequest(
    WindowCaptureTarget Target,
    CapturedWindowFrame Frame,
    string? StructuredInterfaceSummary = null);

public sealed record WindowVisionResult(
    string UserSummary,
    string Confidence,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Limitations);

public interface IWindowVisionProvider
{
    string ProviderId { get; }

    bool SendsImageOffDevice { get; }

    Task<WindowVisionResult> AnalyzeAsync(
        WindowVisionRequest request,
        CancellationToken cancellationToken = default);
}

public interface ILocalOcrTextExtractor
{
    Task<string> ExtractAsync(
        ReadOnlyMemory<byte> pngBytes,
        CancellationToken cancellationToken = default);
}
