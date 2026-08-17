using ScreenGuide.Core.Tasking;
using ScreenGuide.Persistence.Sqlite;
using AgentTaskStatus = ScreenGuide.Core.Tasking.TaskStatus;

namespace ScreenGuide.Tasking.Tests;

internal sealed class TaskStoreTestEnvironment : IAsyncDisposable
{
    private TaskStoreTestEnvironment(
        string rootDirectory,
        string databasePath,
        SqliteTaskStore store,
        DeviceRecord device,
        ProjectRecord project)
    {
        RootDirectory = rootDirectory;
        DatabasePath = databasePath;
        Store = store;
        Device = device;
        Project = project;
    }

    public string RootDirectory { get; }

    public string DatabasePath { get; }

    public SqliteTaskStore Store { get; }

    public DeviceRecord Device { get; }

    public ProjectRecord Project { get; }

    public static async Task<TaskStoreTestEnvironment> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-tasking-{Guid.NewGuid():N}");
        var projectRoot = Path.Combine(root, "project");
        Directory.CreateDirectory(projectRoot);
        var databasePath = Path.Combine(root, "state", "tasking.db");
        var store = new SqliteTaskStore(databasePath);
        await store.InitializeAsync();
        var now = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);
        var device = new DeviceRecord
        {
            Id = Guid.NewGuid(),
            DisplayName = "Test Windows Host",
            DeviceType = DeviceType.WindowsHost,
            TrustState = DeviceTrustState.Local,
            CreatedAtUtc = now,
            LastSeenAtUtc = now
        };
        await store.UpsertDeviceAsync(device);
        var project = new ProjectRecord
        {
            Id = Guid.NewGuid(),
            Name = "Test Project",
            RootPath = projectRoot,
            AuthorizationState = ProjectAuthorizationState.Authorized,
            AuthorizedByDeviceId = device.Id,
            AuthorizedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await store.SetProjectAuthorizationAsync(project);
        return new TaskStoreTestEnvironment(root, databasePath, store, device, project);
    }

    public CommandRecord NewCreateCommand(string idempotencyKey, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        SourceDeviceId = Device.Id,
        ProjectId = Project.Id,
        IdempotencyKey = idempotencyKey,
        CommandType = CommandType.CreateTask,
        PayloadJson = "{\"instruction\":\"test\"}",
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        Status = CommandStatus.Received
    };

    public AgentTask NewTask(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ProjectId = Project.Id,
        CreatedByDeviceId = Device.Id,
        Title = "Test task",
        Instruction = "Make a safe test change.",
        WorkingDirectoryRelativePath = ".",
        Executor = "codex",
        Status = AgentTaskStatus.Pending,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        Version = 0
    };

    public async Task<(AgentTask Task, CommandRecord Command)> CreateTaskAsync(
        string idempotencyKey = "create-task")
    {
        var command = NewCreateCommand(idempotencyKey);
        var registration = await Store.RegisterCommandAsync(command);
        Assert.True(registration.Accepted);
        var task = NewTask();
        await Store.CreateTaskAsync(task, command.Id);
        return (task, command);
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        if (Directory.Exists(RootDirectory))
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
    }
}
