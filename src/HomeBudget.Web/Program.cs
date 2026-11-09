using System.Globalization;
using Dapper;
using HomeBudget.Web.Data;
using HomeBudget.Web.Models;
using HomeBudget.Web.Services;

SqlMapper.AddTypeHandler(new SqliteDecimalHandler());
SqlMapper.AddTypeHandler(new SqliteBoolHandler());

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.ValueCountLimit = int.MaxValue;
    o.MultipartBodyLengthLimit = 104857600;
});

builder.Services.AddSingleton<SqliteConnectionFactory>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<CsvParserService>();
builder.Services.AddSingleton<IngestionStagingService>();
builder.Services.AddSingleton<CategorizationService>();
builder.Services.AddSingleton<CashflowService>();
builder.Services.AddSingleton<BudgetService>();

var app = builder.Build();

await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

// ============================================================================
// 1. ACCOUNTS & CATEGORIES / TOPOLOGY ENDPOINTS (WITH AUTOMATIC DB BACKUPS)
// ============================================================================

app.MapGet("/api/accounts", async (SqliteConnectionFactory db) =>
{
    await using var conn = await db.CreateOpenConnectionAsync();
    var accounts = await conn.QueryAsync<Account>("SELECT * FROM Accounts WHERE IsActive = 1 ORDER BY AccountId;");
    return Results.Ok(accounts);
});

app.MapPost("/api/accounts", async (Account req, SqliteConnectionFactory db, BackupService backupService, CancellationToken ct) =>
{
    await using var conn = await db.CreateOpenConnectionAsync(ct);
    int id = await conn.ExecuteScalarAsync<int>(
        "INSERT INTO Accounts (AccountName, Institution, AccountType, IsActive) VALUES (@AccountName, @Institution, @AccountType, 1); SELECT last_insert_rowid();", req);
    req.AccountId = id;
    await backupService.CreateSnapshotImmediateAsync("account_create", ct);
    return Results.Ok(req);
});

app.MapGet("/api/categories", async (SqliteConnectionFactory db) =>
{
    await using var conn = await db.CreateOpenConnectionAsync();
    var categories = await conn.QueryAsync<Category>(
        "SELECT * FROM Categories WHERE IsActive = 1 ORDER BY SortOrder ASC, Name ASC;");
    return Results.Ok(categories);
});

app.MapPost("/api/categories", async (UpsertCategoryRequest req, CategorizationService catService, CancellationToken ct) =>
{
    var created = await catService.CreateCategoryAsync(req, ct);
    return Results.Ok(created);
});

app.MapPut("/api/categories/{id:int}", async (int id, UpsertCategoryRequest req, CategorizationService catService, CancellationToken ct) =>
{
    var updated = await catService.UpdateCategoryAsync(id, req, ct);
    return updated is not null ? Results.Ok(updated) : Results.NotFound();
});

app.MapDelete("/api/categories/{id:int}", async (int id, int? replacementCategoryId, CategorizationService catService, CancellationToken ct) =>
{
    var res = await catService.DeleteCategoryAsync(id, replacementCategoryId, ct);
    return res is not null ? Results.Ok(res) : Results.NotFound();
});

app.MapPut("/api/categories/{id:int}/budget", async (int id, UpdateBudgetRequest req, BudgetService budgetService, CancellationToken ct) =>
{
    var updated = await budgetService.UpdateCategoryMonthlyBudgetAsync(id, req.MonthlyBudget, ct);
    return updated is not null ? Results.Ok(updated) : Results.NotFound();
});

app.MapPost("/api/categories/{id:int}/merge-into/{targetId:int}", async (
    int id,
    int targetId,
    CategorizationService catService,
    CancellationToken ct) =>
{
    var result = await catService.MergeCategoriesAsync(id, targetId, ct);
    return Results.Ok(result);
});

app.MapGet("/api/topology", async (CategorizationService catService, CancellationToken ct) =>
{
    var result = await catService.GetTopologyAsync(ct);
    return Results.Ok(result);
});

