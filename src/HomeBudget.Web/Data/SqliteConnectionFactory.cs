using Dapper;
using Microsoft.Data.Sqlite;

namespace HomeBudget.Web.Data;

public sealed class SqliteConnectionFactory
{
    public string DatabasePath { get; }

    public SqliteConnectionFactory(IConfiguration configuration)
    {
        DatabasePath = configuration["HomeBudget:DatabasePath"]
            ?? @"C:\ProgramData\HomeBudget\budget.db";

        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public async Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        var conn = new SqliteConnection(connStr);
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync(
            "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");

        return conn;
    }
}
