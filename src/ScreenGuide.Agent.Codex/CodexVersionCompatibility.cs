namespace ScreenGuide.Agent.Codex;

public sealed record CodexVersionCompatibilityResult(
    string DetectedVersion,
    bool IsVerified,
    IReadOnlyList<string> VerifiedVersions,
    string Decision);

public static class CodexVersionCompatibility
{
    public static IReadOnlyList<string> VerifiedVersions { get; } = ["0.147.0"];

    public static CodexVersionCompatibilityResult Evaluate(string version)
    {
        if (!Version.TryParse(version, out var parsed))
        {
            throw new FormatException($"Codex CLI 版本格式无效：{version}");
        }

        var normalized = parsed.ToString();
        var verified = VerifiedVersions.Contains(normalized, StringComparer.OrdinalIgnoreCase);
        return new CodexVersionCompatibilityResult(
            normalized,
            verified,
            VerifiedVersions,
            verified
                ? "允许：版本已通过 V0.1 兼容性验证。"
                : $"拒绝：Codex CLI {normalized} 尚未通过 V0.1 兼容性验证。当前仅验证 0.147.0。");
    }
}

internal sealed class CodexVersionCompatibilityException(
    string detectedVersion,
    string message) : NotSupportedException(message)
{
    public string DetectedVersion { get; } = detectedVersion;
}