app.MapPost("/api/rules", async (UpsertRuleRequest req, CategorizationService catService, CancellationToken ct) =>
{
    var rule = await catService.CreateRuleAsync(req, ct);
    return Results.Ok(rule);
});

app.MapPut("/api/rules/{id:int}", async (int id, UpsertRuleRequest req, CategorizationService catService, CancellationToken ct) =>
{
    var rule = await catService.UpdateRuleAsync(id, req, ct);
    return rule is not null ? Results.Ok(rule) : Results.NotFound();
});

app.MapDelete("/api/rules/{id:int}", async (int id, CategorizationService catService, CancellationToken ct) =>
{
    bool deleted = await catService.DeleteRuleAsync(id, ct);
    return deleted ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/rules/back-propagate", async (
    BackPropagateRequest req,
    CategorizationService catService,
    CancellationToken ct) =>
{
    var response = await catService.BackPropagateRulesAsync(req, ct);
    return Results.Ok(response);
});

// ============================================================================
// 2. CASHFLOW SANKEY & MONTHLY BUDGET VIEWS
// ============================================================================

app.MapGet("/api/cashflow", async (
    string? windowType,
    string? startDate,
    string? endDate,
    int? months,
    bool? includeSubcategories,
    CashflowService cashflowService,
    CancellationToken ct) =>
{
    var result = await cashflowService.GetCashflowAsync(
        windowType,
        startDate,
        endDate,
        months,
        includeSubcategories ?? false,
        ct);
    return Results.Ok(result);
});

app.MapGet("/api/budgets/{yearMonth?}", async (
    string? yearMonth,
    BudgetService budgetService,
    CancellationToken ct) =>
{
    var result = await budgetService.GetMonthlyBudgetAsync(yearMonth, ct);
    return Results.Ok(result);
});

// ============================================================================
// 3. CSV UPLOAD STAGING & DE-DUPLICATION ENDPOINTS
// ============================================================================

app.MapPost("/api/uploads/stage", async (
    HttpRequest request,
    IngestionStagingService stagingService,
    CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    if (!int.TryParse(form["accountId"], out int accountId))
    {
        return Results.BadRequest(new { error = "Valid accountId is required." });
    }

    if (form.Files.Count == 0)
    {
        return Results.BadRequest(new { error = "At least one CSV file is required." });
    }

    var session = await stagingService.StageFilesAsync(accountId, form.Files, ct);

    return Results.Ok(new
    {
        session.StagingSessionId,
        session.AccountId,
        session.FileNames,
        summary = new
        {
            totalParsed = session.Candidates.Count,
            autoApproveCount = session.Candidates.Count(c => c.DedupStatus == "AutoApprove"),
            potentialCollisionCount = session.Candidates.Count(c => c.DedupStatus == "PotentialCollision"),
            manualReviewCount = session.Candidates.Count(c => c.RequiresManualReview && c.DedupStatus == "AutoApprove"),
            skippedDuplicateCount = session.Candidates.Count(c => c.DedupStatus == "SkippedDuplicate"),
            skippedDeletedCount = session.Candidates.Count(c => c.DedupStatus == "SkippedDeleted")
        },
        candidates = session.Candidates
    });
}).DisableAntiforgery();

app.MapPost("/api/uploads/commit", async (
    CommitStagingRequest request,
    IngestionStagingService stagingService,
    CancellationToken ct) =>
{
    var result = await stagingService.CommitSessionAsync(request, ct);
    return Results.Ok(result);
});

// ============================================================================
// 4. TRANSACTION INSPECTOR, CATEGORIZATION PROMPTS, EDIT, SPLIT & DELETE
// ============================================================================

app.MapGet("/api/transactions/similar-count", async (
    string? pattern,
    int? excludeTransactionId,
    CategorizationService catService,
    CancellationToken ct) =>
{
    var res = await catService.GetSimilarTransactionsCountAsync(pattern, excludeTransactionId, ct);
    return Results.Ok(res);
});

app.MapPost("/api/transactions/{id:int}/categorize", async (
    int id,
    CategorizeTransactionRequest req,
    CategorizationService catService,
    CancellationToken ct) =>
{
    var res = await catService.CategorizeTransactionWithOptionsAsync(id, req, ct);
    return res is not null ? Results.Ok(res) : Results.NotFound();
});

