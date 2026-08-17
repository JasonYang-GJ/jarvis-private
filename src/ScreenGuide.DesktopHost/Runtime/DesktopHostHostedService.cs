using Microsoft.Extensions.Hosting;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class DesktopHostHostedService(DesktopHostRuntime runtime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        runtime.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        runtime.StopAsync(cancellationToken);
}
