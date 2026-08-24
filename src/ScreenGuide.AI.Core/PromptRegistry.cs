using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenGuide.AI.Core;

public sealed record PromptDefinition(
    string PromptId,
    string Version,
    string Purpose,
    string Content,
    string ContentSha256,
    IReadOnlyList<string> ProviderIds,
    DateTimeOffset CreatedAtUtc,
    string ChangeReason);

public sealed class PromptRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, PromptDefinition> _prompts;

    private PromptRegistry(IReadOnlyDictionary<string, PromptDefinition> prompts)
    {
        _prompts = prompts;
        Prompts = prompts.Values
            .OrderBy(item => item.PromptId, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<PromptDefinition> Prompts { get; }

    public static async Task<PromptRegistry> LoadAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Prompt 根目录不能为空。", nameof(rootDirectory));
        }

        var root = Path.GetFullPath(rootDirectory.Trim());
        var registryPath = Path.Combine(root, "registry.json");
        var manifest = JsonSerializer.Deserialize<PromptRegistryManifest>(
            await File.ReadAllTextAsync(registryPath, cancellationToken).ConfigureAwait(false),
            JsonOptions) ?? throw new InvalidDataException("Prompt Registry 内容为空。");
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException($"不支持 Prompt Registry schema {manifest.SchemaVersion}。");
        }

        var prompts = new Dictionary<string, PromptDefinition>(StringComparer.Ordinal);
        foreach (var item in manifest.Prompts)
        {
            var promptId = RequireText(item.Id, "Prompt ID");
            var version = RequireText(item.Version, "Prompt 版本");
            var contentPath = ResolveContentPath(root, RequireText(item.ContentFile, "Prompt 内容文件"));
            var expectedSha256 = RequireText(item.Sha256, "Prompt SHA-256");
            var contentBytes = await File.ReadAllBytesAsync(contentPath, cancellationToken)
                .ConfigureAwait(false);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(contentBytes));
            if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Prompt“{promptId}@{version}”的 SHA-256 与 Registry 不一致。");
            }

            var definition = new PromptDefinition(
                promptId,
                version,
                RequireText(item.Purpose, "Prompt 用途"),
                Encoding.UTF8.GetString(contentBytes),
                actualSha256,
                item.ProviderIds?.Select(value => RequireText(value, "Provider ID")).ToArray() ?? [],
                item.CreatedAtUtc,
                RequireText(item.ChangeReason, "Prompt 修改原因"));
            if (!prompts.TryAdd(Key(promptId, version), definition))
            {
                throw new InvalidDataException(
                    $"Prompt ID 和版本重复：{promptId}@{version}");
            }
        }

        return new PromptRegistry(prompts);
    }

    public PromptDefinition GetRequired(string promptId, string version, string providerId)
    {
        var normalizedPromptId = RequireText(promptId, "Prompt ID");
        var normalizedVersion = RequireText(version, "Prompt 版本");
        var normalizedProviderId = RequireText(providerId, "Provider ID");
        if (!_prompts.TryGetValue(Key(normalizedPromptId, normalizedVersion), out var prompt))
        {
            throw new KeyNotFoundException(
                $"未注册 Prompt：{normalizedPromptId}@{normalizedVersion}");
        }

        if (prompt.ProviderIds.Count > 0
            && !prompt.ProviderIds.Contains("*", StringComparer.Ordinal)
            && !prompt.ProviderIds.Contains(normalizedProviderId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Prompt“{normalizedPromptId}@{normalizedVersion}”不适用于 Provider“{normalizedProviderId}”。");
        }

        return prompt;
    }

    private static string ResolveContentPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Prompt 内容文件必须使用相对路径。");
        }

        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Prompt 内容文件不能离开 Registry 根目录。");
        }

        return path;
    }

    private static string RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{name} 不能为空。");
        }

        return value.Trim();
    }

    private static string Key(string promptId, string version) => $"{promptId}\n{version}";

    private sealed record PromptRegistryManifest
    {
        public int SchemaVersion { get; init; }

        public IReadOnlyList<PromptManifestItem> Prompts { get; init; } = [];
    }

    private sealed record PromptManifestItem
    {
        public string? Id { get; init; }

        public string? Version { get; init; }

        public string? Purpose { get; init; }

        public string? ContentFile { get; init; }

        public string? Sha256 { get; init; }

        public IReadOnlyList<string>? ProviderIds { get; init; }

        public DateTimeOffset CreatedAtUtc { get; init; }

        public string? ChangeReason { get; init; }
    }
}
