using ScreenGuide.Core.Memories;

namespace ScreenGuide.Tasking.Tests;

public sealed class MemoryDomainTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MemoryCategory.UserFact)]
    [InlineData(MemoryCategory.UserPreference)]
    [InlineData(MemoryCategory.ProjectNote)]
    [InlineData(MemoryCategory.Decision)]
    public void ExplicitDraftAcceptsOnlyTheFourApprovedCategories(MemoryCategory category)
    {
        var draft = MemoryDraft.Create(
            category,
            MemoryScope.Global,
            "  标题  ",
            "  正文  ",
            Now.AddDays(1),
            Now);

        Assert.Equal(category, draft.Category);
        Assert.Equal("标题", draft.Title);
        Assert.Equal("正文", draft.Body);
        Assert.Equal(MemorySourceKind.UserExplicit, draft.SourceKind);
        Assert.Equal(1.0, draft.Confidence);
    }

    [Fact]
    public void ProjectScopeRequiresAnExactNonEmptyProjectId()
    {
        var projectId = Guid.NewGuid();

        Assert.Equal(projectId, MemoryScope.ForProject(projectId).ProjectId);
        Assert.Throws<MemoryValidationException>(() => MemoryScope.ForProject(Guid.Empty));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public void UnknownCategoryFailsClosed(int rawCategory)
    {
        Assert.Throws<MemoryValidationException>(() => MemoryDraft.Create(
            (MemoryCategory)rawCategory,
            MemoryScope.Global,
            "标题",
            "正文",
            null,
            Now));
    }

    [Theory]
    [InlineData("", "正文")]
    [InlineData("   ", "正文")]
    [InlineData("标题", "")]
    [InlineData("标题", "   ")]
    [InlineData("标题\u0001", "正文")]
    [InlineData("标题\n换行", "正文")]
    [InlineData("标题\t制表", "正文")]
    [InlineData("标题", "正文\u0007")]
    public void EmptyOrControlCharacterContentFailsClosed(string title, string body)
    {
        Assert.Throws<MemoryValidationException>(() => MemoryDraft.Create(
            MemoryCategory.UserFact,
            MemoryScope.Global,
            title,
            body,
            null,
            Now));
    }

    [Fact]
    public void ContentAndExpiryLimitsFailClosed()
    {
        Assert.Throws<MemoryValidationException>(() => MemoryDraft.Create(
            MemoryCategory.UserFact,
            MemoryScope.Global,
            new string('题', 81),
            "正文",
            null,
            Now));
        Assert.Throws<MemoryValidationException>(() => MemoryDraft.Create(
            MemoryCategory.UserFact,
            MemoryScope.Global,
            "标题",
            new string('文', 2001),
            null,
            Now));
        Assert.Throws<MemoryValidationException>(() => MemoryDraft.Create(
            MemoryCategory.UserFact,
            MemoryScope.Global,
            "标题",
            "正文",
            Now,
            Now));
    }

    [Fact]
    public void PersistedMetadataRequiresExplicitSourceFixedConfidenceAndPositiveVersion()
    {
        var valid = MemoryMetadata.Create(
            Guid.NewGuid(),
            MemoryCategory.Decision,
            MemoryScope.Global,
            MemoryStatus.Active,
            MemorySourceKind.UserExplicit,
            Now,
            Now,
            null,
            1.0,
            1);

        Assert.Equal(1, valid.Version);
        Assert.Throws<MemoryValidationException>(() => valid with { Version = 0 });
        Assert.Throws<MemoryValidationException>(() => valid with { Confidence = 0.9 });
        Assert.Throws<MemoryValidationException>(() => valid with
        {
            SourceKind = (MemorySourceKind)99
        });
    }
}