app.MapGet("/api/transactions", async (
    string? startDate,
    string? endDate,
    int? accountId,
    int? categoryId,
    bool? uncategorizedOnly,
    string? transactionType,
    decimal? minAmount,
    decimal? maxAmount,
    string? search,
    string? sortBy,
    int? page,
    int? pageSize,
    SqliteConnectionFactory db,
    CancellationToken ct) =>
{
    await using var conn = await db.CreateOpenConnectionAsync(ct);

    var conditions = new List<string> { "1=1" };
    var parameters = new DynamicParameters();

    if (!string.IsNullOrWhiteSpace(startDate))
    {
        conditions.Add("t.TransactionDate >= @StartDate");
        parameters.Add("StartDate", startDate.Trim());
    }
    if (!string.IsNullOrWhiteSpace(endDate))
    {
        conditions.Add("t.TransactionDate <= @EndDate");
        parameters.Add("EndDate", endDate.Trim());
    }
    if (accountId.HasValue)
    {
        conditions.Add("t.AccountId = @AccountId");
        parameters.Add("AccountId", accountId.Value);
    }
    if (uncategorizedOnly == true)
    {
        conditions.Add("t.CategoryId IS NULL AND t.TransactionType IN ('Expense', 'Refund')");
    }
    else if (categoryId.HasValue)
    {
        conditions.Add("t.CategoryId = @CategoryId");
        parameters.Add("CategoryId", categoryId.Value);
    }
    if (!string.IsNullOrWhiteSpace(transactionType))
    {
        string tt = transactionType.Trim();
        if (tt.Equals("ExpenseOrRefund", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add("t.TransactionType IN ('Expense', 'Refund')");
        }
        else
        {
            conditions.Add("t.TransactionType = @TransactionType");
            parameters.Add("TransactionType", tt);
        }
    }
    if (minAmount.HasValue)
    {
        conditions.Add("ABS(t.Amount) >= @MinAmount");
        parameters.Add("MinAmount", (double)minAmount.Value);
    }
    if (maxAmount.HasValue)
    {
        conditions.Add("ABS(t.Amount) <= @MaxAmount");
        parameters.Add("MaxAmount", (double)maxAmount.Value);
    }
    if (!string.IsNullOrWhiteSpace(search))
    {
        conditions.Add("(t.RawDescription LIKE @Search OR t.CleanMerchantName LIKE @Search OR t.Subcategory LIKE @Search OR t.Notes LIKE @Search)");
        parameters.Add("Search", $"%{search.Trim()}%");
    }

    string orderByClause = (sortBy ?? "date_desc").ToLowerInvariant() switch
    {
        "date_asc" => "t.TransactionDate ASC, t.TransactionId ASC",
        "amount_desc" => "ABS(t.Amount) DESC, t.TransactionDate DESC",
        "amount_asc" => "ABS(t.Amount) ASC, t.TransactionDate DESC",
        "merchant_asc" => "t.CleanMerchantName ASC, t.TransactionDate DESC",
        _ => "t.TransactionDate DESC, t.TransactionId DESC"
    };

    string whereClause = string.Join(" AND ", conditions);
    int limit = Math.Clamp(pageSize ?? 200, 1, 1000);
    int offset = (Math.Max(page ?? 1, 1) - 1) * limit;
    parameters.Add("Limit", limit);
    parameters.Add("Offset", offset);

    int totalCount = await conn.ExecuteScalarAsync<int>(
        $"SELECT COUNT(*) FROM Transactions t WHERE {whereClause};", parameters);

    var items = await conn.QueryAsync<TransactionDto>(
        $"SELECT t.*, a.AccountName, c.Name AS CategoryName " +
        $"FROM Transactions t " +
        $"JOIN Accounts a ON t.AccountId = a.AccountId " +
        $"LEFT JOIN Categories c ON t.CategoryId = c.CategoryId " +
        $"WHERE {whereClause} " +
        $"ORDER BY {orderByClause} " +
        $"LIMIT @Limit OFFSET @Offset;",
        parameters);

    return Results.Ok(new { totalCount, page = page ?? 1, pageSize = limit, items });
});

app.MapPut("/api/transactions/{id:int}", async (
    int id,
    UpdateTransactionRequest req,
    CategorizationService catService,
    CancellationToken ct) =>
{
    var result = await catService.UpdateTransactionAsync(id, req, ct);
    return result is not null ? Results.Ok(result) : Results.NotFound();
});

app.MapPost("/api/transactions/{id:int}/split", async (
    int id,
    SplitTransactionRequest req,
    CategorizationService catService,
    CancellationToken ct) =>
{
    var childRows = await catService.SplitTransactionAsync(id, req, ct);
    return Results.Ok(childRows);
});

app.MapDelete("/api/transactions/{id:int}", async (
    int id,
    bool? addToTombstone,
    SqliteConnectionFactory db,
    BackupService backupService,
    CancellationToken ct) =>
{
    await using var conn = await db.CreateOpenConnectionAsync(ct);
    var txRow = await conn.QuerySingleOrDefaultAsync<TransactionEntity>(
        "SELECT * FROM Transactions WHERE TransactionId = @Id;", new { Id = id });

    if (txRow is null)
    {
        return Results.NotFound();
    }

    await using var sqlTx = await conn.BeginTransactionAsync(ct);

    if (addToTombstone ?? true)
    {
        await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO DeletedTransactionHashes (DedupHash, RawDescription, TransactionDate, Amount) VALUES (@DedupHash, @RawDescription, @TransactionDate, @Amount);",
            txRow,
            sqlTx);
    }

    await conn.ExecuteAsync("DELETE FROM Transactions WHERE TransactionId = @Id;", new { Id = id }, sqlTx);
    await sqlTx.CommitAsync(ct);

    await backupService.CreateSnapshotImmediateAsync("transaction_delete", ct);
    return Results.NoContent();
});

