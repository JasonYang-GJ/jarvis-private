using Microsoft.Extensions.Hosting;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var runOnce = args.Any(argument =>
            string.Equals(argument, "--run-once", StringComparison.OrdinalIgnoreCase));
        var hostArguments = args
            .Where(argument => !string.Equals(
                argument,
                "--run-once",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        using var host = DesktopHostFactory.Build(hostArguments);

        try
        {
            if (runOnce)
            {
                await host.StartAsync().ConfigureAwait(false);
                await host.StopAsync().ConfigureAwait(false);
            }
            else
            {
                await host.RunAsync().ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Desktop Host terminated unexpectedly: {exception.GetType().Name}: {exception.Message}");
            await HostFailureRecorder.TryRecordUnhandledAsync(
                host.Services,
                exception,
                CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
    }
}
