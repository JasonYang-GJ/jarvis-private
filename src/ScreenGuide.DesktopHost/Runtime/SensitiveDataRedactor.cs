using System.Text.RegularExpressions;

namespace ScreenGuide.DesktopHost.Runtime;

public static partial class SensitiveDataRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = SecretAssignment().Replace(value, "$1=[REDACTED]");
        redacted = BearerToken().Replace(redacted, "Bearer [REDACTED]");
        return WindowsUserPath().Replace(redacted, "$1\\[USER]\\");
    }

    [GeneratedRegex("(?i)(api[_-]?key|token|secret|password)\\s*[:=]\\s*[^\\s,;]+")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+/-]+")]
    private static partial Regex BearerToken();

    [GeneratedRegex("(?i)([A-Z]:\\\\Users)\\\\[^\\\\]+\\\\")]
    private static partial Regex WindowsUserPath();
}
