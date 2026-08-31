using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

const string InvalidArguments = "s5_lifecycle_probe_invalid_arguments";
const string DatabaseMissing = "s5_lifecycle_probe_database_missing";
const string SchemaMismatch = "s5_lifecycle_probe_schema_mismatch";
const string IntegrityFailure = "s5_lifecycle_probe_integrity_failure";
const string UnexpectedFailure = "s5_lifecycle_probe_failed";

var arguments = ParseArguments(args);
if (!arguments.TryGetValue("--database", out var databasePath) ||
    !arguments.TryGetValue("--expected-schema", out var expectedSchemaText) ||
    !arguments.TryGetValue("--output", out var outputPath) ||
    !int.TryParse(expectedSchemaText, out var expectedSchema))
{
    return WriteResult(outputPath: null, passed: false, InvalidArguments, 0, false, null);
}

try
{
    databasePath = Path.GetFullPath(databasePath);
    outputPath = Path.GetFullPath(outputPath);
    if (arguments.TryGetValue("--fixture-schema", out var fixtureSchemaText))
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SCREEN_GUIDE_STAGE5_PROBE_TEST_MODE"), "1", StringComparison.Ordinal) ||
            !int.TryParse(fixtureSchemaText, out var fixtureSchema) ||
            !databasePath.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(databasePath).StartsWith("YuanshuStage5ProbeFixture-", StringComparison.Ordinal))
        {
            return WriteResult(outputPath, false, InvalidArguments, 0, false, null);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var fixtureBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        await using var fixtureConnection = new SqliteConnection(fixtureBuilder.ToString());
        await fixtureConnection.OpenAsync();
        await using var fixtureCommand = fixtureConnection.CreateCommand();
        fixtureCommand.CommandText = "CREATE TABLE schema_info(version INTEGER NOT NULL PRIMARY KEY, applied_at_utc TEXT NOT NULL); INSERT INTO schema_info(version, applied_at_utc) VALUES ($version, 'fixture');";
        fixtureCommand.Parameters.AddWithValue("$version", fixtureSchema);
        await fixtureCommand.ExecuteNonQueryAsync();
    }
    if (!File.Exists(databasePath))
    {
        return WriteResult(outputPath, false, DatabaseMissing, 0, false, null);
    }

    var builder = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Private,
        Pooling = false
    };
    await using var connection = new SqliteConnection(builder.ToString());
    await connection.OpenAsync();
    await using var schemaCommand = connection.CreateCommand();
    schemaCommand.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_info;";
    var schemaVersion = Convert.ToInt32(await schemaCommand.ExecuteScalarAsync());
    await using var integrityCommand = connection.CreateCommand();
    integrityCommand.CommandText = "PRAGMA integrity_check;";
    var integrityOk = string.Equals(
        Convert.ToString(await integrityCommand.ExecuteScalarAsync()),
        "ok",
        StringComparison.Ordinal);
    await using var databaseStream = File.OpenRead(databasePath);
    var hash = Convert.ToHexString(await SHA256.HashDataAsync(databaseStream));

    if (!integrityOk)
    {
        return WriteResult(outputPath, false, IntegrityFailure, schemaVersion, false, hash);
    }
    if (schemaVersion != expectedSchema)
    {
        return WriteResult(outputPath, false, SchemaMismatch, schemaVersion, true, hash);
    }
    return WriteResult(outputPath, true, null, schemaVersion, true, hash);
}
catch
{
    return WriteResult(outputPath, false, UnexpectedFailure, 0, false, null);
}

static Dictionary<string, string> ParseArguments(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index + 1 < values.Length; index += 2)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal) ||
            !result.TryAdd(values[index], values[index + 1]))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
    return result;
}

static int WriteResult(
    string? outputPath,
    bool passed,
    string? errorCode,
    int schemaVersion,
    bool integrityOk,
    string? databaseSha256)
{
    var result = new
    {
        contractVersion = 1,
        passed,
        errorCode,
        schemaVersion,
        integrityOk,
        databaseSha256
    };
    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, json, new System.Text.UTF8Encoding(false));
    }
    Console.Write(json);
    return passed ? 0 : 1;
}
