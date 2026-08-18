using System.Collections.Concurrent;
using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.DesktopHost.Configuration;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class DesktopIpcHostedService(
    DesktopApiDispatcher dispatcher,
    DesktopHostOptions options,
    ILogger<DesktopIpcHostedService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private int _nextConnectionId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeName = options.PipeName;
        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }

            var connectionId = Interlocked.Increment(ref _nextConnectionId);
            var task = HandleConnectionAsync(pipe, stoppingToken);
            _connections[connectionId] = task;
            _ = task.ContinueWith(
                completedTask => _connections.TryRemove(connectionId, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        var active = _connections.Values.ToArray();
        if (active.Length > 0)
        {
            await Task.WhenAll(active).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            DesktopApiRequest? request = null;
            try
            {
                request = await DesktopIpcFraming.ReadAsync<DesktopApiRequest>(
                    pipe,
                    cancellationToken).ConfigureAwait(false);
                var response = await dispatcher.DispatchAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                await DesktopIpcFraming.WriteAsync(pipe, response, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Local IPC request failed.");
                if (request is not null && pipe.IsConnected)
                {
                    try
                    {
                        await DesktopIpcFraming.WriteAsync(
                            pipe,
                            new DesktopApiResponse(
                                request.RequestId,
                                false,
                                Error: DesktopApiErrors.FromException(exception)),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
