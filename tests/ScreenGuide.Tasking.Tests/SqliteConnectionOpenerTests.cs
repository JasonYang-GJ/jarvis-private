using Microsoft.Data.Sqlite;
using ScreenGuide.Persistence.Sqlite;

namespace ScreenGuide.Tasking.Tests;

public sealed class SqliteConnectionOpenerTests
{
    [Fact]
    public async Task CancellationAfterNativeOpenReleasesDatabaseBeforeTheTaskCompletes()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"screen-guide-connection-opener-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(root, "tasking.db");
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqliteConnectionOpener.OpenAsync(
                CreateConnectionString(databasePath),
                cancellation.Cancel,
                cancellation.Token));

        using (File.Open(
                   databasePath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
        }

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task SuccessfulOpenTransfersDatabaseOwnershipToTheCaller()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"screen-guide-connection-opener-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(root, "tasking.db");
        Directory.CreateDirectory(root);
        var connection = await SqliteConnectionOpener.OpenAsync(
            CreateConnectionString(databasePath));

        try
        {
            Assert.Throws<IOException>(() => File.Open(
                databasePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None));
        }
        finally
        {
            await connection.DisposeAsync();
        }

        using (File.Open(
                   databasePath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
        }

        Directory.Delete(root, recursive: true);
    }

    private static string CreateConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
}
