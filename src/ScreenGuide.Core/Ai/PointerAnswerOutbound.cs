using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenGuide.Core.Ai;

public static class PointerAnswerErrorCodes
{
    public const string ConsentRequired = "pointer_answer_consent_required";
    public const string ConsentStale = "pointer_answer_consent_stale";
    public const string DestinationUnverifiable = "pointer_answer_destination_unverifiable";
    public const string ContentInvalid = "pointer_answer_content_invalid";
    public const string Cancelled = "pointer_answer_cancelled";
    public const string CommitFailed = "pointer_answer_commit_failed";
}

public sealed record PointerAnswerAuditMetadata(
    Guid ConsentId,
    Guid SessionTurnId,
    Guid AnchorId,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ConsumedAtUtc,
    string ProviderId,
    string ModelId,
    string DestinationOrigin,
    string PromptId,
    string PromptVersion,
    string PromptContentHash,
    string QuestionHash,
    string OcrHash,
    string ContextHash,
    string TargetBindingHash,
    string PreviewHash,
    int QuestionCharacterCount,
    int OcrCharacterCount,
    int OcrLineCount,
    int RegionWidth,
    int RegionHeight,
    string RegionSource)
{
    public override string ToString() =>
        $"PointerAnswerAuditMetadata {{ ConsentId = {ConsentId}, SessionTurnId = {SessionTurnId}, AnchorId = {AnchorId}, ProviderId = {ProviderId}, ModelId = {ModelId}, DestinationOrigin = {DestinationOrigin}, PromptId = {PromptId}, PromptVersion = {PromptVersion}, QuestionHash = {QuestionHash}, OcrHash = {OcrHash}, ContextHash = {ContextHash}, TargetBindingHash = {TargetBindingHash}, PreviewHash = {PreviewHash}, QuestionCharacterCount = {QuestionCharacterCount}, OcrCharacterCount = {OcrCharacterCount}, OcrLineCount = {OcrLineCount}, RegionWidth = {RegionWidth}, RegionHeight = {RegionHeight}, RegionSource = {RegionSource}, Content = [REDACTED] }}";
}

public sealed record PointerAnswerEnvelope(
    string SerializedContext,
    PointerAnswerAuditMetadata Audit)
{
    public override string ToString() =>
        $"PointerAnswerEnvelope {{ Audit = {Audit}, SerializedContext = [REDACTED] }}";
}

public static class PointerAnswerOutboundContract
{
    public const string ContextType = "POINTER_REGION_TEXT_CONTEXT_V1";
    public const int MaximumQuestionCharacters = 20_000;
    public const int MaximumOcrCharacters = 700;
    public const int MaximumSerializedContextCharacters = 2_000;

    public static string SerializeContext(
        string ocrText,
        int lineCount,
        int regionWidth,
        int regionHeight,
        string regionSource)
    {
        var normalized = RequireBoundedText(
            ocrText,
            MaximumOcrCharacters,
            nameof(ocrText),
            allowEmpty: true);
        if (lineCount < 0 || regionWidth <= 0 || regionHeight <= 0
            || (regionSource != "uia-element" && regionSource != "pointer-centered"))
        {
            throw new ArgumentException("指针区域结构信息无效。", nameof(regionSource));
        }

        var json = JsonSerializer.Serialize(new
        {
            type = ContextType,
            ocrText = normalized,
            structure = new
            {
                lineCount,
                regionWidth,
                regionHeight,
                regionSource
            }
        });
        return json.Length <= MaximumSerializedContextCharacters
            ? json
            : throw new ArgumentException("指针区域出站上下文超出安全上限。", nameof(ocrText));
    }

    public static string HashText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static string NormalizeHttpsOrigin(string destination)
    {
        if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("指针回答的数据目的地不是可验证的 HTTPS 地址。", nameof(destination));
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static string RequireQuestion(string question) =>
        RequireBoundedText(question, MaximumQuestionCharacters, nameof(question), allowEmpty: false);

    public static bool ContextMatchesAudit(
        string serializedContext,
        PointerAnswerAuditMetadata audit)
    {
        try
        {
            using var document = JsonDocument.Parse(serializedContext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 3
                || root.GetProperty("type").GetString() != ContextType
                || root.GetProperty("ocrText").ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var ocrText = root.GetProperty("ocrText").GetString() ?? string.Empty;
            var structure = root.GetProperty("structure");
            return structure.ValueKind == JsonValueKind.Object
                && structure.EnumerateObject().Count() == 4
                && ocrText.Length == audit.OcrCharacterCount
                && string.Equals(HashText(ocrText), audit.OcrHash, StringComparison.Ordinal)
                && structure.GetProperty("lineCount").GetInt32() == audit.OcrLineCount
                && structure.GetProperty("regionWidth").GetInt32() == audit.RegionWidth
                && structure.GetProperty("regionHeight").GetInt32() == audit.RegionHeight
                && string.Equals(
                    structure.GetProperty("regionSource").GetString(),
                    audit.RegionSource,
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidOperationException
                or KeyNotFoundException
                or FormatException
                or OverflowException)
        {
            return false;
        }
    }

    private static string RequireBoundedText(
        string value,
        int maximumLength,
        string parameterName,
        bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Trim();
        if ((!allowEmpty && normalized.Length == 0) || normalized.Length > maximumLength
            || normalized.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException("指针回答文本无效。", parameterName);
        }

        return normalized;
    }
}
