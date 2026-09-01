using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using ScreenGuide.Core.Memories;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class MemoryIpcIntegrationTests
{
    [Fact]
    public async Task CurrentUserPipeProvidesExplicitMemoryCrudWithoutAiInvocationOrPlaintextPersistence()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, _) = await environment.SeedProjectsAsync();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IMemoryContentProtector, FakeMemoryContentProtector>());
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(
            environment.Options.PipeName,
            TimeSpan.FromSeconds(3));
        const string title = "ipc-fake-private-title";
        const string body = "ipc-fake-private-body";

        var created = await client.CreateMemoryAsync(new CreateMemoryRequestDto(
            "ProjectNote",
            "Project",
            project.Id,
            title,
            body,
            null));
        var listed = await client.ListMemoriesAsync();
        var fetched = await client.GetMemoryAsync(created.Id);
        var updated = await client.UpdateMemoryAsync(new UpdateMemoryRequestDto(
            created.Id,
            created.Version,
            "Decision",
            "Global",
            null,
            "修正标题",
            "修正正文",
            null));
        var disabled = await client.SetMemoryEnabledAsync(new SetMemoryEnabledRequestDto(
            created.Id,
            updated.Version,
            false));

        var unconfirmed = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.DeleteMemoryAsync(new DeleteMemoryRequestDto(
                created.Id,
                disabled.Version,
                Confirmed: false)));
        var deleted = await client.DeleteMemoryAsync(new DeleteMemoryRequestDto(
            created.Id,
            disabled.Version,
            Confirmed: true));

        Assert.Equal(12, DesktopProtocolVersion.Current);
        Assert.Equal(created.Id, Assert.Single(listed).Id);
        Assert.Equal(title, fetched.Title);
        Assert.Equal(body, fetched.Body);
        Assert.Equal("Disabled", disabled.Status);
        Assert.Equal("memory.invalid_request", unconfirmed.Error.Code);
        Assert.Equal("Deleted", deleted.Status);
        Assert.Null(deleted.Title);
        Assert.Null(deleted.Body);
        Assert.Empty(await client.ListMemoriesAsync());

        await host.StopAsync();

        await using (var connection = new SqliteConnection(
            $"Data Source={environment.Options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM ai_invocations;";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync() ?? -1L));
        }

        foreach (var file in Directory.EnumerateFiles(
                     environment.Options.DataDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.DoesNotContain(Encoding.UTF8.GetBytes(title), bytes);
            Assert.DoesNotContain(Encoding.UTF8.GetBytes(body), bytes);
        }
    }

    [Fact]
    public async Task ProjectMemoryFailsClosedForMissingProjectAndErrorsDoNotEchoContent()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IMemoryContentProtector, FakeMemoryContentProtector>());
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        const string privateBody = "must-not-echo-private-body";

        var failure = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.CreateMemoryAsync(new CreateMemoryRequestDto(
                "ProjectNote",
                "Project",
                Guid.NewGuid(),
                "标题",
                privateBody,
                null)));

        Assert.Equal("memory.invalid_request", failure.Error.Code);
        Assert.DoesNotContain(privateBody, failure.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(privateBody, failure.Error.TechnicalDetail ?? string.Empty, StringComparison.Ordinal);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("0", "Global", false)]
    [InlineData("UserFact", "0", false)]
    [InlineData("UserFact", "1", true)]
    [InlineData("userfact", "Global", false)]
    [InlineData(" UserFact", "Global", false)]
    [InlineData("UserFact", "global", false)]
    [InlineData("UserFact", "Global ", false)]
    [InlineData("Unknown", "Global", false)]
    [InlineData("UserFact", "Unknown", false)]
    [InlineData("", "Global", false)]
    [InlineData("UserFact", "", false)]
    public async Task MemoryCreateRejectsNonCanonicalCategoryAndScopeTokensWithoutMutationOrContentEcho(
        string category,
        string scope,
        bool useAuthorizedProject)
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, _) = await environment.SeedProjectsAsync();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IMemoryContentProtector, FakeMemoryContentProtector>());
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        const string privateTitle = "canonical-token-private-title";
        const string privateBody = "canonical-token-private-body";

        var exception = await Record.ExceptionAsync(() => client.CreateMemoryAsync(
            new CreateMemoryRequestDto(
                category,
                scope,
                useAuthorizedProject ? project.Id : null,
                privateTitle,
                privateBody,
                null)));
        var rows = await client.ListMemoriesAsync();

        Assert.Empty(rows);
        var failure = Assert.IsType<DesktopApiException>(exception);
        Assert.Equal("memory.invalid_request", failure.Error.Code);
        Assert.DoesNotContain(privateTitle, failure.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(privateBody, failure.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(privateTitle, failure.Error.TechnicalDetail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(privateBody, failure.Error.TechnicalDetail ?? string.Empty, StringComparison.Ordinal);
        await host.StopAsync();
    }

    [Fact]
    public async Task CurrentUserPipePreviewsDeterministicLocalMatchesWithoutMutationOrAiInvocation()
    {
        await using var environment = DesktopHostTestEnvironment.Create();
        var (_, project, _) = await environment.SeedProjectsAsync();
        using var host = environment.BuildHost(services =>
            services.AddSingleton<IMemoryContentProtector, FakeMemoryContentProtector>());
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(environment.Options.PipeName);
        var global = await client.CreateMemoryAsync(new CreateMemoryRequestDto(
            "UserPreference",
            "Global",
            null,
            "全局预算",
            "控制支出",
            null));
        var projectMemory = await client.CreateMemoryAsync(new CreateMemoryRequestDto(
            "ProjectNote",
            "Project",
            project.Id,
            "项目预算",
            "本月计划",
            null));
        const string query = "预算";
        var request = new MemoryPreviewRequestDto(query, project.Id);

        var preview = await client.PreviewMemoriesAsync(request);
        var afterGlobal = await client.GetMemoryAsync(global.Id);
        var afterProject = await client.GetMemoryAsync(projectMemory.Id);

        Assert.Equal(2, preview.CandidateCount);
        Assert.Equal(2, preview.SelectedCount);
        Assert.Equal(
            projectMemory.Id,
            Assert.Single(preview.Items, item => item.Item.ProjectId == project.Id).Item.Id);
        Assert.Contains("project_scope", preview.Items[0].Explanations);
        Assert.Equal(global.Version, afterGlobal.Version);
        Assert.Equal(global.UpdatedAtUtc, afterGlobal.UpdatedAtUtc);
        Assert.Equal(projectMemory.Version, afterProject.Version);
        Assert.Equal(projectMemory.UpdatedAtUtc, afterProject.UpdatedAtUtc);
        Assert.DoesNotContain(query, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("全局预算", preview.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("项目预算", preview.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("项目预算", preview.Items[0].ToString(), StringComparison.Ordinal);
        Assert.Equal(12, DesktopProtocolVersion.Current);
        Assert.Equal("memory.preview", DesktopApiMethods.PreviewMemories);

        var invalid = await Assert.ThrowsAsync<DesktopApiException>(() =>
            client.PreviewMemoriesAsync(new MemoryPreviewRequestDto("\u0001private-query", project.Id)));
        Assert.Equal("memory.invalid_request", invalid.Error.Code);
        Assert.DoesNotContain("private-query", invalid.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-query", invalid.Error.TechnicalDetail ?? string.Empty, StringComparison.Ordinal);

        await host.StopAsync();
        await using var connection = new SqliteConnection(
            $"Data Source={environment.Options.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ai_invocations;";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync() ?? -1L));
    }

    private sealed class FakeMemoryContentProtector : IMemoryContentProtector
    {
        public byte[] Protect(string plaintext) =>
            Encoding.UTF8.GetBytes(string.Concat(plaintext.Reverse())).Select(value => (byte)(value ^ 0x5a)).ToArray();

        public string Unprotect(ReadOnlySpan<byte> protectedContent)
        {
            var bytes = protectedContent.ToArray();
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] ^= 0x5a;
            }

            return string.Concat(Encoding.UTF8.GetString(bytes).Reverse());
        }
    }
}
