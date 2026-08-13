namespace ScreenGuide.App.Services;

internal sealed record WindowSelectionResult(
    string DisplayName,
    int PixelWidth,
    int PixelHeight,
    IntPtr WindowHandle)
{
    public bool CanCapture => WindowHandle != IntPtr.Zero;
}