app.MapPost("/api/transactions/bulk-delete", async (
    BulkDeleteTransactionsRequest req,
    SqliteConnectionFactory db,
    BackupService backupService,
    CancellationToken ct) =>
{
    if (req.TransactionIds is null || req.TransactionIds.Count == 0)
    {
        return Results.BadRequest(new { error = "No transactionIds provided." });
    }

    await using var conn = await db.CreateOpenConnectionAsync(ct);
    var rows = (await conn.QueryAsync<TransactionEntity>(
        "SELECT * FROM Transactions WHERE TransactionId IN @Ids;",
        new { Ids = req.TransactionIds })).ToList();

    if (rows.Count == 0)
    {
        return Results.Ok(new { deletedCount = 0 });
    }

    await using var sqlTx = await conn.BeginTransactionAsync(ct);
    if (req.AddToTombstone)
    {
        foreach (var txRow in rows)
        {
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO DeletedTransactionHashes (DedupHash, RawDescription, TransactionDate, Amount) VALUES (@DedupHash, @RawDescription, @TransactionDate, @Amount);",
                txRow,
                sqlTx);
        }
    }

    int deletedCount = await conn.ExecuteAsync(
        "DELETE FROM Transactions WHERE TransactionId IN @Ids;",
        new { Ids = req.TransactionIds },
        sqlTx);

    await sqlTx.CommitAsync(ct);
    string backupFile = await backupService.CreateSnapshotImmediateAsync("transaction_delete", ct);

    return Results.Ok(new { deletedCount, backupFileCreated = backupFile });
});

// ============================================================================
// 5. SETTINGS, INCOME OVERRIDES & LOCAL BACKUP MANAGEMENT
// ============================================================================

