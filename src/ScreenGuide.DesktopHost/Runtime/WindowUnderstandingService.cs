using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed record WindowUnderstandingResult(
    string UserSummary,
    string Confidence,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Limitations,
    string CaptureTechnology);

/// <summary>
/// Privacy boundary for screen understanding. Consent is checked before capture, one exact
/// previously observed HWND is used, and the frame is cleared immediately after analysis.
/// Only metadata is logged; titles, OCR text and image bytes are never written to logs here.
/// </summary>
public sealed class WindowUnderstandingService(
    IWindowCaptureService capture,
    IWindowVisionProvider vision,
    IReliableDesktopAutomation structuredInterface,
    ILogger<WindowUnderstandingService> logger)
{
    public async Task<WindowUnderstandingResult> DescribeAsync(
        ForegroundWindowSnapshot foreground,
        bool explicitConsent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(foreground);
        if (!explicitConsent)
        {
            throw new UnauthorizedAccessException("需要你明确同意本次查看后，元枢才会读取这个窗口。 ");
        }

        var target = new WindowCaptureTarget(
            foreground.WindowHandle,
            foreground.WindowTitle,
            foreground.ProcessName,
            foreground.ObservedAtUtc);
        var targetHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{target.ProcessName}|{target.WindowHandle}")))[..12];
        logger.LogInformation(
            "Single-window observation started. TargetHash={TargetHash}; Provider={Provider}; OffDevice={OffDevice}",
            targetHash,
            vision.ProviderId,
            vision.SendsImageOffDevice);

        string? structure = null;
        try
        {
            structure = structuredInterface.Describe(foreground.WindowHandle).Summary;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogInformation(
                "Structured window metadata unavailable. TargetHash={TargetHash}; Error={Error}",
                targetHash,
                exception.GetType().Name);
        }

        await using var frame = await capture.CaptureAsync(target, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var result = await vision.AnalyzeAsync(
                    new WindowVisionRequest(target, frame, structure),
                    cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation(
                "Single-window observation completed. TargetHash={TargetHash}; Confidence={Confidence}; Capture={Capture}",
                targetHash,
                result.Confidence,
                frame.CaptureTechnology);
            return new WindowUnderstandingResult(
                result.UserSummary,
                result.Confidence,
                result.Sources,
                result.Limitations,
                frame.CaptureTechnology);
        }
        catch
        {
            logger.LogWarning(
                "Single-window observation failed. TargetHash={TargetHash}; Provider={Provider}",
                targetHash,
                vision.ProviderId);
            throw;
        }
    }
}
