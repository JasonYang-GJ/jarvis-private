using System.Text;
using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Memories;
using ScreenGuide.Core.Tasking;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Persistence.Sqlite;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class MemoryServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WindowsCurrentUserMemoryProtectorRoundTripsFakeTextAndFailsClosedOnCorruption()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string fakeText = "fake-memory-current-user-boundary";
        var protector = new WindowsDpapiMemoryContentProtector();

        var protectedContent = protector.Protect(fakeText);

        Assert.False(Encoding.UTF8.GetBytes(fakeText).SequenceEqual(protectedContent));
        Assert.Equal(fakeText, protector.Unprotect(protectedContent));
        protectedContent[^1] ^= 0xff;
        Assert.ThrowsAny<Exception>(() => protector.Unprotect(protectedContent));
    }

    [Fact]
    public async Task ExplicitCrudProtectsContentAndReturnsStableConflictAndConfirmedDeleteErrors()
    {
        await using var environment = await MemoryEnvironment.CreateAsync();
        var service = environment.Service;
        const string title = "fake-private-title";
        const string body = "fake-private-body";

        var created = await service.CreateAsync(MemoryDraft.Create(
            MemoryCategory.ProjectNote,
            MemoryScope.ForProject(environment.Project.Id),
            title,
            body,
            Now.AddDays(3),
            Now));

        Assert.Equal(title, created.Title);
        Assert.Equal(body, created.Body);
        Assert.Equal(MemorySourceKind.UserExplicit, created.Metadata.SourceKind);
        Assert.Equal(1, created.Metadata.Version);
        var databaseBytes = await File.ReadAllBytesAsync(environment.DatabasePath);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes(title), databaseBytes);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes(body), databaseBytes);

        var updated = await service.UpdateAsync(
            created.Metadata.Id,
            1,
            MemoryDraft.Create(
                MemoryCategory.Decision,
                MemoryScope.Global,
                "修正标题",
                "修正正文",
                null,
                Now),
            Now.AddMinutes(1));
        Assert.Equal(2, updated.Metadata.Version);
        Assert.Equal("修正正文", updated.Body);

        var conflict = await Assert.ThrowsAsync<MemoryServiceException>(() => service.UpdateAsync(
            created.Metadata.Id,
            1,
            MemoryDraft.Create(
                MemoryCategory.Decision,
                MemoryScope.Global,
                "过期修改",
                "不应写入",
                null,
                Now),
            Now.AddMinutes(2)));
        Assert.Equal(MemoryServiceErrorCodes.Conflict, conflict.Code);
        Assert.DoesNotContain("不应写入", conflict.Message, StringComparison.Ordinal);

        var disabled = await service.SetEnabledAsync(created.Metadata.Id, false, 2, Now.AddMinutes(2));
        Assert.Equal(MemoryStatus.Disabled, disabled.Metadata.Status);
        Assert.False(disabled.IsRetrievalCandidate(Now));

        var unconfirmed = await Assert.ThrowsAsync<MemoryServiceException>(() => service.DeleteAsync(
            created.Metadata.Id,
            3,
            confirmed: false,
            Now.AddMinutes(3)));
        Assert.Equal(MemoryServiceErrorCodes.InvalidRequest, unconfirmed.Code);

        var deleted = await service.DeleteAsync(
            created.Metadata.Id,
            3,
            confirmed: true,
            Now.AddMinutes(3));
        Assert.Equal(MemoryStatus.Deleted, deleted.Metadata.Status);
        Assert.Null(deleted.Title);
        Assert.Null(deleted.Body);
        Assert.Empty(await service.ListAsync());

        var idempotent = await service.DeleteAsync(
            created.Metadata.Id,
            4,
            confirmed: true,
            Now.AddMinutes(4));
        Assert.Equal(MemoryStatus.Deleted, idempotent.Metadata.Status);
        Assert.Equal(4, idempotent.Metadata.Version);
    }

    [Fact]
    public async Task ProjectScopeRejectsMissingOrRevokedProjectBeforeWritingProtectedContent()
    {
        await using var environment = await MemoryEnvironment.CreateAsync();
        var missingProjectId = Guid.NewGuid();

        var missing = await Assert.ThrowsAsync<MemoryServiceException>(() =>
            environment.Service.CreateAsync(MemoryDraft.Create(
                MemoryCategory.ProjectNote,
                MemoryScope.ForProject(missingProjectId),
                "标题",
                "正文",
                null,
                Now)));

        Assert.Equal(MemoryServiceErrorCodes.InvalidRequest, missing.Code);
        Assert.Empty(await environment.MemoryStore.ListAsync());

        var projectMemory = await environment.Service.CreateAsync(MemoryDraft.Create(
            MemoryCategory.ProjectNote,
            MemoryScope.ForProject(environment.Project.Id),
            "项目标题",
            "项目正文",
            null,
            Now));
        var disabled = await environment.Service.SetEnabledAsync(
            projectMemory.Metadata.Id,
            enabled: false,
            projectMemory.Metadata.Version,
            Now.AddMinutes(1));

        await environment.TaskStore.SetProjectAuthorizationAsync(environment.Project with
        {
            AuthorizationState = ProjectAuthorizationState.Revoked,
            RevokedAtUtc = Now,
            UpdatedAtUtc = Now
        });
        var revoked = await Assert.ThrowsAsync<MemoryServiceException>(() =>
            environment.Service.CreateAsync(MemoryDraft.Create(
                MemoryCategory.ProjectNote,
                MemoryScope.ForProject(environment.Project.Id),
                "标题",
                "正文",
                null,
                Now)));
        Assert.Equal(MemoryServiceErrorCodes.InvalidRequest, revoked.Code);
        var rejectedEnable = await Assert.ThrowsAsync<MemoryServiceException>(() =>
            environment.Service.SetEnabledAsync(
                projectMemory.Metadata.Id,
                enabled: true,
                disabled.Metadata.Version,
                Now.AddMinutes(1)));
        Assert.Equal(MemoryServiceErrorCodes.InvalidRequest, rejectedEnable.Code);
        Assert.Equal(
            MemoryStatus.Disabled,
            (await environment.MemoryStore.GetAsync(projectMemory.Metadata.Id))?.Metadata.Status);
    }

    [Fact]
    public async Task CorruptedProtectedContentFailsClosedWithoutLeakingContent()
    {
        await using var environment = await MemoryEnvironment.CreateAsync();
        var id = Guid.NewGuid();
        await environment.MemoryStore.CreateAsync(new ProtectedMemoryItem(
            MemoryMetadata.Create(
                id,
                MemoryCategory.UserFact,
                MemoryScope.Global,
                MemoryStatus.Active,
                MemorySourceKind.UserExplicit,
                Now,
                Now,
                null,
                1.0,
                1),
            [0, 1],
            [2, 3],
            null));

        var failure = await Assert.ThrowsAsync<MemoryServiceException>(
            () => environment.Service.GetAsync(id));

        Assert.Equal(MemoryServiceErrorCodes.ProtectionFailure, failure.Code);
        Assert.Equal("记忆内容无法安全读取。", failure.Message);

        var statusFailure = await Assert.ThrowsAsync<MemoryServiceException>(() =>
            environment.Service.SetEnabledAsync(id, enabled: false, expectedVersion: 1));
        Assert.Equal(MemoryServiceErrorCodes.ProtectionFailure, statusFailure.Code);
        var unchanged = await environment.MemoryStore.GetAsync(id);
        Assert.Equal(MemoryStatus.Active, unchanged?.Metadata.Status);
        Assert.Equal(1, unchanged?.Metadata.Version);
    }

    [Fact]
    public async Task PreviewUsesBoundedStoreCandidatesAndRejectsRevokedProjectBeforeDecrypting()
    {
        var protector = new CountingMemoryContentProtector();
        await using var environment = await MemoryEnvironment.CreateAsync(protector);
        _ = await environment.Service.CreateAsync(MemoryDraft.Create(
            MemoryCategory.UserPreference,
            MemoryScope.Global,
            "全局预算",
            "控制支出",
            null,
            Now));
        _ = await environment.Service.CreateAsync(MemoryDraft.Create(
            MemoryCategory.ProjectNote,
            MemoryScope.ForProject(environment.Project.Id),
            "项目预算",
            "本月计划",
            null,
            Now));

        protector.ResetUnprotectCount();
        var globalOnly = await environment.Service.PreviewAsync("预算", projectId: null);
        Assert.Equal(1, globalOnly.CandidateCount);
        Assert.Equal(1, globalOnly.SelectedCount);
        Assert.Equal(MemoryScopeKind.Global, Assert.Single(globalOnly.Matches).Item.Metadata.Scope.Kind);
        Assert.Equal(2, protector.UnprotectCount);

        protector.ResetUnprotectCount();
        var withProject = await environment.Service.PreviewAsync("预算", environment.Project.Id);
        Assert.Equal(2, withProject.CandidateCount);
        Assert.Equal(2, withProject.SelectedCount);
        Assert.Equal(environment.Project.Id, withProject.Matches[0].Item.Metadata.Scope.ProjectId);
        Assert.Contains("project_scope", withProject.Matches[0].Explanations);
        Assert.Equal(4, protector.UnprotectCount);

        protector.ResetUnprotectCount();
        var missing = await Assert.ThrowsAsync<MemoryServiceException>(() =>
            environment.Service.PreviewAsync("预算", Guid.NewGuid()));
        Assert.Equal(MemoryServiceErrorCodes.InvalidRequest, missing.Code);
        Assert.Equal(0, protector.UnprotectCount);

        await environment.TaskStore.SetProjectAuthorizationAsync(environment.Project with
        {
            AuthorizationState = ProjectAuthorizationState.Revoked,
            RevokedAtUtc = Now,
            UpdatedAtUtc = Now
        });
        protector.ResetUnprotectCount();
        var revoked = await Assert.ThrowsAsync<MemoryServiceException>(() =>
            environment.Service.PreviewAsync("预算", environment.Project.Id));
        Assert.Equal(MemoryServiceErrorCodes.InvalidRequest, revoked.Code);
        Assert.Equal(0, protector.UnprotectCount);
    }

    private sealed class MemoryEnvironment : IAsyncDisposable
    {
        private readonly string _root;

        private MemoryEnvironment(
            string root,
            string databasePath,
            SqliteTaskStore taskStore,
            SqliteMemoryStore memoryStore,
            ProjectRecord project,
            IMemoryContentProtector protector)
        {
            _root = root;
            DatabasePath = databasePath;
            TaskStore = taskStore;
            MemoryStore = memoryStore;
            Project = project;
            Service = new MemoryService(
                memoryStore,
                protector,
                taskStore,
                new FixedTimeProvider(Now));
        }

        public string DatabasePath { get; }

        public SqliteTaskStore TaskStore { get; }

        public SqliteMemoryStore MemoryStore { get; }

        public ProjectRecord Project { get; }

        public MemoryService Service { get; }

        public static async Task<MemoryEnvironment> CreateAsync(
            IMemoryContentProtector? protector = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"screen-guide-memory-host-{Guid.NewGuid():N}");
            var databasePath = Path.Combine(root, "state", "tasking.db");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            var taskStore = new SqliteTaskStore(databasePath);
            await taskStore.InitializeAsync();
            var device = new DeviceRecord
            {
                Id = Guid.NewGuid(),
                DisplayName = "Fake host",
                DeviceType = DeviceType.WindowsHost,
                TrustState = DeviceTrustState.Local,
                CreatedAtUtc = Now
            };
            await taskStore.UpsertDeviceAsync(device);
            var project = new ProjectRecord
            {
                Id = Guid.NewGuid(),
                Name = "Fake authorized project",
                RootPath = Path.Combine(root, "project"),
                AuthorizationState = ProjectAuthorizationState.Authorized,
                AuthorizedByDeviceId = device.Id,
                AuthorizedAtUtc = Now,
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now
            };
            Directory.CreateDirectory(project.RootPath);
            await taskStore.SetProjectAuthorizationAsync(project);
            var memoryStore = new SqliteMemoryStore(databasePath);
            await memoryStore.InitializeAsync();
            return new MemoryEnvironment(
                root,
                databasePath,
                taskStore,
                memoryStore,
                project,
                protector ?? new FakeMemoryContentProtector());
        }

        public async ValueTask DisposeAsync()
        {
            await MemoryStore.DisposeAsync();
            await TaskStore.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeMemoryContentProtector : IMemoryContentProtector
    {
        private static readonly byte[] Prefix = [0x4d, 0x45, 0x4d, 0x01];

        public byte[] Protect(string plaintext)
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = new byte[Prefix.Length + bytes.Length];
            Prefix.CopyTo(protectedBytes, 0);
            for (var index = 0; index < bytes.Length; index++)
            {
                protectedBytes[Prefix.Length + index] = (byte)(bytes[index] ^ 0xa5);
            }

            return protectedBytes;
        }

        public string Unprotect(ReadOnlySpan<byte> protectedContent)
        {
            if (protectedContent.Length <= Prefix.Length
                || !protectedContent[..Prefix.Length].SequenceEqual(Prefix))
            {
                throw new InvalidDataException("fake corruption");
            }

            var bytes = protectedContent[Prefix.Length..].ToArray();
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] ^= 0xa5;
            }

            return Encoding.UTF8.GetString(bytes);
        }
    }

    private sealed class CountingMemoryContentProtector : IMemoryContentProtector
    {
        private readonly FakeMemoryContentProtector _inner = new();

        public int UnprotectCount { get; private set; }

        public byte[] Protect(string plaintext) => _inner.Protect(plaintext);

        public string Unprotect(ReadOnlySpan<byte> protectedContent)
        {
            UnprotectCount++;
            return _inner.Unprotect(protectedContent);
        }

        public void ResetUnprotectCount() => UnprotectCount = 0;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
