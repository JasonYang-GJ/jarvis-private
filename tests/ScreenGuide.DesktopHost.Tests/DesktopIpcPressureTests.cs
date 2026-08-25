using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.Core.Conversations;
using ScreenGuide.DesktopProtocol;
using Xunit.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopIpcPressureCollection
{
    public const string Name = "Desktop IPC pressure serial";
}

[Collection(DesktopIpcPressureCollection.Name)]
public sealed class DesktopIpcPressureTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ManyWaitersWithoutASessionStayBlockedUntilTheFirstSessionExists()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(
            environment.Options.PipeName,
            TimeSpan.FromSeconds(3));

        var waiters = Enumerable.Range(0, 12)
            .Select(_ => client.WaitForSessionUpdateAsync(
                knownChangeVersion: -1,
                waitMilliseconds: 3_000))
            .ToArray();
        await Task.Delay(300);
        var stillWaitingCount = waiters.Count(wait => !wait.IsCompleted);
        var pingWhileWaiting = await client.PingAsync();

        var wakeup = Stopwatch.StartNew();
        var created = await client.StartNewSessionAsync("首次会话唤醒压力测试");
        var results = await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(4));
        wakeup.Stop();
        await host.StopAsync();

        output.WriteLine(
            $"无Session：300ms时仍等待 {stillWaitingCount}/12；" +
            $"Ping={pingWhileWaiting}；创建Session后 {wakeup.Elapsed.TotalMilliseconds:F0} ms 唤醒全部。 ");

        Assert.Equal(12, stillWaitingCount);
        Assert.True(pingWhileWaiting);
        Assert.All(results, result =>
        {
            Assert.NotNull(result);
            Assert.Equal(created.SessionId, result.SessionId);
            Assert.True(result.ChangeVersion >= created.ChangeVersion);
        });
    }

    [Fact]
    public async Task TwoLongPollersTenConversationRoundsAndFiveWayRefreshesRemainResponsive()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new PressureConversationProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(
            environment.Options.PipeName,
            TimeSpan.FromSeconds(3));
        var session = await client.StartNewSessionAsync("混合并发压力");
        using var pollingLifetime = new CancellationTokenSource();
        var firstPoller = RunLongPollerAsync(
            client,
            session.ChangeVersion,
            pollingLifetime.Token);
        var secondPoller = RunLongPollerAsync(
            client,
            session.ChangeVersion,
            pollingLifetime.Token);
        var pingMeasurements = new List<PingMeasurement>();

        for (var round = 1; round <= 10; round++)
        {
            var submit = client.SubmitSessionInputAsync(new SessionInputRequestDto(
                $"连续压力对话第 {round} 轮",
                "Text",
                $"pressure-conversation-{round}",
                session.SessionId));
            var refresh = RunFiveWayRefreshAsync(client);
            var ping = MeasurePingAsync(client);

            var submitted = await submit;
            await refresh;
            pingMeasurements.Add(await ping);
            await WaitForTurnPhaseAsync(client, submitted.TurnId, "Completed");
        }

        var final = await client.GetCurrentSessionAsync();
        pollingLifetime.Cancel();
        var pollCounts = await Task.WhenAll(firstPoller, secondPoller)
            .WaitAsync(TimeSpan.FromSeconds(3));
        await host.StopAsync();

        output.WriteLine(
            $"混合负载：10轮Fake Provider完成；两个长轮询观察次数={string.Join(',', pollCounts)}；" +
            $"10次Ping最大用时={pingMeasurements.Max(item => item.Elapsed).TotalMilliseconds:F0} ms。 ");

        Assert.NotNull(final);
        Assert.Equal(10, provider.SendCount);
        Assert.Equal(20, final.Messages.Count);
        Assert.Equal("Fake 回答 10", final.Messages[^1].Content);
        Assert.All(final.Turns, turn => Assert.Equal("Completed", turn.Phase));
        Assert.All(pollCounts, count => Assert.True(count > 0));
        Assert.All(pingMeasurements, measurement =>
        {
            Assert.True(measurement.Succeeded);
            Assert.True(
                measurement.Elapsed < TimeSpan.FromSeconds(2),
                $"混合并发期间 Ping 用时 {measurement.Elapsed.TotalMilliseconds:F0} ms。");
        });
        Assert.All(
            provider.Requests.Skip(1),
            request => Assert.Equal("thread-pressure-fake", request.ExternalThreadId));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public Task InstantMixedWaitsWithinTheConnectionLimitDrainAndRecover(int operationCount) =>
        RunPressureBoundaryAsync(
            operationCount,
            batchSize: operationCount,
            waitForServerDrainBetweenBatches: false,
            requireEveryOperationByFifteenSeconds: true,
            scenario: $"{operationCount} 瞬时混合");

    [Fact]
    public Task OneHundredTwentyEightMixedWaitsInFourDrainedBatchesRecover() =>
        RunPressureBoundaryAsync(
            operationCount: 128,
            batchSize: 32,
            waitForServerDrainBetweenBatches: true,
            requireEveryOperationByFifteenSeconds: true,
            scenario: "128 分四批，每批32并等待排空");

    [Fact]
    public Task OneHundredTwentyEightInstantMixedWaitsRemainABoundedDosBoundary() =>
        RunPressureBoundaryAsync(
            operationCount: 128,
            batchSize: 128,
            waitForServerDrainBetweenBatches: false,
            requireEveryOperationByFifteenSeconds: false,
            scenario: "128 瞬时混合");

    [Fact]
    public async Task SilentPipeClientIsReleasedWithinFiveSecondsWithoutBlockingOtherClients()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(
            environment.Options.PipeName,
            TimeSpan.FromSeconds(3));
        await using var silentClient = new NamedPipeClientStream(
            ".",
            environment.Options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await silentClient.ConnectAsync(3_000);

        var disconnect = ObserveServerDisconnectAsync(silentClient);
        var pingWhileSilent = await MeasurePingAsync(client);
        var createdWhileSilent = await client.StartNewSessionAsync("静默连接旁路可用性");
        var disconnected = await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
        var pingAfterRelease = await MeasurePingAsync(client);
        await host.StopAsync();

        output.WriteLine(
            $"静默Pipe：{disconnected.Elapsed.TotalMilliseconds:F0} ms释放；" +
            $"静默期间Ping={pingWhileSilent.Succeeded} ({pingWhileSilent.Elapsed.TotalMilliseconds:F0} ms)；" +
            $"释放后Ping={pingAfterRelease.Succeeded} ({pingAfterRelease.Elapsed.TotalMilliseconds:F0} ms)。 ");

        Assert.True(disconnected.WasDisconnected);
        Assert.True(
            disconnected.Elapsed >= TimeSpan.FromMilliseconds(700),
            $"静默连接在 {disconnected.Elapsed.TotalMilliseconds:F0} ms 即断开，未经过首帧宽限期。");
        Assert.True(
            disconnected.Elapsed < TimeSpan.FromSeconds(5),
            $"静默连接释放用时 {disconnected.Elapsed.TotalMilliseconds:F0} ms。");
        Assert.True(pingWhileSilent.Succeeded);
        Assert.True(pingWhileSilent.Elapsed < TimeSpan.FromSeconds(2));
        Assert.NotEqual(Guid.Empty, createdWhileSilent.SessionId);
        Assert.True(pingAfterRelease.Succeeded);
        Assert.True(pingAfterRelease.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MalformedPipeFrameCannotBreakTheNextClientOrHostShutdown()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        await using (var malformedClient = new NamedPipeClientStream(
                         ".",
                         environment.Options.PipeName,
                         PipeDirection.InOut,
                         PipeOptions.Asynchronous,
                         TokenImpersonationLevel.Identification))
        {
            await malformedClient.ConnectAsync(3_000);
            await malformedClient.WriteAsync(BitConverter.GetBytes(-1));
            await malformedClient.FlushAsync();
            _ = await ObserveServerDisconnectAsync(malformedClient)
                .WaitAsync(TimeSpan.FromSeconds(3));
        }

        IDesktopApiClient client = new DesktopApiClient(
            environment.Options.PipeName,
            TimeSpan.FromSeconds(3));
        var ping = await client.PingAsync();
        var stopError = await Record.ExceptionAsync(() => host.StopAsync());

        Assert.True(ping);
        Assert.Null(stopError);
    }

    private async Task RunPressureBoundaryAsync(
        int operationCount,
        int batchSize,
        bool waitForServerDrainBetweenBatches,
        bool requireEveryOperationByFifteenSeconds,
        string scenario)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var provider = new PressureConversationProvider();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IConversationProvider>(provider));
        await host.StartAsync();
        IDesktopApiClient loadClient = new DesktopApiClient(
            environment.Options.PipeName,
            TimeSpan.FromSeconds(3));
        var session = await loadClient.StartNewSessionAsync(scenario);
        var knownChangeVersion = session.ChangeVersion;
        var currentSessionId = session.SessionId;
        Guid? drainSessionId = null;
        if (waitForServerDrainBetweenBatches)
        {
            var drainSession = await loadClient.StartNewSessionAsync($"{scenario} 排空屏障");
            drainSessionId = drainSession.SessionId;
            var restored = await loadClient.SetCurrentSessionAsync(session.SessionId);
            knownChangeVersion = restored.ChangeVersion;
            currentSessionId = restored.SessionId;
        }

        using var pressureLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pressure = Stopwatch.StartNew();
        var operations = new List<Task<WaitPressureOutcome>>(operationCount);

        for (var offset = 0; offset < operationCount; offset += batchSize)
        {
            var count = Math.Min(batchSize, operationCount - offset);
            var batch = Enumerable.Range(offset, count)
                .Select(index => index % 2 == 0
                    ? RunCancelledWaitAsync(
                        environment.Options.PipeName,
                        knownChangeVersion,
                        pressureLifetime.Token)
                    : RunShortWaitAsync(
                        loadClient,
                        knownChangeVersion,
                        pressureLifetime.Token))
                .ToArray();
            operations.AddRange(batch);

            if (waitForServerDrainBetweenBatches)
            {
                _ = await DrainPressureOperationsAsync(batch, scenario);
                var nextSessionId = currentSessionId == session.SessionId
                    ? drainSessionId!.Value
                    : session.SessionId;
                var changed = await loadClient.SetCurrentSessionAsync(
                    nextSessionId,
                    pressureLifetime.Token);
                knownChangeVersion = changed.ChangeVersion;
                currentSessionId = changed.SessionId;
                await AwaitServerRoundTripClosureAsync(
                    environment.Options.PipeName,
                    scenario,
                    pressureLifetime.Token);
            }
        }

        if (currentSessionId != session.SessionId)
        {
            var restored = await loadClient.SetCurrentSessionAsync(
                session.SessionId,
                pressureLifetime.Token);
            knownChangeVersion = restored.ChangeVersion;
            currentSessionId = restored.SessionId;
            await AwaitServerRoundTripClosureAsync(
                environment.Options.PipeName,
                scenario,
                pressureLifetime.Token);
        }

        var allOperations = Task.WhenAll(operations);
        var remainingObservationTime = TimeSpan.FromSeconds(15) - pressure.Elapsed;
        if (remainingObservationTime > TimeSpan.Zero && !allOperations.IsCompleted)
        {
            _ = await Task.WhenAny(allOperations, Task.Delay(remainingObservationTime));
        }

        var completedByFifteenSeconds = operations.Count(operation => operation.IsCompleted);
        var pressureElapsedAtObservation = pressure.Elapsed;
        IDesktopApiClient independentClient = new DesktopApiClient(
            environment.Options.PipeName,
            TimeSpan.FromSeconds(3));
        var ping = await MeasurePingAsync(independentClient, TimeSpan.FromSeconds(5));
        var submit = await MeasureSubmitAsync(
            independentClient,
            session.SessionId,
            $"{scenario}-recovery",
            TimeSpan.FromSeconds(5));

        pressureLifetime.Cancel();
        var outcomes = await DrainPressureOperationsAsync(operations, scenario);
        await host.StopAsync();
        host.Dispose();
        AssertDatabaseReleased(environment.Options.DatabasePath);

        output.WriteLine(
            $"{scenario}: 15秒时完成 {completedByFifteenSeconds}/{operationCount}; " +
            $"观察耗时 {pressureElapsedAtObservation.TotalMilliseconds:F0} ms; " +
            $"Ping={ping.Succeeded} ({ping.Elapsed.TotalMilliseconds:F0} ms); " +
            $"Submit={submit.Succeeded} ({submit.Elapsed.TotalMilliseconds:F0} ms); " +
            $"结果={string.Join(',', outcomes.GroupBy(item => item.Kind).Select(group => $"{group.Key}:{group.Count()}"))}");

        Assert.Equal(operationCount, operations.Count);
        Assert.Equal(operationCount, outcomes.Length);
        if (requireEveryOperationByFifteenSeconds)
        {
            Assert.Equal(operationCount, completedByFifteenSeconds);
            Assert.Equal(operationCount / 2, outcomes.Count(item => item.Kind == "Cancelled"));
            Assert.Equal(operationCount / 2, outcomes.Count(item => item.Kind == "ShortCompleted"));
            Assert.DoesNotContain(
                outcomes,
                item => item.Kind is "BusyRejected" or "TransportRejected" or "PressureCancelled");
        }
        else
        {
            Assert.All(outcomes, item => Assert.Contains(
                item.Kind,
                new[]
                {
                    "Cancelled",
                    "PressureCancelled",
                    "ShortCompleted",
                    "BusyRejected",
                    "TransportRejected"
                }));
        }

        var unexpectedOutcomes = outcomes
            .Where(item => item.Kind is "UnexpectedCompletion" or "UnexpectedNull" or "ApiFailure" or "Fault")
            .ToArray();
        Assert.True(
            unexpectedOutcomes.Length == 0,
            $"{scenario} 出现未知、协议或 SQLite 失败：" +
            string.Join(',', unexpectedOutcomes.Select(item => $"{item.Kind}:{item.Detail}")));

        Assert.True(
            ping.Succeeded,
            $"{scenario} 后独立客户端 Ping 在5秒内未恢复。");
        Assert.True(
            submit.Succeeded,
            $"{scenario} 后独立客户端 Submit 在5秒内未恢复。");
        Assert.Equal(1, provider.SendCount);
    }

    private static async Task RunFiveWayRefreshAsync(IDesktopApiClient client)
    {
        var dashboard = client.GetDashboardAsync();
        var projects = client.ListProjectsAsync();
        var tasks = client.ListTasksAsync();
        var applications = client.ListDesktopApplicationsAsync();
        var session = client.GetCurrentSessionAsync();
        await Task.WhenAll(dashboard, projects, tasks, applications, session);
    }

    private static async Task<WaitPressureOutcome[]> DrainPressureOperationsAsync(
        IReadOnlyCollection<Task<WaitPressureOutcome>> operations,
        string scenario)
    {
        var allOperations = Task.WhenAll(operations);
        try
        {
            return await allOperations.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException exception)
        {
            var unfinishedCount = operations.Count(operation => !operation.IsCompleted);
            throw new Xunit.Sdk.XunitException(
                $"{scenario} 取消后仍有 {unfinishedCount}/{operations.Count} 个压力请求未进入终态。",
                exception);
        }
    }

    private static void AssertDatabaseReleased(string databasePath)
    {
        using var exclusive = File.Open(
            databasePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    private static async Task AwaitServerRoundTripClosureAsync(
        string pipeName,
        string scenario,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);
        var requestId = Guid.NewGuid().ToString("N");
        await DesktopIpcFraming.WriteAsync(
            pipe,
            new DesktopApiRequest(
                requestId,
                DesktopApiMethods.Ping,
                DesktopProtocolJson.ToElement(new EmptyRequest())),
            timeout.Token);
        var response = await DesktopIpcFraming.ReadAsync<DesktopApiResponse>(
            pipe,
            timeout.Token);
        if (!response.Success)
        {
            throw new Xunit.Sdk.XunitException(
                $"{scenario} 的批次排空屏障被拒绝：{response.Error?.Code ?? "unknown"}。");
        }

        var endOfStreamProbe = new byte[1];
        var bytesRead = await pipe.ReadAsync(endOfStreamProbe, timeout.Token);
        if (bytesRead != 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"{scenario} 的批次排空屏障在响应后仍收到额外协议数据。");
        }
    }

    private static async Task<WaitPressureOutcome> RunCancelledWaitAsync(
        string pipeName,
        long knownVersion,
        CancellationToken pressureCancellation)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(3_000, pressureCancellation);
            var requestId = Guid.NewGuid().ToString("N");
            await DesktopIpcFraming.WriteAsync(
                pipe,
                new DesktopApiRequest(
                    requestId,
                    DesktopApiMethods.WaitForSessionUpdate,
                    DesktopProtocolJson.ToElement(
                        new WaitForSessionUpdateRequestDto(
                            knownVersion,
                            WaitMilliseconds: 1_000))),
                pressureCancellation);

            using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                pressureCancellation);
            var responseTask = DesktopIpcFraming.ReadAsync<DesktopApiResponse>(
                pipe,
                readCancellation.Token);
            readCancellation.Cancel();
            try
            {
                var response = await responseTask;
                if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
                {
                    return new WaitPressureOutcome("Fault", "MismatchedRequestId");
                }

                if (response.Success)
                {
                    return new WaitPressureOutcome("UnexpectedCompletion");
                }

                return response.Error?.Code == "ipc_busy"
                    ? new WaitPressureOutcome("BusyRejected")
                    : new WaitPressureOutcome(
                        "ApiFailure",
                        response.Error?.Code ?? "unknown");
            }
            catch (OperationCanceledException exception)
                when (readCancellation.IsCancellationRequested)
            {
                if (exception.CancellationToken != readCancellation.Token)
                {
                    return new WaitPressureOutcome("Fault", "UnexpectedCancellationToken");
                }

                return new WaitPressureOutcome(
                    pressureCancellation.IsCancellationRequested
                        ? "PressureCancelled"
                        : "Cancelled");
            }
        }
        catch (OperationCanceledException) when (pressureCancellation.IsCancellationRequested)
        {
            return new WaitPressureOutcome("PressureCancelled");
        }
        catch (IOException exception)
        {
            return new WaitPressureOutcome("TransportRejected", exception.GetType().Name);
        }
        catch (TimeoutException exception)
        {
            return new WaitPressureOutcome("TransportRejected", exception.GetType().Name);
        }
        catch (Exception exception)
        {
            return new WaitPressureOutcome("Fault", exception.GetType().Name);
        }
    }

    private static async Task<WaitPressureOutcome> RunShortWaitAsync(
        IDesktopApiClient client,
        long knownVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await client.WaitForSessionUpdateAsync(
                knownVersion,
                waitMilliseconds: 25,
                cancellationToken);
            return new WaitPressureOutcome(snapshot is null ? "UnexpectedNull" : "ShortCompleted");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WaitPressureOutcome("PressureCancelled");
        }
        catch (DesktopApiException exception) when (exception.Error.Code == "ipc_busy")
        {
            return new WaitPressureOutcome("BusyRejected");
        }
        catch (DesktopApiException exception)
        {
            return new WaitPressureOutcome("ApiFailure", exception.Error.Code);
        }
        catch (IOException exception)
        {
            return new WaitPressureOutcome("TransportRejected", exception.GetType().Name);
        }
        catch (TimeoutException exception)
        {
            return new WaitPressureOutcome("TransportRejected", exception.GetType().Name);
        }
        catch (Exception exception)
        {
            return new WaitPressureOutcome("Fault", exception.GetType().Name);
        }
    }

    private static async Task<int> RunLongPollerAsync(
        IDesktopApiClient client,
        long initialVersion,
        CancellationToken cancellationToken)
    {
        var knownVersion = initialVersion;
        var observedChanges = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = await client.WaitForSessionUpdateAsync(
                    knownVersion,
                    waitMilliseconds: 5_000,
                    cancellationToken);
                if (snapshot is not null && snapshot.ChangeVersion > knownVersion)
                {
                    observedChanges++;
                    knownVersion = snapshot.ChangeVersion;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return observedChanges;
    }

    private static async Task<PingMeasurement> MeasurePingAsync(
        IDesktopApiClient client,
        TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var succeeded = await client.PingAsync(cancellation.Token);
            stopwatch.Stop();
            return new PingMeasurement(succeeded, stopwatch.Elapsed);
        }
        catch
        {
            stopwatch.Stop();
            return new PingMeasurement(Succeeded: false, stopwatch.Elapsed);
        }
    }

    private static async Task<SubmitMeasurement> MeasureSubmitAsync(
        IDesktopApiClient client,
        Guid sessionId,
        string idempotencyKey,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var submitted = await client.SubmitSessionInputAsync(
                new SessionInputRequestDto(
                    "压力后继续对话",
                    "Text",
                    idempotencyKey,
                    sessionId),
                cancellation.Token);
            while (!cancellation.IsCancellationRequested)
            {
                var snapshot = await client.GetCurrentSessionAsync(cancellation.Token);
                if (snapshot?.Turns.SingleOrDefault(turn => turn.Id == submitted.TurnId)?.Phase
                    == "Completed")
                {
                    stopwatch.Stop();
                    return new SubmitMeasurement(Succeeded: true, stopwatch.Elapsed);
                }

                await Task.Delay(20, cancellation.Token);
            }
        }
        catch
        {
        }

        stopwatch.Stop();
        return new SubmitMeasurement(Succeeded: false, stopwatch.Elapsed);
    }

    private static async Task<DisconnectObservation> ObserveServerDisconnectAsync(
        NamedPipeClientStream pipe)
    {
        var buffer = new byte[1];
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var read = await pipe.ReadAsync(buffer);
            stopwatch.Stop();
            return new DisconnectObservation(read == 0, stopwatch.Elapsed);
        }
        catch (IOException)
        {
            stopwatch.Stop();
            return new DisconnectObservation(WasDisconnected: true, stopwatch.Elapsed);
        }
    }

    private static async Task<SessionSnapshotDto> WaitForTurnPhaseAsync(
        IDesktopApiClient client,
        Guid turnId,
        string expectedPhase,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await client.GetCurrentSessionAsync();
            if (snapshot?.Turns.SingleOrDefault(turn => turn.Id == turnId)?.Phase == expectedPhase)
            {
                return snapshot;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"统一会话请求没有进入 {expectedPhase}。");
    }

    private sealed class PressureConversationProvider : IConversationProvider
    {
        private readonly object _gate = new();
        private int _sendCount;

        public string ProviderId => "desktop-ipc-pressure-fake";

        public int SendCount => Volatile.Read(ref _sendCount);

        public List<ConversationProviderRequest> Requests { get; } = [];

        public async Task<ConversationProviderResult> SendAsync(
            ConversationProviderRequest request,
            Func<string, int, Task>? started = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _sendCount);
            lock (_gate)
            {
                Requests.Add(request);
            }

            if (started is not null)
            {
                await started("thread-pressure-fake", 9600 + call);
            }

            return new ConversationProviderResult(
                ConversationProviderOutcome.Succeeded,
                "thread-pressure-fake",
                $"Fake 回答 {call}",
                $"message-pressure-{call}",
                9600 + call);
        }

        public Task CancelAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record PingMeasurement(bool Succeeded, TimeSpan Elapsed);

    private sealed record WaitPressureOutcome(string Kind, string? Detail = null);

    private sealed record DisconnectObservation(bool WasDisconnected, TimeSpan Elapsed);

    private sealed record SubmitMeasurement(bool Succeeded, TimeSpan Elapsed);
}