app.MapGet("/api/settings", async (SqliteConnectionFactory db) =>
{
    await using var conn = await db.CreateOpenConnectionAsync();
    var dict = (await conn.QueryAsync<(string Key, string Value)>("SELECT Key, Value FROM Settings;"))
        .ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    var overrides = await conn.QueryAsync<IncomeOverrideDto>(
        "SELECT YearMonth, IncomeAmount, Notes FROM IncomeOverrides ORDER BY YearMonth DESC;");

    return Results.Ok(new
    {
        persistentMonthlyIncome = dict.TryGetValue("PersistentMonthlyIncome", out var inc) && decimal.TryParse(inc, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 10000.00m,
        googlePayrollMatchPattern = dict.GetValueOrDefault("GooglePayrollMatchPattern", "GOOGLE"),
        currentMonthIncomeMode = dict.GetValueOrDefault("CurrentMonthIncomeMode", "Auto"),
        hasGeminiApiKey = !string.IsNullOrWhiteSpace(dict.GetValueOrDefault("GeminiApiKey", "")),
        incomeOverrides = overrides
    });
});

app.MapPut("/api/settings", async (SettingsDto req, SqliteConnectionFactory db, BackupService backupService, CancellationToken ct) =>
{
    await using var conn = await db.CreateOpenConnectionAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    async Task UpsertSetting(string key, string val)
    {
        await conn.ExecuteAsync(
            "INSERT INTO Settings (Key, Value, UpdatedAt) VALUES (@Key, @Value, datetime('now')) ON CONFLICT(Key) DO UPDATE SET Value = @Value, UpdatedAt = datetime('now');",
            new { Key = key, Value = val },
            tx);
    }

    await UpsertSetting("PersistentMonthlyIncome", req.PersistentMonthlyIncome.ToString("F2", CultureInfo.InvariantCulture));
    await UpsertSetting("GooglePayrollMatchPattern", req.GooglePayrollMatchPattern.Trim());
    await UpsertSetting("CurrentMonthIncomeMode", req.CurrentMonthIncomeMode.Trim());
    if (!string.IsNullOrWhiteSpace(req.GeminiApiKey))
    {
        await UpsertSetting("GeminiApiKey", req.GeminiApiKey.Trim());
    }

    await tx.CommitAsync(ct);
    await backupService.CreateSnapshotImmediateAsync("settings_update", ct);
    return Results.Ok(new { updated = true });
});

app.MapPut("/api/settings/income-overrides", async (IncomeOverrideDto req, SqliteConnectionFactory db, BackupService backupService, CancellationToken ct) =>
{
    await using var conn = await db.CreateOpenConnectionAsync(ct);
    await conn.ExecuteAsync(
        "INSERT INTO IncomeOverrides (YearMonth, IncomeAmount, Notes) VALUES (@YearMonth, @IncomeAmount, @Notes) ON CONFLICT(YearMonth) DO UPDATE SET IncomeAmount = @IncomeAmount, Notes = @Notes;",
        req);
    await backupService.CreateSnapshotImmediateAsync("income_override", ct);
    return Results.Ok(req);
});

app.MapDelete("/api/settings/income-overrides/{yearMonth}", async (string yearMonth, SqliteConnectionFactory db, BackupService backupService, CancellationToken ct) =>
{
    await using var conn = await db.CreateOpenConnectionAsync(ct);
    await conn.ExecuteAsync("DELETE FROM IncomeOverrides WHERE YearMonth = @YearMonth;", new { YearMonth = yearMonth });
    await backupService.CreateSnapshotImmediateAsync("income_override_delete", ct);
    return Results.NoContent();
});

app.MapGet("/api/backups", (BackupService backupService) =>
    Results.Ok(new
    {
        backupDirectoryPath = backupService.BackupDirectoryPath,
        backups = backupService.ListBackups()
    }));

app.MapPost("/api/backups/trigger", async (BackupService backupService, CancellationToken ct) =>
{
    string fileName = await backupService.CreateSnapshotImmediateAsync("manual", ct);
    return Results.Ok(new { fileName });
});

app.MapGet("/api/backups/download/{fileName}", (string fileName, BackupService backupService) =>
{
    string safeName = Path.GetFileName(fileName);
    string fullPath = Path.Combine(backupService.BackupDirectoryPath, safeName);
    if (!File.Exists(fullPath))
    {
        return Results.NotFound();
    }
    return Results.File(fullPath, "application/octet-stream", safeName);
});

app.Run();
