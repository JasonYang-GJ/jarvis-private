using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Runtime;

public static class SensitiveDataRedactor
{
    public static string Redact(string? value) => SensitiveDataSanitizer.Redact(value);
}
