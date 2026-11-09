using Dapper;

namespace HomeBudget.Web.Data;

public sealed class DatabaseInitializer(
    SqliteConnectionFactory connectionFactory,
    IWebHostEnvironment env,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);

        string[] candidatePaths =
        [
            Path.Combine(AppContext.BaseDirectory, "schema.sql"),
            Path.Combine(env.ContentRootPath, "schema.sql"),
            Path.Combine(env.ContentRootPath, "..", "..", "deploy", "schema.sql")
        ];

        string? schemaSql = null;
        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                schemaSql = await File.ReadAllTextAsync(path, ct);
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(schemaSql))
        {
            await conn.ExecuteAsync(schemaSql);
        }

        await ApplyIdempotentSchemaMigrationsAsync(conn);

        int ruleCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM CategorizationRules;");
        if (ruleCount == 0)
        {
            var catMap = (await conn.QueryAsync<(int CategoryId, string Name)>("SELECT CategoryId, Name FROM Categories;"))
                .ToDictionary(x => x.Name, x => x.CategoryId, StringComparer.OrdinalIgnoreCase);

            async Task AddDefaultRule(string pattern, string catName, int priority)
            {
                if (catMap.TryGetValue(catName, out int catId))
                {
                    await conn.ExecuteAsync(
                        "INSERT INTO CategorizationRules (MatchPattern, IsRegex, CategoryId, Priority) VALUES (@Pattern, 0, @CatId, @Priority);",
                        new { Pattern = pattern, CatId = catId, Priority = priority });
                }
            }

            await AddDefaultRule("COSTCO", "Costco", 10);
            await AddDefaultRule("AMZN", "Amazon", 15);
            await AddDefaultRule("AMAZON", "Amazon", 15);
            await AddDefaultRule("TARGET", "Target", 20);
            await AddDefaultRule("WAL-MART", "Walmart", 25);
            await AddDefaultRule("WALMART", "Walmart", 25);
            await AddDefaultRule("WHOLEFDS", "Groceries", 30);
            await AddDefaultRule("TRADER JOE", "Groceries", 30);
            await AddDefaultRule("SAFEWAY", "Groceries", 30);
            await AddDefaultRule("MORTGAGE", "Housing & Utilities", 40);
            await AddDefaultRule("PG&E", "Housing & Utilities", 40);
            await AddDefaultRule("CHEVRON", "Transportation & Gas", 50);
            await AddDefaultRule("SHELL", "Transportation & Gas", 50);
            await AddDefaultRule("NETFLIX", "Subscriptions", 60);
            await AddDefaultRule("COFFEE", "Dining & Coffee", 70);
        }

        logger.LogInformation("SQLite database initialized in WAL mode at {DatabasePath}", connectionFactory.DatabasePath);
    }

    public static async Task ApplyIdempotentSchemaMigrationsAsync(System.Data.Common.DbConnection conn)
    {
        // 1. Ensure StagingSessions exists for older databases
        await conn.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS StagingSessions (
                StagingSessionId TEXT PRIMARY KEY,
                AccountId        INTEGER NOT NULL,
                FileNamesJson    TEXT    NOT NULL,
                CandidatesJson   TEXT    NOT NULL,
                CreatedAt        TEXT    NOT NULL DEFAULT (datetime('now'))
            );
            """);

        // Prune expired staging sessions older than 24 hours
        await conn.ExecuteAsync("DELETE FROM StagingSessions WHERE CreatedAt < datetime('now', '-1 day');");

        // 2. Check Categories for UpdatedAt
        var catColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('Categories');"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!catColumns.Contains("UpdatedAt"))
        {
            await conn.ExecuteAsync("ALTER TABLE Categories ADD COLUMN UpdatedAt TEXT NULL;");
            string catBackfill = catColumns.Contains("CreatedAt")
                ? "UPDATE Categories SET UpdatedAt = COALESCE(CreatedAt, datetime('now')) WHERE UpdatedAt IS NULL;"
                : "UPDATE Categories SET UpdatedAt = datetime('now') WHERE UpdatedAt IS NULL;";
            await conn.ExecuteAsync(catBackfill);
        }

        // 3. Check CategorizationRules for UpdatedAt
        var ruleColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('CategorizationRules');"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ruleColumns.Count > 0 && !ruleColumns.Contains("UpdatedAt"))
        {
            await conn.ExecuteAsync("ALTER TABLE CategorizationRules ADD COLUMN UpdatedAt TEXT NULL;");
            string ruleBackfill = ruleColumns.Contains("CreatedAt")
                ? "UPDATE CategorizationRules SET UpdatedAt = COALESCE(CreatedAt, datetime('now')) WHERE UpdatedAt IS NULL;"
                : "UPDATE CategorizationRules SET UpdatedAt = datetime('now') WHERE UpdatedAt IS NULL;";
            await conn.ExecuteAsync(ruleBackfill);
        }

        // 4. Check Transactions columns for CategorizationSource, UploadBatchId, and UpdatedAt
        var txColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('Transactions');"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!txColumns.Contains("CategorizationSource"))
        {
            await conn.ExecuteAsync(
                "ALTER TABLE Transactions ADD COLUMN CategorizationSource TEXT NOT NULL DEFAULT 'Uncategorized';");

            await conn.ExecuteAsync(
                """
                UPDATE Transactions
                SET CategorizationSource = CASE
                    WHEN CategoryId IS NULL THEN 'Uncategorized'
                    WHEN IsCategoryManuallyLocked = 1 THEN 'Manual'
                    ELSE 'Rule'
                END;
                """);
        }

        if (!txColumns.Contains("UploadBatchId"))
        {
            await conn.ExecuteAsync("ALTER TABLE Transactions ADD COLUMN UploadBatchId TEXT NULL;");
        }

        if (!txColumns.Contains("UpdatedAt"))
        {
            await conn.ExecuteAsync("ALTER TABLE Transactions ADD COLUMN UpdatedAt TEXT NULL;");
            string txBackfill = txColumns.Contains("CreatedAt")
                ? "UPDATE Transactions SET UpdatedAt = COALESCE(CreatedAt, TransactionDate, datetime('now')) WHERE UpdatedAt IS NULL;"
                : "UPDATE Transactions SET UpdatedAt = COALESCE(TransactionDate, datetime('now')) WHERE UpdatedAt IS NULL;";
            await conn.ExecuteAsync(txBackfill);
        }

        await conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_Transactions_UpdatedAt ON Transactions(UpdatedAt);");

        // Normalize any legacy TransactionType values ('Payment' or 'Transfer') to canonical 'CreditCardPayoff'
        await conn.ExecuteAsync(
            "UPDATE Transactions SET TransactionType = 'CreditCardPayoff' WHERE TransactionType IN ('Payment', 'Transfer');");
    }
}
