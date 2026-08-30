using System.Drawing;
using System.Security.Cryptography;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.Stage4.RealUsageRunner;

public sealed record VisionFrameShapeSummary(
    string CaptureTechnology,
    int PixelWidth,
    int PixelHeight,
    int SampledPixelCount,
    int DarkPixelPermille,
    int BrightPixelPermille,
    int OpaquePixelPermille,
    int LuminanceRange,
    bool OcrTextDetected)
{
    public static VisionFrameShapeSummary Empty { get; } = new(
        "unknown",
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        false);
}

public static class VisionFrameShapeAnalyzer
{
    private const int SamplingGridSize = 64;

    public static VisionFrameShapeSummary Analyze(CapturedWindowFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var encodedCopy = frame.PngBytes.ToArray();
        try
        {
            using var stream = new MemoryStream(encodedCopy, writable: false);
            using var bitmap = new Bitmap(stream);
            var horizontalStep = Math.Max(1, bitmap.Width / SamplingGridSize);
            var verticalStep = Math.Max(1, bitmap.Height / SamplingGridSize);
            var sampled = 0;
            var dark = 0;
            var bright = 0;
            var opaque = 0;
            var minimumLuminance = 255;
            var maximumLuminance = 0;

            for (var y = verticalStep / 2; y < bitmap.Height; y += verticalStep)
            {
                for (var x = horizontalStep / 2; x < bitmap.Width; x += horizontalStep)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    var sourceLuminance = ((299 * pixel.R) + (587 * pixel.G) + (114 * pixel.B)) / 1000;
                    var compositedLuminance = ((sourceLuminance * pixel.A) + (255 * (255 - pixel.A))) / 255;
                    sampled++;
                    if (compositedLuminance <= 80)
                    {
                        dark++;
                    }

                    if (compositedLuminance >= 240)
                    {
                        bright++;
                    }

                    if (pixel.A >= 240)
                    {
                        opaque++;
                    }

                    minimumLuminance = Math.Min(minimumLuminance, compositedLuminance);
                    maximumLuminance = Math.Max(maximumLuminance, compositedLuminance);
                }
            }

            return new VisionFrameShapeSummary(
                frame.CaptureTechnology,
                frame.PixelWidth,
                frame.PixelHeight,
                sampled,
                Permille(dark, sampled),
                Permille(bright, sampled),
                Permille(opaque, sampled),
                sampled == 0 ? 0 : maximumLuminance - minimumLuminance,
                OcrTextDetected: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedCopy);
        }
    }

    private static int Permille(int value, int total) =>
        total <= 0 ? 0 : (int)Math.Round(value * 1000d / total, MidpointRounding.AwayFromZero);
}
