using ScreenGuide.Core.Security;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Core.Tests;

public sealed class CapabilityPolicyEngineTests
{
    private static readonly SkillDescriptor Codex = new(
        "codex.project-task",
        "0.2.0",
        "Codex 项目任务",
        true,
        new HashSet<string>(StringComparer.Ordinal) { "coding.execute" });

    [Fact]
    public void AllowsTrustedCodexOnlyForOneExplicitProjectScope()
    {
        var policy = new CapabilityPolicyEngine();
        var request = Request();

        var decision = policy.AuthorizeOnce(Codex, request);
        var repeated = policy.AuthorizeOnce(Codex, request);

        Assert.True(decision.IsAllowed);
        Assert.False(repeated.IsAllowed);
        Assert.Equal("action_authorization_consumed", repeated.Code);
    }

    [Theory]
    [InlineData(SkillAuthorizationOrigin.Model)]
    [InlineData(SkillAuthorizationOrigin.ScreenContent)]
    [InlineData(SkillAuthorizationOrigin.Document)]
    [InlineData(SkillAuthorizationOrigin.Website)]
    public void ContentAndModelOutputCannotAuthorizeAnAction(SkillAuthorizationOrigin origin)
    {
        var decision = new CapabilityPolicyEngine().Evaluate(
            Codex,
            Request() with { AuthorizationOrigin = origin });

        Assert.False(decision.IsAllowed);
        Assert.Equal("explicit_user_authorization_required", decision.Code);
    }

    [Fact]
    public void DeniesUnknownSkillCapabilityAndBroadScopes()
    {
        var policy = new CapabilityPolicyEngine();
        var unknownCapability = policy.Evaluate(
            Codex,
            Request() with { Capability = "computer.delete" });
        var broadScope = policy.Evaluate(
            Codex,
            Request() with
            {
                ResourceScopes =
                [
                    new SkillResourceScope("Project", Guid.NewGuid(), "C:\\safe", "Execute"),
                    new SkillResourceScope("Directory", null, "C:\\", "Execute")
                ]
            });

        Assert.Equal("capability_not_allowed", unknownCapability.Code);
        Assert.Equal("resource_scope_required", broadScope.Code);
    }

    [Theory]
    [InlineData("desktop.application.open", "Application", "notepad")]
    [InlineData("desktop.website.open", "Website", "https://example.com/")]
    [InlineData("browser.open", "Website", "https://example.com/")]
    [InlineData("file.open", "File", "selected-file.txt")]
    [InlineData("desktop.search.submit", "Window", "42")]
    [InlineData("desktop.window.describe", "Window", "42")]
    public void AllowsOnlyExplicitOneTimeDesktopLaunchWithMatchingScope(
        string capability,
        string scopeType,
        string scopeValue)
    {
        var descriptor = new SkillDescriptor(
            "windows.safe-launch",
            "0.2.0",
            "Windows 安全启动",
            true,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "desktop.application.open",
                "desktop.website.open",
                "browser.open",
                "file.open",
                "desktop.search.submit",
                "desktop.window.describe"
            });
        var request = new SkillInvocationRequest(
            Guid.Empty,
            Guid.NewGuid(),
            descriptor.Id,
            capability,
            "{}",
            [new SkillResourceScope(scopeType, null, scopeValue, "Execute")],
            SkillAuthorizationOrigin.ExplicitUser,
            Guid.NewGuid());
        var policy = new CapabilityPolicyEngine();

        var allowed = policy.AuthorizeOnce(descriptor, request);
        var repeated = policy.AuthorizeOnce(descriptor, request);
        var wrongScope = policy.Evaluate(request: request with
        {
            ActionAuthorizationId = Guid.NewGuid(),
            ResourceScopes = [new SkillResourceScope("Directory", null, "C:\\", "Execute")]
        }, descriptor: descriptor);

        Assert.True(allowed.IsAllowed);
        Assert.Equal("action_authorization_consumed", repeated.Code);
        Assert.Equal("resource_scope_required", wrongScope.Code);
    }

    private static SkillInvocationRequest Request() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Codex.Id,
        "coding.execute",
        "{}",
        [new SkillResourceScope("Project", Guid.NewGuid(), "C:\\safe", "Execute")],
        SkillAuthorizationOrigin.ExplicitUser,
        Guid.NewGuid());
}
