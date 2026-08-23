using ScreenGuide.Core.Conversations;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Core.Tasking;
using ScreenGuide.Persistence.Sqlite;

namespace ScreenGuide.Tasking.Tests;

public sealed class SqliteSessionStoreTests
{
    [Fact]
    public async Task CurrentSessionAndProjectContextSurviveAStoreRestart()
    {
        await using var environment = await SessionStoreEnvironment.CreateAsync();
        var session = await environment.CreateSessionAsync("统一会话");
        await environment.Store.SetSelectedProjectAsync(session.Id, environment.Project.Id, environment.Now);

        await using var reopened = new SqliteSessionStore(environment.DatabasePath);
        await reopened.InitializeAsync();
        var current = await reopened.GetCurrentSessionAsync();

        Assert.Equal(session.Id, current?.Id);
        Assert.Equal(session.ConversationId, current?.ConversationId);
        Assert.Equal(environment.Project.Id, current?.SelectedProjectId);
        Assert.True(current?.IsCurrent);
    }

    [Fact]
    public async Task WaitingTurnKeepsTheOriginalRequestAndCanResumeAfterRestart()
    {
        await using var environment = await SessionStoreEnvironment.CreateAsync();
        var session = await environment.CreateSessionAsync("等待上下文");
        var registration = await environment.Store.StartTurnAsync(
            session.Id,
            "帮我修改一下这个项目",
            "Voice",
            "stage1-waiting-project",
            environment.Now);
        var turn = registration.Turn;
        var waiting = await environment.Store.UpdateTurnAsync(
            turn with
            {
                WorkKind = SessionWorkKind.CodingTask,
                IntentKind = "CodingTask",
                ExpectedIntentKind = "DesktopAction",
                ExpectedTarget = "app:notepad",
                PlanTarget = "notepad.exe",
                Phase = SessionTurnPhase.WaitingForProject,
                MissingContext = SessionMissingContext.Project
            },
            turn.Version,
            environment.Now.AddSeconds(1));

        await using var reopened = new SqliteSessionStore(environment.DatabasePath);
        await reopened.InitializeAsync();
        var stored = await reopened.GetTurnAsync(waiting.Id);

        Assert.Equal("帮我修改一下这个项目", stored?.InputText);
        Assert.Equal(SessionTurnPhase.WaitingForProject, stored?.Phase);
        Assert.Equal(SessionMissingContext.Project, stored?.MissingContext);
        Assert.Equal("DesktopAction", stored?.ExpectedIntentKind);
        Assert.Equal("app:notepad", stored?.ExpectedTarget);
        Assert.Equal("notepad.exe", stored?.PlanTarget);
        Assert.Equal(waiting.Version, stored?.Version);
    }

    private sealed class SessionStoreEnvironment : IAsyncDisposable
    {
        private SessionStoreEnvironment(
            string root,
            string databasePath,
            SqliteSessionStore store,
            SqliteConversationStore conversations,
            DeviceRecord device,
            ProjectRecord project)
        {
            Root = root;
            DatabasePath = databasePath;
            Store = store;
            Conversations = conversations;
            Device = device;
            Project = project;
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public SqliteSessionStore Store { get; }

        public SqliteConversationStore Conversations { get; }

        public DeviceRecord Device { get; }

        public ProjectRecord Project { get; }

        public DateTimeOffset Now { get; } = new(2026, 8, 24, 1, 0, 0, TimeSpan.Zero);

        public static async Task<SessionStoreEnvironment> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"screen-guide-session-{Guid.NewGuid():N}");
            var projectRoot = Path.Combine(root, "project");
            var databasePath = Path.Combine(root, "state", "tasking.db");
            Directory.CreateDirectory(projectRoot);
            await using var taskStore = new SqliteTaskStore(databasePath);
            await taskStore.InitializeAsync();
            var now = new DateTimeOffset(2026, 8, 24, 1, 0, 0, TimeSpan.Zero);
            var device = new DeviceRecord
            {
                Id = Guid.NewGuid(),
                DisplayName = "Session Test Host",
                DeviceType = DeviceType.WindowsHost,
                TrustState = DeviceTrustState.Local,
                CreatedAtUtc = now,
                LastSeenAtUtc = now
            };
            await taskStore.UpsertDeviceAsync(device);
            var project = new ProjectRecord
            {
                Id = Guid.NewGuid(),
                Name = "Session Project",
                RootPath = projectRoot,
                AuthorizationState = ProjectAuthorizationState.Authorized,
                AuthorizedByDeviceId = device.Id,
                AuthorizedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            await taskStore.SetProjectAuthorizationAsync(project);
            var conversations = new SqliteConversationStore(databasePath);
            await conversations.InitializeAsync();
            var store = new SqliteSessionStore(databasePath);
            await store.InitializeAsync();
            return new SessionStoreEnvironment(root, databasePath, store, conversations, device, project);
        }

        public async Task<SessionRecord> CreateSessionAsync(string title)
        {
            var conversation = new ConversationRecord
            {
                Id = Guid.NewGuid(),
                CreatedByDeviceId = Device.Id,
                Title = title,
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now
            };
            await Conversations.CreateConversationAsync(conversation);
            var session = new SessionRecord
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                CreatedByDeviceId = Device.Id,
                Title = title,
                IsCurrent = true,
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
                LastActiveAtUtc = Now
            };
            await Store.CreateSessionAsync(session);
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
