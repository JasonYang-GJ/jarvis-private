using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Configuration;
using ScreenGuide.FakeCodexCli;
using ScreenGuide.Persistence.Sqlite;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.DesktopHost.Tests;

internal sealed class DesktopHostTestEnvironment : IAsyncDisposable
{
    private DesktopHostTestEnvironment(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        Options = new DesktopHostOptions(
            Path.Combine(rootDirectory, "host-data"),
            Path.ChangeExtension(typeof(FakeCodexMarker).Assembly.Location, ".exe"),
            $"ScreenGuide.Tests.{Guid.NewGuid():N}");
        TimeProvider = new TestTimeProvider(
            new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero));
    }

    public string RootDirectory { get; }

    public DesktopHostOptions Options { get; }

    public TestTimeProvider TimeProvider { get; }

    public static DesktopHostTestEnvironment Create()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new DesktopHostTestEnvironment(root);
    }

    public IHost BuildHost(Action<IServiceCollection>? configureServices = null) =>
        DesktopHostFactory.Build(
            Array.Empty<string>(),
            Options,
            services =>
            {
                services.AddSingleton<TimeProvider>(TimeProvider);
                configureServices?.Invoke(services);
            });

    public async Task<(DeviceRecord Device, ProjectRecord Authorized, ProjectRecord Revoked)>
        SeedProjectsAsync()
    {
        await using var store = await OpenStoreAsync();
        var now = TimeProvider.GetUtcNow();
        var device = new DeviceRecord
        {
            Id = Guid.NewGuid(),
            DisplayName = "Seed Windows Host",
            DeviceType = DeviceType.WindowsHost,
            TrustState = DeviceTrustState.Local,
            CreatedAtUtc = now,
            LastSeenAtUtc = now
        };
        await store.UpsertDeviceAsync(device);
        var authorizedRoot = Path.Combine(RootDirectory, "authorized-project");
        var revokedRoot = Path.Combine(RootDirectory, "revoked-project");
        Directory.CreateDirectory(authorizedRoot);
        Directory.CreateDirectory(revokedRoot);
        await RunGitAsync(authorizedRoot, "init", "--quiet");
        await RunGitAsync(revokedRoot, "init", "--quiet");
        var authorized = new ProjectRecord
        {
            Id = Guid.NewGuid(),
            Name = "Authorized Project",
            RootPath = authorizedRoot,
            AuthorizationState = ProjectAuthorizationState.Authorized,
            AuthorizedByDeviceId = device.Id,
            AuthorizedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var revoked = new ProjectRecord
        {
            Id = Guid.NewGuid(),
            Name = "Revoked Project",
            RootPath = revokedRoot,
            AuthorizationState = ProjectAuthorizationState.Revoked,
            AuthorizedByDeviceId = device.Id,
            AuthorizedAtUtc = now,
            RevokedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await store.SetProjectAuthorizationAsync(authorized);
        await store.SetProjectAuthorizationAsync(revoked);
        return (device, authorized, revoked);
    }

    public async Task<Guid> SeedRunningTaskAsync()
    {
        var (device, project, _) = await SeedProjectsAsync();
        await using var store = await OpenStoreAsync();
        var now = TimeProvider.GetUtcNow();
        var command = new CommandRecord
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = device.Id,
            ProjectId = project.Id,
            IdempotencyKey = $"seed-{Guid.NewGuid():N}",
            CommandType = CommandType.CreateTask,
            PayloadJson = "{\"instruction\":\"seed\"}",
            ReceivedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(5),
            Status = CommandStatus.Received
        };
        await store.RegisterCommandAsync(command);
        var task = new AgentTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            CreatedByDeviceId = device.Id,
            Title = "Seed running task",
            Instruction = "Remain active for recovery test.",
            WorkingDirectoryRelativePath = ".",
            Executor = "codex",
            Status = AgentTaskStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 0
        };
        await store.CreateTaskAsync(task, command.Id);
        await store.TransitionTaskAsync(
            task.Id,
            AgentTaskStatus.Running,
            TaskEventSource.System,
            "Seed task started.");
        return task.Id;
    }

    public async Task<(DeviceRecord Device, ProjectRecord Project, AgentTask Task)> SeedTaskAsync(
        string instruction,
        bool authorized = true)
    {
        var (device, project, _) = await SeedProjectsAsync();
        await using var store = await OpenStoreAsync();
        var now = TimeProvider.GetUtcNow();
        var command = new CommandRecord
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = device.Id,
            ProjectId = project.Id,
            IdempotencyKey = $"agent-task-{Guid.NewGuid():N}",
            CommandType = CommandType.CreateTask,
            PayloadJson = "{\"instruction\":\"fake test\"}",
            ReceivedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(5),
            Status = CommandStatus.Received
        };
        await store.RegisterCommandAsync(command);
        var task = new AgentTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            CreatedByDeviceId = device.Id,
            Title = "Agent lifecycle test",
            Instruction = instruction,
            WorkingDirectoryRelativePath = ".",
            Executor = "codex",
            Status = AgentTaskStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 0
        };

        await store.CreateTaskAsync(task, command.Id);
        if (!authorized)
        {
            project = project with
            {
                AuthorizationState = ProjectAuthorizationState.Revoked,
                RevokedAtUtc = now,
                UpdatedAtUtc = now
            };
            await store.SetProjectAuthorizationAsync(project);
        }

        return (device, project, task);
    }

    public async Task<CommandRecord> RegisterTaskCommandAsync(
        Guid taskId,
        CommandType commandType,
        string payloadJson = "{}")
    {
        await using var store = await OpenStoreAsync();
        var task = await store.GetTaskAsync(taskId)
            ?? throw new InvalidOperationException("测试任务不存在。");
        var now = TimeProvider.GetUtcNow();
        var command = new CommandRecord
        {
            Id = Guid.NewGuid(),
            SourceDeviceId = task.CreatedByDeviceId,
            ProjectId = task.ProjectId,
            TaskId = task.Id,
            IdempotencyKey = $"test-{commandType}-{Guid.NewGuid():N}",
            CommandType = commandType,
            PayloadJson = payloadJson,
            ReceivedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(10),
            Status = CommandStatus.Received
        };
        var result = await store.RegisterCommandAsync(command);
        Assert.True(result.Accepted);
        return result.Command;
    }

    public async Task<SqliteTaskStore> OpenStoreAsync()
    {
        var store = new SqliteTaskStore(Options.DatabasePath);
        await store.InitializeAsync();
        return store;
    }

    public static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start());
        await process.WaitForExitAsync();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {await process.StandardError.ReadToEndAsync()}");
    }

    public string CopyFakeCliToUnverifiedVersionDirectory()
    {
        var sourceDirectory = Path.GetDirectoryName(typeof(FakeCodexMarker).Assembly.Location)!;
        var destination = Path.Combine(RootDirectory, "unverified-version");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        return Path.Combine(
            destination,
            Path.GetFileName(Path.ChangeExtension(typeof(FakeCodexMarker).Assembly.Location, ".exe")));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(RootDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(RootDirectory, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(RootDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
