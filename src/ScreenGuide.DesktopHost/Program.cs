using Microsoft.Extensions.Hosting;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.DesktopHost.Runtime;

namespace ScreenGuide.DesktopHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = DesktopHostOptions.FromEnvironment();
        using var singleInstance = HostSingleInstanceLease.TryAcquire();
        if (singleInstance is null)
        {
            return 0;
        }

        var runOnce = args.Any(argument =>
            string.Equals(argument, "--run-once", StringComparison.OrdinalIgnoreCase));
        var hostArguments = args
            .Where(argument => !string.Equals(
                argument,
                "--run-once",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        IHost? host = null;
        try
        {
            HostStartupStatusStore.Clear(options);
            host = DesktopHostFactory.Build(hostArguments, options);
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
            if (host is not null)
            {
                await HostFailureRecorder.TryRecordUnhandledAsync(
                    host.Services,
                    exception,
                    CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                HostStartupStatusStore.Record(options, exception);
            }
            catch
            {
            }

            return 1;
        }
        finally
        {
            host?.Dispose();
        }
    }
}
