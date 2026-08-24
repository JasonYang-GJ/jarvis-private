using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenGuide.Agent.Abstractions;
using ScreenGuide.Agent.Codex;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.DeepSeek;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Security;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Evidence;
using ScreenGuide.Persistence.Runtime;
using ScreenGuide.Persistence.Sqlite;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;
using ScreenGuide.Vision.Abstractions;
using ScreenGuide.Vision.Windows;

namespace ScreenGuide.DesktopHost.Configuration;

public static class DesktopHostFactory
{
    public static IHost Build(
        string[] args,
        DesktopHostOptions? options = null,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = Host.CreateApplicationBuilder(args);
        var resolvedOptions = options ?? DesktopHostOptions.FromEnvironment();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new RollingFileLoggerProvider(resolvedOptions.LogsDirectory));

        builder.Services.AddSingleton(resolvedOptions);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ILocalTaskStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new SqliteTaskStore(hostOptions.DatabasePath);
        });
        builder.Services.AddSingleton<IConversationStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new SqliteConversationStore(hostOptions.DatabasePath);
        });
        builder.Services.AddSingleton<ISessionStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new SqliteSessionStore(hostOptions.DatabasePath);
        });
        builder.Services.AddSingleton<IAiInvocationStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new SqliteAiInvocationStore(hostOptions.DatabasePath);
        });
        builder.Services.AddSingleton<IProviderCredentialStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new WindowsDpapiCredentialStore(
                hostOptions.SecretsDirectory,
                services.GetRequiredService<TimeProvider>());
        });
        builder.Services.AddSingleton<IAiSettingsStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new FileAiSettingsStore(
                hostOptions.AiSettingsPath,
                new AiSettings(new ChatModelRoute(
                    CodexChatModelProvider.ProviderId,
                    CodexChatModelProvider.DefaultModelId)));
        });
        builder.Services.AddSingleton(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return PromptRegistry.LoadAsync(hostOptions.PromptRegistryDirectory)
                .GetAwaiter()
                .GetResult();
        });
        builder.Services.AddSingleton<TaskCancellationRegistry>();
        builder.Services.AddSingleton<TaskCancellationService>();
        builder.Services.AddSingleton<TaskRecoveryService>();
        builder.Services.AddSingleton(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new EvidenceOptions(hostOptions.EvidenceDataDirectory);
        });
        builder.Services.AddSingleton<GitEvidenceCollector>();
        builder.Services.AddSingleton<TaskEvidenceService>();
        builder.Services.AddSingleton(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return hostOptions.CodexExecutablePath is null
                ? CodexConnectorOptions.FromDataDirectory(hostOptions.CodexDataDirectory)
                : new CodexConnectorOptions(
                    hostOptions.CodexDataDirectory,
                    hostOptions.CodexExecutablePath);
        });
        builder.Services.AddSingleton<CodexConnector>();
        builder.Services.AddSingleton<CodexConversationProvider>();
        builder.Services.AddSingleton(services => new CodexChatModelProvider(
            services.GetRequiredService<CodexConnectorOptions>(),
            CodexChatModelExecutionPolicy.ProductionDisabled));
        builder.Services.AddSingleton<DeepSeekChatModelProvider>();
        builder.Services.AddSingleton<IChatModelProvider>(services =>
            services.GetRequiredService<CodexChatModelProvider>());
        builder.Services.AddSingleton<IChatModelProvider>(services =>
            services.GetRequiredService<DeepSeekChatModelProvider>());
        builder.Services.AddSingleton(services => new ChatProviderRegistry(
            services.GetServices<IChatModelProvider>()));
        builder.Services.AddSingleton<ModelRouter>();
        builder.Services.AddSingleton<AiSettingsService>();
        builder.Services.AddSingleton<ModelSemanticIntentSuggester>();
        builder.Services.AddSingleton<ISemanticIntentSuggester>(services =>
            services.GetRequiredService<ModelSemanticIntentSuggester>());
        builder.Services.AddSingleton<CodexDiagnosticsService>();
        builder.Services.AddSingleton<IAgentConnector>(services =>
            services.GetRequiredService<CodexConnector>());
        builder.Services.AddSingleton<CapabilityPolicyEngine>();
        builder.Services.AddSingleton<IIntentPlanner, DeterministicIntentPlanner>();
        builder.Services.AddSingleton<CodexSkillAdapter>();
        builder.Services.AddSingleton<ISkillAdapter>(services =>
            services.GetRequiredService<CodexSkillAdapter>());
        builder.Services.AddSingleton<IDesktopProcessLauncher, DesktopProcessLauncher>();
        builder.Services.AddSingleton<IInstalledApplicationCatalog, InstalledApplicationCatalog>();
        builder.Services.AddSingleton<IReliableDesktopAutomation, WindowsUiAutomationService>();
        builder.Services.AddSingleton<ForegroundWindowTracker>();
        builder.Services.AddSingleton<IForegroundWindowContextProvider>(services =>
            services.GetRequiredService<ForegroundWindowTracker>());
        builder.Services.AddSingleton<ISensitiveWindowPolicy, WindowsSensitiveWindowPolicy>();
        builder.Services.AddSingleton<WindowsGraphicsCaptureBackend>();
        builder.Services.AddSingleton<PrintWindowCaptureBackend>();
        builder.Services.AddSingleton<IExactWindowCaptureBackend, ResilientExactWindowCaptureBackend>();
        builder.Services.AddSingleton<IWindowCaptureService, WindowsSingleWindowCaptureService>();
        builder.Services.AddSingleton<ILocalOcrTextExtractor, WindowsLocalOcrTextExtractor>();
        builder.Services.AddSingleton<IWindowVisionProvider, WindowsLocalWindowVisionProvider>();
        builder.Services.AddSingleton<WindowUnderstandingService>();
        builder.Services.AddSingleton<WindowsDesktopSkillAdapter>();
        builder.Services.AddSingleton<ISkillAdapter>(services =>
            services.GetRequiredService<WindowsDesktopSkillAdapter>());
        builder.Services.AddSingleton<SkillAdapterRegistry>();
        builder.Services.AddSingleton<TaskSkillRouter>();
        builder.Services.AddSingleton<LocalDeviceInitializer>();
        builder.Services.AddSingleton<AgentConnectorRegistry>();
        builder.Services.AddSingleton<AgentTaskExecutionService>();
        builder.Services.AddSingleton<LocalTaskEntryService>();
        builder.Services.AddSingleton<RoutedConversationProvider>();
        builder.Services.AddSingleton<IConversationProvider>(services =>
            services.GetRequiredService<RoutedConversationProvider>());
        builder.Services.AddSingleton<ConversationService>();
        builder.Services.AddSingleton<SessionCoordinator>();
        builder.Services.AddSingleton<DesktopActionEntryService>();
        builder.Services.AddSingleton<AssistantCommandService>();
        builder.Services.AddSingleton<ProjectInspector>();
        builder.Services.AddSingleton<DesktopHostState>();
        builder.Services.AddSingleton<DesktopHostRuntime>();
        builder.Services.AddSingleton<RuntimeDataMaintenance>();
        builder.Services.AddHostedService<DesktopHostHostedService>();
        builder.Services.AddSingleton<DesktopApiDispatcher>();
        builder.Services.AddHostedService<DesktopIpcHostedService>();
        configureServices?.Invoke(builder.Services);

        return builder.Build();
    }
}
