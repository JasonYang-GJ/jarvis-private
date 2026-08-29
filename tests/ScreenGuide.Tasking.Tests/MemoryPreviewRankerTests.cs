using ScreenGuide.Core.Memories;

namespace ScreenGuide.Tasking.Tests;

public sealed class MemoryPreviewRankerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PreviewNormalizesFormKcAndRanksExactPhraseTokensAndBigramsWithoutScopeOnlyMatches()
    {
        var exact = Item(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "Cafe 教程",
            "本地说明",
            Now);
        var reordered = Item(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "教程 cafe",
            "本地说明",
            Now.AddMinutes(1));
        var unrelated = Item(
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            "无关记录",
            "完全不匹配",
            Now.AddMinutes(2));

        var result = MemoryPreviewRanker.Preview(
            "  ＣＡＦＥ，教程  ",
            [unrelated, reordered, exact],
            selectedProjectId: null);

        Assert.Equal(3, result.CandidateCount);
        Assert.Equal(2, result.SelectedCount);
        Assert.Collection(
            result.Matches,
            match =>
            {
                Assert.Equal(exact.Metadata.Id, match.Item.Metadata.Id);
                Assert.Equal(1240, match.Score);
                Assert.Contains("exact_phrase", match.Explanations);
                Assert.Contains("token_overlap:2", match.Explanations);
                Assert.Contains("bigram_overlap:4", match.Explanations);
            },
            match =>
            {
                Assert.Equal(reordered.Metadata.Id, match.Item.Metadata.Id);
                Assert.Equal(240, match.Score);
                Assert.DoesNotContain("exact_phrase", match.Explanations);
            });
    }

    [Fact]
    public void PreviewRejectsEmptyOversizedPunctuationOnlyAndControlCharacterQueries()
    {
        var candidate = Item(Guid.NewGuid(), "合法标题", "合法正文", Now);

        Assert.Throws<MemoryValidationException>(() =>
            MemoryPreviewRanker.Preview("   ", [candidate], null));
        Assert.Throws<MemoryValidationException>(() =>
            MemoryPreviewRanker.Preview("!!!", [candidate], null));
        Assert.Throws<MemoryValidationException>(() =>
            MemoryPreviewRanker.Preview(new string('a', 501), [candidate], null));
        Assert.Throws<MemoryValidationException>(() =>
            MemoryPreviewRanker.Preview("合法\n查询", [candidate], null));
        Assert.Throws<MemoryValidationException>(() =>
            MemoryPreviewRanker.Preview("\n合法查询", [candidate], null));
    }

    [Fact]
    public void PreviewUsesProjectBoostOnlyAfterLexicalMatchAndBreaksTiesByTimeThenGuid()
    {
        var projectId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var laterGlobal = Item(
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            "计划",
            "内容",
            Now.AddMinutes(1));
        var lowerGuidGlobal = Item(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "计划",
            "内容",
            Now);
        var higherGuidGlobal = Item(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "计划",
            "内容",
            Now);
        var project = Item(
            Guid.Parse("00000000-0000-0000-0000-000000000004"),
            "计划",
            "内容",
            Now,
            MemoryScope.ForProject(projectId));
        var projectWithoutLexicalMatch = Item(
            Guid.Parse("00000000-0000-0000-0000-000000000005"),
            "无关",
            "记录",
            Now.AddMinutes(2),
            MemoryScope.ForProject(projectId));

        var result = MemoryPreviewRanker.Preview(
            "计划",
            [higherGuidGlobal, projectWithoutLexicalMatch, lowerGuidGlobal, project, laterGlobal],
            projectId);

        Assert.Equal(
            [project.Metadata.Id, laterGlobal.Metadata.Id, lowerGuidGlobal.Metadata.Id, higherGuidGlobal.Metadata.Id],
            result.Matches.Select(match => match.Item.Metadata.Id));
        Assert.Contains("project_scope", result.Matches[0].Explanations);
        Assert.DoesNotContain(
            result.Matches,
            match => match.Item.Metadata.Id == projectWithoutLexicalMatch.Metadata.Id);
    }

    [Fact]
    public void PreviewCapsEightItemsAndSkipsAnItemThatExceedsTheCharacterBudget()
    {
        var oversized = Item(
            Guid.Parse("00000000-0000-0000-0000-000000000099"),
            "match",
            new string('x', MemoryPreviewRanker.MaximumCombinedCharacters),
            Now.AddHours(1));
        var normal = Enumerable.Range(1, 10)
            .Select(index => Item(
                Guid.Parse($"00000000-0000-0000-0000-{index:D12}"),
                "match",
                "ok",
                Now.AddMinutes(index)))
            .ToArray();

        var result = MemoryPreviewRanker.Preview(
            "match",
            [oversized, .. normal],
            selectedProjectId: null);

        Assert.Equal(11, result.CandidateCount);
        Assert.Equal(8, result.SelectedCount);
        Assert.Equal(8 * 7, result.TotalCharacters);
        Assert.DoesNotContain(result.Matches, match => match.Item.Metadata.Id == oversized.Metadata.Id);
    }

    private static MemoryItem Item(
        Guid id,
        string title,
        string body,
        DateTimeOffset updatedAtUtc,
        MemoryScope? scope = null) => new(
        MemoryMetadata.Create(
            id,
            MemoryCategory.ProjectNote,
            scope ?? MemoryScope.Global,
            MemoryStatus.Active,
            MemorySourceKind.UserExplicit,
            Now,
            updatedAtUtc,
            null,
            1.0,
            1),
        title,
        body);
}
