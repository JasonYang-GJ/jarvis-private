using Microsoft.Data.Sqlite;

namespace ScreenGuide.Persistence.Sqlite;

internal static class SqliteConnectionOpener
{
    internal static Task<SqliteConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken = default) =>
        OpenAsync(connectionString, afterOpen: null, cancellationToken);

    internal static async Task<SqliteConnection> OpenAsync(
        string connectionString,
        Action? afterOpen,
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            afterOpen?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception initializationException)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposalException)
            {
                throw new AggregateException(
                    "SQLite 连接初始化失败，释放未返回的连接时再次失败。",
                    initializationException,
                    disposalException);
            }

            throw;
        }
    }
}
