using Microsoft.Extensions.DependencyInjection;
using System.IO.Pipes;
using System.Security.Principal;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopIpcIntegrationTests
{
    [Fact]
    public async Task CurrentUserPipeExposesTaskLifecycleWithoutClientStoreAccess()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, _) = await environment.SeedProjectsAsync();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(
            environment.Options.PipeName,
            TimeSpan.FromSeconds(3));

        Assert.True(await client.PingAsync());
        var status = await client.GetSystemStatusAsync();
        var projects = await client.ListProjectsAsync();
        Assert.True(status.HostOnline);
        Assert.Equal(DesktopProtocolVersion.Current, status.ProtocolVersion);
        Assert.Equal(V02Contract.SchemaVersion, status.DatabaseSchemaVersion);
        Assert.Contains(projects, item => item.Id == project.Id);

        var created = await client.CreateTaskAsync(new CreateTaskRequestDto(
            project.Id,
            "TEST_EVIDENCE_NO_TEST",
            "IPC lifecycle"));
        await host.Services.GetRequiredService<AgentTaskExecutionService>()
            .WaitForTaskAsync(created.TaskId);
        var details = await client.GetTaskAsync(created.TaskId);

        Assert.Equal("Succeeded", details?.Summary.Status);
        Assert.Equal("Unverified", details?.Evidence?.VerificationStatus);
        Assert.NotEmpty(details?.Events ?? []);
        Assert.Equal("Verifying", details?.Summary.Phase);
        var scope = Assert.Single(details?.ResourceScopes ?? []);
        Assert.Equal("Project", scope.ScopeType);
        Assert.Equal("Execute", scope.AccessMode);
        var invocation = Assert.Single(details?.SkillInvocations ?? []);
        Assert.Equal("codex.project-task", invocation.SkillId);
        Assert.Equal("Succeeded", invocation.Status);
        await host.StopAsync();
    }

    [Fact]
    public async Task RejectsUnsupportedProtocolVersionWithStructuredError()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        await using var pipe = new NamedPipeClientStream(
            ".",
            environment.Options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(3000);
        var requestId = Guid.NewGuid().ToString("N");

        await DesktopIpcFraming.WriteAsync(
            pipe,
            new DesktopApiRequest(
                requestId,
                DesktopApiMethods.Ping,
                DesktopProtocolJson.ToElement(new EmptyRequest()),
                ProtocolVersion: 99));
        var response = await DesktopIpcFraming.ReadAsync<DesktopApiResponse>(pipe);
        await host.StopAsync();

        Assert.Equal(requestId, response.RequestId);
        Assert.False(response.Success);
        Assert.Equal("protocol_version_unsupported", response.Error?.Code);
        Assert.Equal(DesktopProtocolVersion.Current, response.ProtocolVersion);
    }

    [Fact]
    public async Task UserSelectedGitProjectCanBeAddedAndRevokedOverIpc()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var root = Path.Combine(environment.RootDirectory, "user-selected-project");
        Directory.CreateDirectory(root);
        await DesktopHostTestEnvironment.RunGitAsync(root, "init", "--quiet");

        var added = await client.AddProjectAsync(new AddProjectRequestDto(root, "Selected Project"));
        var listed = await client.ListProjectsAsync();
        var revoked = await client.RevokeProjectAsync(added.Id);
        await host.StopAsync();

        Assert.Equal("Authorized", added.AuthorizationState);
        Assert.True(added.IsGitRepository);
        Assert.Contains(listed, project => project.Id == added.Id);
        Assert.Equal("Revoked", revoked.AuthorizationState);
    }

    [Fact]
    public async Task NonGitDirectoryIsRejectedWithUserFacingError()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var root = Path.Combine(environment.RootDirectory, "not-git");
        Directory.CreateDirectory(root);

        var exception = await Assert.ThrowsAsync<DesktopApiException>(
            () => client.AddProjectAsync(new AddProjectRequestDto(root)));
        await host.StopAsync();

        Assert.Equal("project_not_git", exception.Error.Code);
        Assert.Contains("Git", exception.Error.UserMessage);
    }

    [Fact]
    public async Task IpcTechnicalDetailDoesNotEchoSensitiveInputOrRawFailureMessage()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var missing = Path.Combine(
            environment.RootDirectory,
            DesktopStabilityTests.CanaryApiKey,
            "missing-project");

        var exception = await Assert.ThrowsAsync<DesktopApiException>(
            () => client.AddProjectAsync(new AddProjectRequestDto(missing)));
        await host.StopAsync();

        Assert.Equal("project_missing", exception.Error.Code);
        Assert.Equal("DirectoryNotFoundException", exception.Error.TechnicalDetail);
        Assert.DoesNotContain(
            DesktopStabilityTests.CanaryApiKey,
            exception.Error.TechnicalDetail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearHistoryDeletesTerminalTasksButNotProjects()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, _) = await environment.SeedProjectsAsync();
        using var host = environment.BuildHost();
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var created = await client.CreateTaskAsync(new CreateTaskRequestDto(
            project.Id,
            "TEST_EVIDENCE_NO_TEST"));
        await host.Services.GetRequiredService<AgentTaskExecutionService>()
            .WaitForTaskAsync(created.TaskId);

        var count = await client.ClearHistoryAsync();
        var tasks = await client.ListTasksAsync();
        var projects = await client.ListProjectsAsync();
        await host.StopAsync();

        Assert.Equal(1, count);
        Assert.Empty(tasks);
        Assert.Contains(projects, item => item.Id == project.Id);
    }

    [Fact]
    public async Task OnlyOneHostLeaseCanBeOwnedByDifferentThreads()
    {
        var scope = $"Tests.{Guid.NewGuid():N}";
        var secondWasRejected = await Task.Run(() =>
        {
            using var first = HostSingleInstanceLease.TryAcquire(scope);
            Assert.NotNull(first);
            HostSingleInstanceLease? second = null;
            var thread = new Thread(() => second = HostSingleInstanceLease.TryAcquire(scope));
            thread.Start();
            thread.Join();
            second?.Dispose();
            return second is null;
        });

        Assert.True(secondWasRejected);
    }

    [Fact]
    public async Task HostLeaseCanBeReacquiredAfterAsyncOwnerDisposes()
    {
        var scope = $"Tests.{Guid.NewGuid():N}";
        await Task.Run(async () =>
        {
            using (var first = HostSingleInstanceLease.TryAcquire(scope))
            {
                Assert.NotNull(first);
                await Task.Yield();
            }

            using var next = HostSingleInstanceLease.TryAcquire(scope);
            Assert.NotNull(next);
        });
    }
}
