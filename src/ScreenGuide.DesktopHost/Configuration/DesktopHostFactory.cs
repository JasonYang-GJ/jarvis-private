using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenGuide.Agent.Abstractions;
using ScreenGuide.Agent.Codex;
using ScreenGuide.Core.Security;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Evidence;
using ScreenGuide.Persistence.Runtime;
using ScreenGuide.Persistence.Sqlite;
using ScreenGuide.Skills.Abstractions;
using ScreenGuide.Skills.Windows;

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
        builder.Logging.AddProvider(new RollingFileLoggerProvider(resolvedOptions.LogsDirectory));

        builder.Services.AddSingleton(resolvedOptions);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ILocalTaskStore>(services =>
        {
            var hostOptions = services.GetRequiredService<DesktopHostOptions>();
            return new SqliteTaskStore(hostOptions.DatabasePath);
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
        builder.Services.AddSingleton<CodexDiagnosticsService>();
        builder.Services.AddSingleton<IAgentConnector>(services =>
            services.GetRequiredService<CodexConnector>());
        builder.Services.AddSingleton<CapabilityPolicyEngine>();
        builder.Services.AddSingleton<CodexSkillAdapter>();
        builder.Services.AddSingleton<ISkillAdapter>(services =>
            services.GetRequiredService<CodexSkillAdapter>());
        builder.Services.AddSingleton<IDesktopProcessLauncher, DesktopProcessLauncher>();
        builder.Services.AddSingleton<WindowsDesktopSkillAdapter>();
        builder.Services.AddSingleton<ISkillAdapter>(services =>
            services.GetRequiredService<WindowsDesktopSkillAdapter>());
        builder.Services.AddSingleton<SkillAdapterRegistry>();
        builder.Services.AddSingleton<TaskSkillRouter>();
        builder.Services.AddSingleton<LocalDeviceInitializer>();
        builder.Services.AddSingleton<AgentConnectorRegistry>();
        builder.Services.AddSingleton<AgentTaskExecutionService>();
        builder.Services.AddSingleton<LocalTaskEntryService>();
        builder.Services.AddSingleton<DesktopActionEntryService>();
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
