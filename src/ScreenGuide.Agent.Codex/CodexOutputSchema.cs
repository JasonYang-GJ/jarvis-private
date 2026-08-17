using System.Text.Json;

namespace ScreenGuide.Agent.Codex;

internal static class CodexOutputSchema
{
    private static readonly string SchemaJson = JsonSerializer.Serialize(
        new
        {
            type = "object",
            properties = new
            {
                outcome = new { type = "string", @enum = new[] { "completed", "action_required" } },
                summary = new { type = "string" },
                changedFiles = new
                {
                    type = "array",
                    items = new { type = "string" }
                },
                tests = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string" },
                            status = new
                            {
                                type = "string",
                                @enum = new[] { "passed", "failed", "not_run" }
                            }
                        },
                        required = new[] { "name", "status" },
                        additionalProperties = false
                    }
                },
                question = new { type = new[] { "string", "null" } },
                decisionOptions = new
                {
                    type = "array",
                    items = new { type = "string" }
                }
            },
            required = new[]
            {
                "outcome",
                "summary",
                "changedFiles",
                "tests",
                "question",
                "decisionOptions"
            },
            additionalProperties = false
        });

    public static async Task<string> EnsureFileAsync(
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        var schemaDirectory = Path.Combine(dataDirectory, "protocol");
        Directory.CreateDirectory(schemaDirectory);
        var schemaPath = Path.Combine(schemaDirectory, "codex-turn-result.schema.json");
        if (!File.Exists(schemaPath)
            || !string.Equals(
                await File.ReadAllTextAsync(schemaPath, cancellationToken).ConfigureAwait(false),
                SchemaJson,
                StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(schemaPath, SchemaJson, cancellationToken)
                .ConfigureAwait(false);
        }

        return schemaPath;
    }
}
