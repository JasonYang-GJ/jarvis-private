using System.Text.Json;

namespace ScreenGuide.AI.Core;

/// <summary>
/// A model-produced hint only. It never carries authorization and must be fed
/// back through deterministic planning before it can affect a workflow.
/// </summary>
public enum SemanticIntentKind
{
    Conversation,
    CodingTask,
    OpenFile,
    DescribeForeground
}

public enum SemanticMissingContext
{
    None,
    Project,
    File,
    Window,
    WindowConsent
}

public sealed record SemanticIntentSuggestion(
    SemanticIntentKind Kind,
    string? TargetHint,
    double Confidence,
    bool IsAmbiguous,
    SemanticMissingContext MissingContext);

public interface ISemanticIntentSuggester
{
    Task<SemanticIntentSuggestion?> SuggestAsync(
        Guid sessionTurnId,
        FrozenChatModelRoute frozenRoute,
        string text,
        CancellationToken cancellationToken = default);
}

public sealed class SemanticIntentFormatException : Exception
{
    public SemanticIntentFormatException()
        : base("语义意图模型返回了无效的结构化结果。")
    {
    }
}

public static class StrictSemanticIntentSuggestionParser
{
    private const int MaximumJsonCharacters = 4_096;
    private const int MaximumTargetCharacters = 200;
    private static readonly HashSet<string> ExpectedProperties =
    [
        "kind",
        "target",
        "confidence",
        "isAmbiguous",
        "missingContext"
    ];

    public static SemanticIntentSuggestion Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonCharacters)
        {
            throw new SemanticIntentFormatException();
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new SemanticIntentFormatException();
            }

            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!ExpectedProperties.Contains(property.Name)
                    || !observed.Add(property.Name))
                {
                    throw new SemanticIntentFormatException();
                }
            }

            if (!observed.SetEquals(ExpectedProperties))
            {
                throw new SemanticIntentFormatException();
            }

            var kind = ParseEnum<SemanticIntentKind>(root.GetProperty("kind"));
            var missingContext = ParseEnum<SemanticMissingContext>(
                root.GetProperty("missingContext"));
            var confidenceElement = root.GetProperty("confidence");
            if (confidenceElement.ValueKind != JsonValueKind.Number
                || !confidenceElement.TryGetDouble(out var confidence)
                || !double.IsFinite(confidence)
                || confidence is < 0 or > 1)
            {
                throw new SemanticIntentFormatException();
            }

            var ambiguityElement = root.GetProperty("isAmbiguous");
            if (ambiguityElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new SemanticIntentFormatException();
            }

            var target = ParseTarget(root.GetProperty("target"));
            ValidateKindAndContext(kind, missingContext);
            return new SemanticIntentSuggestion(
                kind,
                target,
                confidence,
                ambiguityElement.GetBoolean(),
                missingContext);
        }
        catch (SemanticIntentFormatException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidOperationException
                or KeyNotFoundException
                or FormatException)
        {
            throw new SemanticIntentFormatException();
        }
    }

    private static T ParseEnum<T>(JsonElement element)
        where T : struct, Enum
    {
        if (element.ValueKind != JsonValueKind.String
            || !Enum.TryParse<T>(element.GetString(), ignoreCase: false, out var value)
            || !Enum.IsDefined(value))
        {
            throw new SemanticIntentFormatException();
        }

        return value;
    }

    private static string? ParseTarget(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new SemanticIntentFormatException();
        }

        var target = element.GetString();
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        target = target.Trim();
        if (target.Length > MaximumTargetCharacters
            || target.Any(char.IsControl))
        {
            throw new SemanticIntentFormatException();
        }

        return target;
    }

    private static void ValidateKindAndContext(
        SemanticIntentKind kind,
        SemanticMissingContext missingContext)
    {
        var valid = kind switch
        {
            SemanticIntentKind.Conversation => missingContext == SemanticMissingContext.None,
            SemanticIntentKind.CodingTask =>
                missingContext is SemanticMissingContext.None or SemanticMissingContext.Project,
            SemanticIntentKind.OpenFile =>
                missingContext is SemanticMissingContext.None or SemanticMissingContext.File,
            SemanticIntentKind.DescribeForeground =>
                missingContext is SemanticMissingContext.None
                    or SemanticMissingContext.Window
                    or SemanticMissingContext.WindowConsent,
            _ => false
        };
        if (!valid)
        {
            throw new SemanticIntentFormatException();
        }
    }
}

public static class SemanticIntentCandidateDetector
{
    private static readonly string[] References = ["这个", "那个", "刚才", "当前"];
    private static readonly string[] ActionWords =
        ["修改", "处理", "打开", "看看", "查看", "修复", "运行", "弄", "操作"];

    public static bool ShouldEvaluate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        return normalized.Length <= 2_000
               && References.Any(reference =>
                   normalized.Contains(reference, StringComparison.Ordinal))
               && ActionWords.Any(action =>
                   normalized.Contains(action, StringComparison.Ordinal));
    }
}

public static class SemanticIntentSuggestionPolicy
{
    private const double MinimumConfidence = 0.80;

    public static UniversalIntentKind? SelectExplicitIntent(
        SemanticIntentSuggestion? suggestion)
    {
        if (suggestion is null
            || suggestion.IsAmbiguous
            || suggestion.Confidence < MinimumConfidence)
        {
            return null;
        }

        return suggestion.Kind switch
        {
            SemanticIntentKind.CodingTask => UniversalIntentKind.CodingTask,
            SemanticIntentKind.OpenFile => UniversalIntentKind.OpenFile,
            SemanticIntentKind.DescribeForeground => UniversalIntentKind.DescribeForeground,
            _ => null
        };
    }
}
