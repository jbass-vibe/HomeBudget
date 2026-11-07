namespace HomeBudget.Web.Data;

public sealed class DatabaseInitializer(SqliteConnectionFactory connectionFactory, IWebHostEnvironment env, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);

        string schemaPath = Path.Combine(env.ContentRootPath, "schema.sql");
        if (!File.Exists(schemaPath))
        {
            schemaPath = Path.Combine(AppContext.BaseDirectory, "schema.sql");
        }

        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException($"Could not locate schema.sql at {schemaPath}");
        }

        string sql = await File.ReadAllTextAsync(schemaPath, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogInformation("SQLite database initialized in WAL mode at {DbPath}", connectionFactory.DatabasePath);
    }
}
