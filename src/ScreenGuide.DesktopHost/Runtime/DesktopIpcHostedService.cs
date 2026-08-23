using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
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
    private const int MaximumConcurrentConnections = 64;
    private const int MaximumBusyResponses = 8;
    private static readonly TimeSpan InitialRequestTimeout = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly SemaphoreSlim _connectionSlots = new(MaximumConcurrentConnections);
    private readonly SemaphoreSlim _busyResponseSlots = new(MaximumBusyResponses);
    private int _nextConnectionId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeName = options.PipeName;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }

                break;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }

                logger.LogWarning(
                    exception,
                    "Local IPC listener will retry; active connections: {ActiveConnections}.",
                    _connections.Count);
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
                continue;
            }

            Task task;
            if (_connectionSlots.Wait(0))
            {
                task = HandleConnectionAsync(pipe!, stoppingToken);
            }
            else if (_busyResponseSlots.Wait(0))
            {
                task = HandleBusyConnectionAsync(pipe!, stoppingToken);
            }
            else
            {
                await pipe!.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            TrackConnection(task);
        }
    }

    private void TrackConnection(Task task)
    {
        var connectionId = Interlocked.Increment(ref _nextConnectionId);
        _connections[connectionId] = task;
        _ = task.ContinueWith(
            completedTask => _connections.TryRemove(connectionId, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
        await Task.Yield();
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                DesktopApiRequest request;
                try
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    requestTimeout.CancelAfter(InitialRequestTimeout);
                    request = await DesktopIpcFraming.ReadAsync<DesktopApiRequest>(
                        pipe,
                        requestTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("Local IPC client did not send a request within the time limit.");
                    return;
                }

                try
                {
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
                    if (pipe.IsConnected)
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
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException)
        {
            logger.LogWarning(exception, "Local IPC client sent an invalid request frame.");
        }
        finally
        {
            _connectionSlots.Release();
        }
    }

    private async Task HandleBusyConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                requestTimeout.CancelAfter(InitialRequestTimeout);
                try
                {
                    var request = await DesktopIpcFraming.ReadAsync<DesktopApiRequest>(
                        pipe,
                        requestTimeout.Token).ConfigureAwait(false);
                    await DesktopIpcFraming.WriteAsync(
                        pipe,
                        new DesktopApiResponse(
                            request.RequestId,
                            false,
                            Error: new DesktopApiError(
                                "ipc_busy",
                                "本机会话中枢正忙，请稍后重试。")),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or JsonException)
                {
                    logger.LogWarning(exception, "Busy local IPC client sent an invalid request frame.");
                }
            }
        }
        finally
        {
            _busyResponseSlots.Release();
        }
    }
}
