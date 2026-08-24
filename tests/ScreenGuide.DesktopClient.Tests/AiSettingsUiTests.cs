using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class AiSettingsUiTests
{
    [Fact]
    public void SettingsPageExposesAiRoutingPrivacyAndCredentialControls()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml"));

        Assert.Contains("普通聊天大脑", xaml, StringComparison.Ordinal);
        Assert.Contains("数据去向", xaml, StringComparison.Ordinal);
        Assert.Contains("同一会话的既有历史和下一条消息会发送给所选 Provider", xaml, StringComparison.Ordinal);
        Assert.Contains("编程任务仍由 Codex 负责", xaml, StringComparison.Ordinal);
        Assert.Contains("AI 服务（Provider）", xaml, StringComparison.Ordinal);
        Assert.Contains("聊天模型（Model）", xaml, StringComparison.Ordinal);
        Assert.Contains("使用 Codex 登录账号，无需在元枢保存 Key", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiResponsibilityBoundary\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiChatProvider\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiChatModel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SaveAiChatRoute\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiDataDestination\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiCredentialPassword\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiCredentialRequiredPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AiCredentialNotRequired\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<PasswordBox", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SaveAiCredential\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"DeleteAiCredential\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"CheckAiProviderHealth\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("TextSearch.TextPath=\"DisplayName\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, xaml.Split("AutomationProperties.Name=\"{Binding DisplayName}\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            2,
            xaml.Split(
                "<Setter Property=\"AutomationProperties.Name\" Value=\"{Binding DisplayName}\" />",
                StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("显示密钥", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AiSettingsControlsWireSelectionAndAllUserActions()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ScreenGuide.DesktopClient",
            "MainWindow.xaml"));

        Assert.Contains("SelectionChanged=\"AiChatProviderComboBox_SelectionChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PasswordChanged=\"AiCredentialPasswordBox_PasswordChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SaveAiChatRouteButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SaveAiCredentialButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DeleteAiCredentialButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CheckAiProviderHealthButton_Click\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CodexNotRequiredStateHidesCredentialActionsAndSurvivesHealthRefresh()
    {
        var provider = Provider("NotRequired");

        var presentation = AiSettingsUiPolicy.PresentCredential(
            provider,
            hasSecretInput: true,
            hostOnline: true);
        var refreshed = AiSettingsUiPolicy.ApplyHealth(
            provider,
            new AiProviderHealthDto(
                "codex",
                "Healthy",
                IsConfigured: true,
                "Codex 登录有效。"));

        Assert.False(presentation.ShowCredentialInputs);
        Assert.False(presentation.CanSave);
        Assert.False(presentation.CanDelete);
        Assert.Equal("使用 Codex 登录账号，无需在元枢保存 Key。", presentation.StatusText);
        Assert.Equal("NotRequired", refreshed.ConfigurationState);
    }

    [Theory]
    [InlineData("Missing", true, false)]
    [InlineData("Configured", true, true)]
    public void ApiKeyProviderShowsOnlyCredentialActionsAllowedByCurrentState(
        string configurationState,
        bool canSave,
        bool canDelete)
    {
        var presentation = AiSettingsUiPolicy.PresentCredential(
            Provider(configurationState),
            hasSecretInput: true,
            hostOnline: true);

        Assert.True(presentation.ShowCredentialInputs);
        Assert.Equal(canSave, presentation.CanSave);
        Assert.Equal(canDelete, presentation.CanDelete);
    }

    [Fact]
    public void SwitchingBackToCurrentProviderRestoresItsRoutedModelInsteadOfAStaleOtherProviderModel()
    {
        var provider = Provider("Configured") with
        {
            ProviderId = "deepseek",
            Models =
            [
                new AiModelSettingsDto("deepseek-chat", "DeepSeek Chat", ["Text"]),
                new AiModelSettingsDto("deepseek-reasoner", "DeepSeek Reasoner", ["Reasoning"])
            ]
        };

        var selectedModelId = AiSettingsUiPolicy.SelectModelId(
            provider,
            requestedModelId: "codex-default",
            new AiChatRouteDto("deepseek", "deepseek-reasoner"));

        Assert.Equal("deepseek-reasoner", selectedModelId);
    }

    [Fact]
    public async Task CredentialSubmissionClearsPasswordAfterFailedSaveAttempt()
    {
        var cleared = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AiCredentialSubmission.RunAsync(
                "fake-test-key",
                _ => throw new InvalidOperationException("safe failure"),
                () => cleared = true));

        Assert.True(cleared);
    }

    [Fact]
    public async Task CredentialSubmissionClearsPasswordAfterSuccessfulSaveAttempt()
    {
        var cleared = false;
        string? submitted = null;

        await AiCredentialSubmission.RunAsync(
            "fake-test-key",
            value =>
            {
                submitted = value;
                return Task.CompletedTask;
            },
            () => cleared = true);

        Assert.Equal("fake-test-key", submitted);
        Assert.True(cleared);
    }

    [Fact]
    public void CredentialDeletionRequiresProviderSpecificVisibleConfirmation()
    {
        var provider = Provider("Configured") with { DisplayName = "DeepSeek" };

        var confirmation = AiSettingsUiPolicy.CredentialDeleteConfirmation(provider);

        Assert.Contains("DeepSeek", confirmation, StringComparison.Ordinal);
        Assert.Contains("删除", confirmation, StringComparison.Ordinal);
        Assert.Contains("无法使用普通聊天", confirmation, StringComparison.Ordinal);
    }

    private static AiProviderSettingsDto Provider(string configurationState) => new(
        "codex",
        "Codex",
        "OpenAI Codex 服务",
        SendsDataOffDevice: true,
        configurationState,
        new AiProviderHealthDto(
            "codex",
            "Healthy",
            IsConfigured: true,
            "Codex 登录有效。"),
        [new AiModelSettingsDto("codex", "Codex", ["Coding"])]);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ScreenGuide.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("ScreenGuide repository root was not found.");
    }
}
