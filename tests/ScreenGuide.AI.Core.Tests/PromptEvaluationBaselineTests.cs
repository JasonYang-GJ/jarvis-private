using System.Text.Json;
using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class PromptEvaluationBaselineTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void BaselineIsVersionedUniqueAndHasTheLockedStage2CaseCounts()
    {
        var baseline = LoadBaseline();

        Assert.Equal(1, baseline.SchemaVersion);
        Assert.Equal("stage2-prompt-evaluation-v1", baseline.BaselineId);
        Assert.Equal(2, baseline.Prompts.Count);
        Assert.Equal(4, baseline.ChatCases.Count);
        Assert.Equal(13, baseline.IntentCases.Count);
        Assert.Equal(
            baseline.Prompts.Count + baseline.ChatCases.Count + baseline.IntentCases.Count,
            baseline.Prompts.Select(item => $"prompt:{item.Id}@{item.Version}")
                .Concat(baseline.ChatCases.Select(item => $"chat:{item.Id}"))
                .Concat(baseline.IntentCases.Select(item => $"intent:{item.Id}"))
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Contains(baseline.ChatCases, item => item.Category == "continuous-multiturn");
        Assert.Contains(baseline.ChatCases, item => item.Category == "prior-reference");
        Assert.Contains(baseline.IntentCases, item => item.Category == "project-semantics");
        Assert.Contains(baseline.IntentCases, item => item.Category == "file-semantics");
        Assert.Contains(baseline.IntentCases, item => item.Category == "window-semantics");
        Assert.Contains(baseline.IntentCases, item => item.Category == "prompt-injection");
        Assert.Contains(baseline.IntentCases, item => item.Category == "forged-authorization");
        Assert.Contains(baseline.IntentCases, item => item.Category == "unknown-field");
        Assert.Contains(baseline.IntentCases, item => item.Category == "duplicate-field");
        Assert.Contains(baseline.IntentCases, item => item.Category == "low-confidence");
    }

    [Fact]
    public async Task RegisteredPromptsMatchThePinnedHashesAndRequiredSafetyClauses()
    {
        var baseline = LoadBaseline();
        var registry = await LoadRepositoryPromptsAsync();

        foreach (var expectation in baseline.Prompts)
        {
            var prompt = registry.GetRequired(expectation.Id, expectation.Version, "offline-evaluator");
            Assert.Equal(expectation.Sha256, prompt.ContentSha256);
            foreach (var clause in expectation.RequiredClauses)
            {
                Assert.Contains(clause, prompt.Content, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ChatCaseIds))]
    public void ChatBaselineKeepsPriorEvidenceAndOrdinaryChatOutsideActionEvaluation(
        string caseId)
    {
        var item = LoadBaseline().ChatCases.Single(value => value.Id == caseId);
        var messages = item.Messages.Select(message => new ChatMessage(
            Enum.Parse<ChatMessageRole>(message.Role, ignoreCase: false),
            message.Content)).ToArray();

        Assert.NotEmpty(messages);
        Assert.Equal(ChatMessageRole.User, messages[^1].Role);
        Assert.True(messages.Length >= item.MinimumMessageCount);
        Assert.Equal(
            item.ShouldEvaluateSemanticIntent,
            SemanticIntentCandidateDetector.ShouldEvaluate(messages[^1].Content));

        var priorMessages = messages[..^1];
        foreach (var reference in item.RequiredPriorEvidence)
        {
            Assert.Contains(
                priorMessages,
                message => message.Content.Contains(reference, StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(IntentCaseIds))]
    public void IntentBaselineIsStrictCountableAndNeverTurnsHintsIntoAuthority(string caseId)
    {
        var item = LoadBaseline().IntentCases.Single(value => value.Id == caseId);
        Assert.Equal(
            item.ShouldEvaluate,
            SemanticIntentCandidateDetector.ShouldEvaluate(item.UserText));

        if (item.ModelOutput is null)
        {
            Assert.False(item.ShouldEvaluate);
            Assert.Equal("NotEvaluated", item.ParseOutcome);
            return;
        }

        if (item.ParseOutcome == "Rejected")
        {
            Assert.Throws<SemanticIntentFormatException>(() =>
                StrictSemanticIntentSuggestionParser.Parse(item.ModelOutput));
            return;
        }

        Assert.Equal("Accepted", item.ParseOutcome);
        var suggestion = StrictSemanticIntentSuggestionParser.Parse(item.ModelOutput);
        Assert.Equal(Enum.Parse<SemanticIntentKind>(item.ExpectedKind!), suggestion.Kind);
        Assert.Equal(
            Enum.Parse<SemanticMissingContext>(item.ExpectedMissingContext!),
            suggestion.MissingContext);
        Assert.Equal(item.ExpectedConfidence, suggestion.Confidence, precision: 6);

        var explicitIntent = SemanticIntentSuggestionPolicy.SelectExplicitIntent(suggestion);
        Assert.Equal(
            item.ExpectedExplicitIntent is null
                ? null
                : Enum.Parse<UniversalIntentKind>(item.ExpectedExplicitIntent),
            explicitIntent);

        if (item.ExpectedPlannerMissingContext is null)
        {
            return;
        }

        Assert.NotNull(explicitIntent);
        var plan = new DeterministicIntentPlanner(TimeProvider.System).Plan(
            item.UserText,
            new IntentPlanningContext(ExplicitUserIntent: explicitIntent));
        Assert.Equal(IntentPlanReadiness.NeedsContext, plan.Readiness);
        Assert.Equal(item.ExpectedPlannerMissingContext, plan.MissingContext);
        Assert.Null(plan.Target);
        Assert.False(plan.RequiresConfirmation);
    }

    public static IEnumerable<object[]> ChatCaseIds() =>
        LoadBaseline().ChatCases.Select(item => new object[] { item.Id });

    public static IEnumerable<object[]> IntentCaseIds() =>
        LoadBaseline().IntentCases.Select(item => new object[] { item.Id });

    private static PromptEvaluationBaseline LoadBaseline()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "ScreenGuide.AI.Core.Tests",
            "TestData",
            "prompt-evaluation.v1.json");
        return JsonSerializer.Deserialize<PromptEvaluationBaseline>(
                   File.ReadAllText(path),
                   JsonOptions)
               ?? throw new InvalidDataException("Prompt 评估基线为空。");
    }

    private static Task<PromptRegistry> LoadRepositoryPromptsAsync() =>
        PromptRegistry.LoadAsync(Path.Combine(FindRepositoryRoot(), "prompts", "runtime"));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ScreenGuide.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
               ?? throw new DirectoryNotFoundException("找不到测试仓库根目录。");
    }

    private sealed record PromptEvaluationBaseline
    {
        public int SchemaVersion { get; init; }

        public required string BaselineId { get; init; }

        public IReadOnlyList<PromptExpectation> Prompts { get; init; } = [];

        public IReadOnlyList<ChatEvaluationCase> ChatCases { get; init; } = [];

        public IReadOnlyList<IntentEvaluationCase> IntentCases { get; init; } = [];
    }

    private sealed record PromptExpectation
    {
        public required string Id { get; init; }

        public required string Version { get; init; }

        public required string Sha256 { get; init; }

        public IReadOnlyList<string> RequiredClauses { get; init; } = [];
    }

    private sealed record ChatEvaluationCase
    {
        public required string Id { get; init; }

        public required string Category { get; init; }

        public int MinimumMessageCount { get; init; }

        public bool ShouldEvaluateSemanticIntent { get; init; }

        public IReadOnlyList<string> RequiredPriorEvidence { get; init; } = [];

        public IReadOnlyList<EvaluationMessage> Messages { get; init; } = [];
    }

    private sealed record EvaluationMessage
    {
        public required string Role { get; init; }

        public required string Content { get; init; }
    }

    private sealed record IntentEvaluationCase
    {
        public required string Id { get; init; }

        public required string Category { get; init; }

        public required string UserText { get; init; }

        public bool ShouldEvaluate { get; init; }

        public string? ModelOutput { get; init; }

        public required string ParseOutcome { get; init; }

        public string? ExpectedKind { get; init; }

        public string? ExpectedMissingContext { get; init; }

        public double ExpectedConfidence { get; init; }

        public string? ExpectedExplicitIntent { get; init; }

        public string? ExpectedPlannerMissingContext { get; init; }
    }
}
