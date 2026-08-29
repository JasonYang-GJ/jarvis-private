using System.Text;

namespace ScreenGuide.Core.Memories;

public sealed record MemoryPreviewMatch(
    MemoryItem Item,
    int Score,
    IReadOnlyList<string> Explanations);

public sealed record MemoryPreviewResult(
    IReadOnlyList<MemoryPreviewMatch> Matches,
    int CandidateCount,
    int SelectedCount,
    int TotalCharacters);

public static class MemoryPreviewRanker
{
    public const int MaximumQueryCharacters = 500;
    public const int MaximumSelectedItems = 8;
    public const int MaximumCombinedCharacters = 4_000;

    public static MemoryPreviewResult Preview(
        string query,
        IReadOnlyList<MemoryItem> candidates,
        Guid? selectedProjectId)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (selectedProjectId == Guid.Empty)
        {
            throw new MemoryValidationException("项目 ID 无效。");
        }

        var queryText = CanonicalizeQuery(query);
        var queryTokens = Tokens(queryText)
            .Where(token => token.EnumerateRunes().Count() >= 2)
            .ToHashSet(StringComparer.Ordinal);
        var queryBigrams = Bigrams(queryText);
        var ranked = candidates
            .Select(candidate => Rank(candidate, queryText, queryTokens, queryBigrams, selectedProjectId))
            .Where(match => match is not null)
            .Select(match => match!)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Item.Metadata.UpdatedAtUtc)
            .ThenBy(
                match => match.Item.Metadata.Id.ToString("D"),
                StringComparer.Ordinal)
            .ToArray();

        var selected = new List<MemoryPreviewMatch>(MaximumSelectedItems);
        var totalCharacters = 0;
        foreach (var match in ranked)
        {
            if (selected.Count == MaximumSelectedItems)
            {
                break;
            }

            var characterCount = (match.Item.Title?.Length ?? 0) + (match.Item.Body?.Length ?? 0);
            if (characterCount > MaximumCombinedCharacters - totalCharacters)
            {
                continue;
            }

            selected.Add(match);
            totalCharacters += characterCount;
        }

        return new MemoryPreviewResult(
            selected,
            candidates.Count,
            selected.Count,
            totalCharacters);
    }

    public static string CanonicalizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new MemoryValidationException("本地记忆预览查询不能为空。");
        }

        if (query.Any(char.IsControl))
        {
            throw new MemoryValidationException("本地记忆预览查询不符合要求。");
        }

        var trimmed = query.Trim();
        if (trimmed.Length > MaximumQueryCharacters)
        {
            throw new MemoryValidationException("本地记忆预览查询不符合要求。");
        }

        var normalized = trimmed.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        if (normalized.Length > MaximumQueryCharacters)
        {
            throw new MemoryValidationException("本地记忆预览查询超过长度限制。");
        }

        var canonical = CanonicalizeText(normalized);
        return canonical.Length == 0
            ? throw new MemoryValidationException("本地记忆预览查询必须包含文字或数字。")
            : canonical;
    }

    private static MemoryPreviewMatch? Rank(
        MemoryItem candidate,
        string query,
        IReadOnlySet<string> queryTokens,
        IReadOnlySet<string> queryBigrams,
        Guid? selectedProjectId)
    {
        if (candidate.Title is null || candidate.Body is null)
        {
            return null;
        }

        var candidateText = CanonicalizeText(string.Concat(candidate.Title, " ", candidate.Body)
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant());
        var candidateTokens = Tokens(candidateText).ToHashSet(StringComparer.Ordinal);
        var candidateBigrams = Bigrams(candidateText);
        var exactPhrase = candidateText.Contains(query, StringComparison.Ordinal);
        var tokenOverlap = queryTokens.Count(candidateTokens.Contains);
        var bigramOverlap = queryBigrams.Count(candidateBigrams.Contains);
        var lexicalScore = (exactPhrase ? 1_000 : 0) + (tokenOverlap * 100) + (bigramOverlap * 10);
        if (lexicalScore == 0)
        {
            return null;
        }

        var explanations = new List<string>(4);
        if (exactPhrase)
        {
            explanations.Add("exact_phrase");
        }

        if (tokenOverlap > 0)
        {
            explanations.Add($"token_overlap:{tokenOverlap}");
        }

        if (bigramOverlap > 0)
        {
            explanations.Add($"bigram_overlap:{bigramOverlap}");
        }

        var exactProjectScope = selectedProjectId is { } projectId
            && candidate.Metadata.Scope.Kind == MemoryScopeKind.Project
            && candidate.Metadata.Scope.ProjectId == projectId;
        if (exactProjectScope)
        {
            explanations.Add("project_scope");
        }

        return new MemoryPreviewMatch(
            candidate,
            lexicalScore + (exactProjectScope ? 5 : 0),
            explanations);
    }

    private static IEnumerable<string> Tokens(string canonical) =>
        canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static HashSet<string> Bigrams(string canonical)
    {
        var bigrams = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Tokens(canonical))
        {
            var runes = token.EnumerateRunes().ToArray();
            for (var index = 0; index + 1 < runes.Length; index++)
            {
                bigrams.Add(string.Concat(runes[index].ToString(), runes[index + 1].ToString()));
            }
        }

        return bigrams;
    }

    private static string CanonicalizeText(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingBoundary = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (pendingBoundary && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(rune.ToString());
                pendingBoundary = false;
            }
            else
            {
                pendingBoundary = true;
            }
        }

        return builder.ToString();
    }
}
