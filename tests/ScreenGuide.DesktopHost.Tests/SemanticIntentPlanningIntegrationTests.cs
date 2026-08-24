using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.AI.Core;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class SemanticIntentPlanningIntegrationTests
{
    [Fact]
    public async Task AssistantCommandPlan_is_pure_deterministic_and_never_calls_semantic_model()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var suggester = new FixedSuggester(new SemanticIntentSuggestion(
            SemanticIntentKind.OpenFile,
            "C:\\untrusted\\model-target.txt",
            0.95,
            IsAmbiguous: false,
            SemanticMissingContext.File));
        using var host = environment.BuildHost(services =>
            services.AddSingleton<ISemanticIntentSuggester>(suggester));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var plan = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("帮我处理一下这个"));
        await host.StopAsync();

        Assert.Equal("Conversation", plan.IntentKind);
        Assert.Equal("Ready", plan.Readiness);
        Assert.Null(plan.MissingContext);
        Assert.Equal("帮我处理一下这个", plan.CanonicalTarget);
        Assert.DoesNotContain("model-target", plan.UserSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, suggester.CallCount);
    }

    [Fact]
    public async Task AssistantCommandPlanKeepsAmbiguousTextAndExplicitSafeActionDeterministic()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var suggester = new FixedSuggester(new SemanticIntentSuggestion(
            SemanticIntentKind.OpenFile,
            "C:\\untrusted\\model-target.txt",
            0.4,
            IsAmbiguous: false,
            SemanticMissingContext.File));
        using var host = environment.BuildHost(services =>
            services.AddSingleton<ISemanticIntentSuggester>(suggester));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);

        var ambiguous = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("帮我处理一下这个"));
        var explicitAction = await client.PlanAssistantCommandAsync(
            new PlanAssistantCommandRequestDto("打开记事本"));
        await host.StopAsync();

        Assert.Equal("Conversation", ambiguous.IntentKind);
        Assert.Equal("OpenApplication", explicitAction.IntentKind);
        Assert.Equal("notepad", explicitAction.CanonicalTarget);
        Assert.Equal(0, suggester.CallCount);
    }

    private sealed class FixedSuggester(SemanticIntentSuggestion suggestion)
        : ISemanticIntentSuggester
    {
        public int CallCount { get; private set; }

        public Task<SemanticIntentSuggestion?> SuggestAsync(
            Guid sessionTurnId,
            FrozenChatModelRoute frozenRoute,
            string text,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<SemanticIntentSuggestion?>(suggestion);
        }
    }
}
