using System.Text;

namespace ScreenGuide.Core;

public static class ApplicationNameMatcher
{
    private static readonly string[] UnsafeOrUtilityPrefixes =
    [
        "卸载",
        "uninstall",
        "remove"
    ];

    public static int Score(string? requestedName, string? candidateName)
    {
        var requested = Normalize(requestedName);
        var candidate = Normalize(candidateName);
        if (requested.Length == 0 || candidate.Length == 0)
        {
            return 0;
        }

        if (UnsafeOrUtilityPrefixes.Any(prefix => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        if (string.Equals(requested, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (candidate.StartsWith(requested, StringComparison.OrdinalIgnoreCase))
        {
            return 85;
        }

        if (candidate.Contains(requested, StringComparison.OrdinalIgnoreCase))
        {
            return 70;
        }

        return 0;
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
