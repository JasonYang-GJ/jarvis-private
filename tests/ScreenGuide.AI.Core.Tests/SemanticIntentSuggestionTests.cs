using ScreenGuide.AI.Core;
using Xunit;

namespace ScreenGuide.AI.Core.Tests;

public sealed class SemanticIntentSuggestionTests
{
    [Fact]
    public void ParsesOnlyTheDocumentedUntrustedSuggestionShape()
    {
        var suggestion = StrictSemanticIntentSuggestionParser.Parse("""
            {
              "kind": "CodingTask",
              "target": "这个项目",
              "confidence": 0.91,
              "isAmbiguous": false,
              "missingContext": "Project"
            }
            """);

        Assert.Equal(SemanticIntentKind.CodingTask, suggestion.Kind);
        Assert.Equal("这个项目", suggestion.TargetHint);
        Assert.Equal(0.91, suggestion.Confidence);
        Assert.False(suggestion.IsAmbiguous);
        Assert.Equal(SemanticMissingContext.Project, suggestion.MissingContext);
    }

    [Theory]
    [InlineData("{\"kind\":\"CodingTask\",\"target\":null,\"confidence\":0.9,\"isAmbiguous\":false,\"missingContext\":\"Project\",\"authorized\":true}")]
    [InlineData("{\"kind\":\"CodingTask\",\"kind\":\"Conversation\",\"target\":null,\"confidence\":0.9,\"isAmbiguous\":false,\"missingContext\":\"Project\"}")]
    [InlineData("回答如下：{\"kind\":\"CodingTask\",\"target\":null,\"confidence\":0.9,\"isAmbiguous\":false,\"missingContext\":\"Project\"}")]
    [InlineData("{\"kind\":\"DeleteFile\",\"target\":\"C:\\\\x\",\"confidence\":1,\"isAmbiguous\":false,\"missingContext\":\"None\"}")]
    [InlineData("{\"kind\":\"OpenFile\",\"target\":null,\"confidence\":1.1,\"isAmbiguous\":false,\"missingContext\":\"File\"}")]
    public void RejectsInjectionUnknownDuplicateOrOutOfRangeOutput(string json)
    {
        Assert.Throws<SemanticIntentFormatException>(() =>
            StrictSemanticIntentSuggestionParser.Parse(json));
    }

    [Fact]
    public void ValidLookingModelPathRemainsOnlyAHintAndCannotSkipFileSelection()
    {
        var suggestion = StrictSemanticIntentSuggestionParser.Parse("""
            {
              "kind": "OpenFile",
              "target": "C:\\private\\model-selected.txt",
              "confidence": 1,
              "isAmbiguous": false,
              "missingContext": "None"
            }
            """);
        var plan = new DeterministicIntentPlanner(TimeProvider.System).Plan(
            "打开刚才那个",
            new IntentPlanningContext(ExplicitUserIntent: UniversalIntentKind.OpenFile));

        Assert.Equal("C:\\private\\model-selected.txt", suggestion.TargetHint);
        Assert.Equal(IntentPlanReadiness.NeedsContext, plan.Readiness);
        Assert.Equal("文件", plan.MissingContext);
        Assert.Null(plan.Target);
        Assert.False(plan.RequiresConfirmation);
    }

    [Theory]
    [InlineData("帮我修改一下这个", true)]
    [InlineData("打开刚才那个", true)]
    [InlineData("看看这个", true)]
    [InlineData("第二个方案详细一点", false)]
    [InlineData("你觉得刚才那个方案怎么样", false)]
    [InlineData("今天天气怎么样", false)]
    public void CandidateDetectorOnlySelectsAmbiguousActionLikeRequests(
        string text,
        bool expected)
    {
        Assert.Equal(expected, SemanticIntentCandidateDetector.ShouldEvaluate(text));
    }

    [Theory]
    [InlineData(SemanticIntentKind.CodingTask, UniversalIntentKind.CodingTask)]
    [InlineData(SemanticIntentKind.OpenFile, UniversalIntentKind.OpenFile)]
    [InlineData(SemanticIntentKind.DescribeForeground, UniversalIntentKind.DescribeForeground)]
    public void HighConfidenceSuggestionCanOnlySelectASafeDeterministicReplanningKind(
        SemanticIntentKind suggested,
        UniversalIntentKind expected)
    {
        var actual = SemanticIntentSuggestionPolicy.SelectExplicitIntent(
            new SemanticIntentSuggestion(
                suggested,
                "模型声称的目标永远不会传给规划器",
                0.9,
                IsAmbiguous: false,
                SemanticMissingContext.None));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(SemanticIntentKind.Conversation, 0.99, false)]
    [InlineData(SemanticIntentKind.OpenFile, 0.79, false)]
    [InlineData(SemanticIntentKind.OpenFile, 0.99, true)]
    public void ConversationLowConfidenceOrAmbiguousSuggestionCannotChangeThePlanner(
        SemanticIntentKind kind,
        double confidence,
        bool ambiguous)
    {
        var actual = SemanticIntentSuggestionPolicy.SelectExplicitIntent(
            new SemanticIntentSuggestion(
                kind,
                "C:\\untrusted\\target.txt",
                confidence,
                ambiguous,
                SemanticMissingContext.None));

        Assert.Null(actual);
    }
}
