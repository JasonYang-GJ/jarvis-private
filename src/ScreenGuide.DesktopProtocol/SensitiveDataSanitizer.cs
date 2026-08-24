using System.Text.RegularExpressions;

namespace ScreenGuide.DesktopProtocol;

/// <summary>
/// Shared last-line defense for text that may cross a log, crash-report, or IPC boundary.
/// Provider implementations must still avoid putting credentials or raw response bodies in exceptions.
/// </summary>
public static partial class SensitiveDataSanitizer
{
    public static string ExceptionType(Exception? exception) =>
        exception?.GetType().Name ?? "Exception";

    public static string DiagnosticCode(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80)
        {
            return fallback;
        }

        return value.All(character =>
                character is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or '_' or '-' or '.')
            ? value
            : fallback;
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = BearerToken().Replace(value, "Bearer [REDACTED]");
        redacted = SecretAssignment().Replace(redacted, "${prefix}[REDACTED]");
        redacted = ProviderKey().Replace(redacted, "[REDACTED]");
        return WindowsUserPath().Replace(redacted, "$1\\[USER]\\");
    }

    [GeneratedRegex(
        """(?i)\bBearer\s+['"]?[A-Za-z0-9._~+/=-]+['"]?""",
        RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(
        """(?ix)(?<prefix>['"]?(?:api[-_]?key|access[-_]?token|token|secret|password|authorization)['"]?\s*(?::|=)\s*)['"]?[^\s,;}&'"]+['"]?""",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(
        @"(?i)(?<![A-Za-z0-9_-])sk-[A-Za-z0-9_-]{12,}(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProviderKey();

    [GeneratedRegex(
        @"(?i)([A-Z]:\\Users)\\[^\\]+\\",
        RegexOptions.CultureInvariant)]
    private static partial Regex WindowsUserPath();
}
