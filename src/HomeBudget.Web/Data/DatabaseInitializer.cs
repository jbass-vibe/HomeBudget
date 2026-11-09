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
}
