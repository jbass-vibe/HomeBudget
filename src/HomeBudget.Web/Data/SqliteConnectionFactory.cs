using Microsoft.Data.Sqlite;

namespace HomeBudget.Web.Data;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;
    public string DatabasePath { get; }

    public SqliteConnectionFactory(IConfiguration config)
    {
        DatabasePath = config["DatabasePath"] ?? @"C:\ProgramData\HomeBudget\budget.db";
        string? dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await cmd.ExecuteNonQueryAsync(ct);

        return conn;
    }
}
