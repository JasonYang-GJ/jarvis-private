using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenGuide.Agent.Codex;
using ScreenGuide.AI.Core;
using ScreenGuide.AI.DeepSeek;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopV01.RealAcceptanceRunner;

public static class R3RunnerHostComposition
{
    public static IHost BuildCurrentUser(
        DesktopHostOptions hostOptions,
        R3DeepSeekValidationOptions validation)
    {
        var canonicalCredentialRoot = R3CanonicalCredentialStoreRoot.ResolveCurrentUser();
        return BuildCore(
            hostOptions,
            validation,
            serviceProvider => R3SecureCredentialLeaseBinding.CreateReadOnly(
                new WindowsDpapiCredentialStore(
                    canonicalCredentialRoot,
                    serviceProvider.GetRequiredService<TimeProvider>())),
            () => new HttpClientHandler { AllowAutoRedirect = false });
    }

    public static IHost BuildOffline(
        DesktopHostOptions hostOptions,
        R3DeepSeekValidationOptions validation,
        IProviderCredentialStore fakeCredentialStore,
        HttpMessageHandler stubTransport)
    {
        ArgumentNullException.ThrowIfNull(fakeCredentialStore);
        ArgumentNullException.ThrowIfNull(stubTransport);
        return BuildCore(
            hostOptions,
            validation,
            _ => R3SecureCredentialLeaseBinding.CreateReadOnly(fakeCredentialStore),
            () => stubTransport);
    }

    private static IHost BuildCore(
        DesktopHostOptions hostOptions,
        R3DeepSeekValidationOptions validation,
        Func<IServiceProvider, IProviderCredentialStore> credentialStoreFactory,
        Func<HttpMessageHandler> transportFactory)
    {
        ArgumentNullException.ThrowIfNull(hostOptions);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(credentialStoreFactory);
        ArgumentNullException.ThrowIfNull(transportFactory);
        if (!validation.IsValid
            || validation.Budget is null
            || string.IsNullOrWhiteSpace(validation.ExpectedModelId))
        {
            throw new R3ValidationFailureException("r3_runner_composition_invalid");
        }

        return DesktopHostFactory.Build(
            [],
            hostOptions,
            services =>
            {
                services.AddSingleton<IProviderCredentialStore>(credentialStoreFactory);
                services.AddSingleton<R3SafeResponseShapeCollector>();
                services.AddSingleton(serviceProvider =>
                {
                    var responseShapes = serviceProvider
                        .GetRequiredService<R3SafeResponseShapeCollector>();
                    return new R3BudgetedChatModelProvider(
                        new DeepSeekChatModelProvider(
                            serviceProvider.GetRequiredService<IProviderCredentialStore>(),
                            new R3SafeResponseShapeTrackingHandler(
                                transportFactory(),
                                responseShapes)),
                        validation.ExpectedModelId,
                        validation.Budget,
                        responseShapes);
                });
                services.AddSingleton(serviceProvider => new ChatProviderRegistry(
                [
                    serviceProvider.GetRequiredService<CodexChatModelProvider>(),
                    serviceProvider.GetRequiredService<R3BudgetedChatModelProvider>()
                ]));
            });
    }
}
