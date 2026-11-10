using Dapper;
using HomeBudget.Web.Data;
using HomeBudget.Web.Models;
using HomeBudget.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeBudget.Tests;

public sealed class DatabaseAndServicesIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly BackupService _backupService;
    private readonly CategorizationService _categorizationService;
    private readonly CashflowService _cashflowService;
    private readonly BudgetService _budgetService;

    public DatabaseAndServicesIntegrationTests()
    {
        SqlMapper.AddTypeHandler(new SqliteDecimalHandler());
        SqlMapper.AddTypeHandler(new SqliteBoolHandler());

        _tempDir = Path.Combine(Path.GetTempPath(), $"HomeBudgetTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HomeBudget:DatabasePath"] = Path.Combine(_tempDir, "budget.db"),
                ["HomeBudget:BackupDirectoryPath"] = Path.Combine(_tempDir, "Backups"),
                ["HomeBudget:MaxRetainedBackups"] = "5"
            })
            .Build();

        _connectionFactory = new SqliteConnectionFactory(config);
        _backupService = new BackupService(_connectionFactory, config, NullLogger<BackupService>.Instance);
        _categorizationService = new CategorizationService(_connectionFactory, _backupService);
        _cashflowService = new CashflowService(_connectionFactory);
        _budgetService = new BudgetService(_connectionFactory, _backupService);
    }

    [Fact]
    public async Task ApplyIdempotentSchemaMigrations_UpgradesLegacyDatabaseAndNormalizesTypes()
    {
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync();

        // Simulate a v2.0 database without CategorizationSource or StagingSessions
        await conn.ExecuteAsync(
            """
            CREATE TABLE Accounts (AccountId INTEGER PRIMARY KEY, AccountName TEXT NOT NULL, Institution TEXT NOT NULL, AccountType TEXT NOT NULL, IsActive INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')));
            CREATE TABLE Categories (CategoryId INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE, IsBigBoxMerchantCategory INTEGER NOT NULL DEFAULT 0, MonthlyBudget NUMERIC NOT NULL DEFAULT 0, SortOrder INTEGER NOT NULL DEFAULT 100, ColorHex TEXT NOT NULL DEFAULT '#3B82F6', IsActive INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')));
            CREATE TABLE Transactions (
                TransactionId INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountId INTEGER NOT NULL,
                TransactionDate TEXT NOT NULL,
                PostDate TEXT NULL,
                RawDescription TEXT NOT NULL,
                CleanMerchantName TEXT NOT NULL,
                Amount NUMERIC NOT NULL,
                TransactionType TEXT NOT NULL,
                CategoryId INTEGER NULL,
                Subcategory TEXT NULL,
                IsCategoryManuallyLocked INTEGER NOT NULL DEFAULT 0,
                ParentTransactionId INTEGER NULL,
                DedupHash TEXT NOT NULL,
                Notes TEXT NULL
            );
            INSERT INTO Accounts (AccountId, AccountName, Institution, AccountType) VALUES (1, 'Checking', 'WellsFargo', 'Checking');
            INSERT INTO Categories (CategoryId, Name, MonthlyBudget) VALUES (1, 'Groceries', 800);
            INSERT INTO Transactions (AccountId, TransactionDate, RawDescription, CleanMerchantName, Amount, TransactionType, CategoryId, IsCategoryManuallyLocked, DedupHash)
            VALUES (1, '2026-11-01', 'TRADER JOES', 'TRADER JOES', 50.00, 'Expense', 1, 1, 'HASH1'),
                   (1, '2026-11-02', 'SAFEWAY', 'SAFEWAY', 30.00, 'Expense', 1, 0, 'HASH2'),
                   (1, '2026-11-03', 'MYSTERY SHOP', 'MYSTERY SHOP', 20.00, 'Expense', NULL, 0, 'HASH3'),
                   (1, '2026-11-04', 'CC AUTOPAY', 'CC AUTOPAY', 500.00, 'Payment', NULL, 0, 'HASH4');
            """);

        await DatabaseInitializer.ApplyIdempotentSchemaMigrationsAsync(conn);

        var rows = (await conn.QueryAsync<TransactionEntity>("SELECT * FROM Transactions ORDER BY TransactionId;")).ToList();
        Assert.Equal("Manual", rows[0].CategorizationSource);
        Assert.Equal("Rule", rows[1].CategorizationSource);
        Assert.Equal("Uncategorized", rows[2].CategorizationSource);
        Assert.Equal(TransactionTypes.CreditCardPayoff, rows[3].TransactionType);
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.UpdatedAt)));

        var cat = await conn.QuerySingleAsync<Category>("SELECT * FROM Categories WHERE CategoryId = 1;");
        Assert.False(string.IsNullOrWhiteSpace(cat.UpdatedAt));
    }

    [Fact]
    public void SortCategoryRows_SupportsSpendAlphaRecentAndCustomOrderWhilePinningUncategorizedLast()
    {
        var rows = new List<CashflowTableRowDto>
        {
            new() { CategoryId = 1, CategoryName = "Utilities", SortOrder = 30, NetSpend = 120m, LastActivityAt = "2026-11-02 10:00:00" },
            new() { CategoryId = 2, CategoryName = "Automotive", SortOrder = 10, NetSpend = 450m, LastActivityAt = "2026-11-01 08:00:00" },
            new() { CategoryId = null, CategoryName = "Uncategorized", SortOrder = 999999, NetSpend = 999m, LastActivityAt = "2026-11-09 12:00:00" },
            new() { CategoryId = 3, CategoryName = "Groceries", SortOrder = 20, NetSpend = 300m, LastActivityAt = "2026-11-08 15:30:00" }
        };

        var bySpendDesc = CashflowService.SortCategoryRows(rows, "spend_desc");
        Assert.Equal(new int?[] { 2, 3, 1, null }, bySpendDesc.Select(r => r.CategoryId).ToArray());

        var bySpendAsc = CashflowService.SortCategoryRows(rows, "spend_asc");
        Assert.Equal(new int?[] { 1, 3, 2, null }, bySpendAsc.Select(r => r.CategoryId).ToArray());

        var byAlphaAsc = CashflowService.SortCategoryRows(rows, "alpha_asc");
        Assert.Equal(new[] { "Automotive", "Groceries", "Utilities", "Uncategorized" }, byAlphaAsc.Select(r => r.CategoryName).ToArray());

        var byRecent = CashflowService.SortCategoryRows(rows, "recent");
        Assert.Equal(new int?[] { 3, 1, 2, null }, byRecent.Select(r => r.CategoryId).ToArray());

        var byCustom = CashflowService.SortCategoryRows(rows, "custom_order");
        Assert.Equal(new int?[] { 2, 3, 1, null }, byCustom.Select(r => r.CategoryId).ToArray());
    }

    [Fact]
    public async Task CategorizeUpdateSplitAndBackup_PreserveProvenanceAndImmutableDedupHash()
    {
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        string schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "deploy", "schema.sql"));
        await conn.ExecuteAsync(await File.ReadAllTextAsync(schemaPath));

        // Insert two uncategorized transactions from the same merchant
        await conn.ExecuteAsync(
            """
            INSERT INTO Transactions (AccountId, TransactionDate, RawDescription, CleanMerchantName, Amount, TransactionType, DedupHash)
            VALUES (2, '2026-11-01', 'PETCO #104', 'PETCO', 45.00, 'Expense', 'ORIG_HASH_1'),
                   (2, '2026-11-05', 'PETCO #209', 'PETCO', 65.00, 'Expense', 'ORIG_HASH_2');
            """);

        // Categorize transaction #1 with inline category creation + forward rule + historical back-propagation
        var catResult = await _categorizationService.CategorizeTransactionWithOptionsAsync(1, new CategorizeTransactionRequest
        {
            NewCategoryName = "Pet Care",
            NewCategoryMonthlyBudget = 150m,
            NewCategoryColorHex = "#10B981",
            MatchPattern = "PETCO",
            ApplyMovingForward = true,
            ApplyToPast = true,
            IncludeManuallyLockedInPast = true
        });

        Assert.NotNull(catResult);

        var tx1 = await conn.QuerySingleAsync<TransactionEntity>("SELECT * FROM Transactions WHERE TransactionId = 1;");
        var tx2 = await conn.QuerySingleAsync<TransactionEntity>("SELECT * FROM Transactions WHERE TransactionId = 2;");

        Assert.Equal("Manual", tx1.CategorizationSource);
        Assert.True(tx1.IsCategoryManuallyLocked);
        Assert.Equal("Rule", tx2.CategorizationSource);
        Assert.Equal(tx1.CategoryId, tx2.CategoryId);

        // Edit transaction #1 Notes, Date, and Amount; verify DedupHash remains immutable
        await _categorizationService.UpdateTransactionAsync(1, new UpdateTransactionRequest
        {
            TransactionDate = "2026-11-02",
            Amount = 48.00m,
            Notes = "Prescription dog food"
        });

        var tx1Edited = await conn.QuerySingleAsync<TransactionEntity>("SELECT * FROM Transactions WHERE TransactionId = 1;");
        Assert.Equal("ORIG_HASH_1", tx1Edited.DedupHash);
        Assert.Equal("Prescription dog food", tx1Edited.Notes);

        // Split transaction #2 into two legs and verify ParentTransactionId and base hash preservation
        var splitRows = await _categorizationService.SplitTransactionAsync(2, new SplitTransactionRequest
        {
            Splits =
            [
                new SplitItemDto { Amount = 40.00m, CategoryId = tx1.CategoryId, Subcategory = "Food", Notes = "Leg 1" },
                new SplitItemDto { Amount = 25.00m, CategoryId = tx1.CategoryId, Subcategory = "Toys", Notes = "Leg 2" }
            ]
        });

        Assert.Equal(2, splitRows.Count);
        Assert.Equal(2, splitRows[0].ParentTransactionId);
        Assert.Equal("Leg 1", splitRows[0].Notes);
        Assert.Equal("Leg 2", splitRows[1].Notes);

        // Verify routine edits were coalesced into the pending queue and split created a major snapshot
        var statusBeforeFlush = _backupService.GetStatus();
        Assert.True(statusBeforeFlush.PendingChangesCount > 0);

        string? flushedFile = await _backupService.FlushPendingSnapshotAsync("coalesced_flush");
        Assert.False(string.IsNullOrWhiteSpace(flushedFile));

        var backups = _backupService.ListBackups();
        Assert.NotEmpty(backups);
        Assert.All(backups, b => Assert.True(b.SizeBytes > 0));
    }

    [Fact]
    public async Task SmartBackupPolicyAndAdminLifecycle_SupportsCoalescingPruningDeletionAndIntegrityVerifiedRestore()
    {
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        string schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "deploy", "schema.sql"));
        await conn.ExecuteAsync(await File.ReadAllTextAsync(schemaPath));

        // 1. Default policy is Coalesced
        var policy = await _backupService.GetPolicyConfigAsync();
        Assert.Equal("Coalesced", policy.AutoBackupMode);

        // 2. Routine edits coalesce into pending queue without writing immediate disk snapshots
        string? res1 = await _backupService.RequestBackupAsync("category_update", isMajorOperation: false);
        string? res2 = await _backupService.RequestBackupAsync("rule_create", isMajorOperation: false);
        Assert.Equal("scheduled", res1);
        Assert.Equal("scheduled", res2);
        Assert.Equal(2, _backupService.GetStatus().PendingChangesCount);

        // 3. Flush creates a single snapshot and clears the pending queue
        string? snap1 = await _backupService.FlushPendingSnapshotAsync("manual");
        Assert.NotNull(snap1);
        Assert.Equal(0, _backupService.GetStatus().PendingChangesCount);

        // 4. Modify live DB, then restore from snap1 with pre_restore safety snapshot
        await conn.ExecuteAsync("INSERT INTO Categories (Name, MonthlyBudget, SortOrder, ColorHex) VALUES ('TempCategory', 99, 999, '#000000');");
        int countBeforeRestore = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Categories WHERE Name = 'TempCategory';");
        Assert.Equal(1, countBeforeRestore);

        var restoreRes = await _backupService.RestoreSnapshotAsync(snap1!, createPreRestoreBackup: true);
        Assert.Equal(snap1, restoreRes.RestoredFrom);
        Assert.False(string.IsNullOrWhiteSpace(restoreRes.PreRestoreSnapshot));

        await using var connAfter = await _connectionFactory.CreateOpenConnectionAsync();
        int countAfterRestore = await connAfter.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Categories WHERE Name = 'TempCategory';");
        Assert.Equal(0, countAfterRestore);

        // 5. Clear transaction history scoped to Account 1 with pre-clear backup and account-scoped tombstone reset
        await connAfter.ExecuteAsync(
            """
            INSERT INTO Transactions (AccountId, TransactionDate, RawDescription, CleanMerchantName, Amount, TransactionType, DedupHash)
            VALUES (1, '2026-11-08', 'TEST CHARGE ACCT 1', 'TEST CHARGE', 99.95, 'Expense', 'CLEAR_TEST_HASH_1'),
                   (2, '2026-11-08', 'TEST CHARGE ACCT 2', 'TEST CHARGE', 42.00, 'Expense', 'CLEAR_TEST_HASH_2');
            INSERT INTO DeletedTransactionHashes (DedupHash, AccountId, RawDescription, TransactionDate, Amount)
            VALUES ('TOMBSTONE_ACCT_1', 1, 'OLD DELETED 1', '2026-11-01', 12.00),
                   ('TOMBSTONE_ACCT_2', 2, 'OLD DELETED 2', '2026-11-01', 19.00);
            """);

        var clearAcct1Res = await _categorizationService.ClearTransactionHistoryAsync(new ClearTransactionHistoryRequest
        {
            AccountId = 1,
            CreateBackupFirst = true,
            ClearDeletedTombstones = true,
            ClearStagingSessions = true
        });

        Assert.Equal(1, clearAcct1Res.DeletedTransactionsCount);
        Assert.Equal(1, clearAcct1Res.DeletedTombstonesCount);
        Assert.False(string.IsNullOrWhiteSpace(clearAcct1Res.PreClearBackupFile));
        Assert.Equal(1, await connAfter.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Transactions WHERE AccountId = 2;"));
        Assert.Equal(1, await connAfter.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DeletedTransactionHashes WHERE AccountId = 2;"));

        // Restore the pre-clear snapshot and verify Account 1's transaction is restored
        await _backupService.RestoreSnapshotAsync(clearAcct1Res.PreClearBackupFile!, createPreRestoreBackup: false);
        await using var connRestored = await _connectionFactory.CreateOpenConnectionAsync();
        Assert.Equal(1, await connRestored.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Transactions WHERE DedupHash = 'CLEAR_TEST_HASH_1';"));

        // 6. Configure custom MaxRetainedBackups (50) and verify ResetAllDatabaseAsync preserves backup policy & all backup files
        await _backupService.UpdatePolicyConfigAsync(new BackupPolicyConfigDto
        {
            AutoBackupMode = "Coalesced",
            DebounceSeconds = 45,
            MinIntervalMinutes = 15,
            MaxRetainedBackups = 50
        });

        int backupsBeforeReset = _backupService.ListBackups().Count;
        var resetRes = await _categorizationService.ResetAllDatabaseAsync(new ResetAllDatabaseRequest
        {
            CreateBackupFirst = true,
            ReseedDefaultCategoriesAndRules = true
        });
        Assert.Equal(2, resetRes.DeletedTransactionsCount);
        Assert.True(resetRes.ReseededDefaults);
        Assert.False(string.IsNullOrWhiteSpace(resetRes.PreResetBackupFile));
        Assert.Equal(backupsBeforeReset + 1, resetRes.RetainedBackupFilesCount);
        Assert.Equal(0, await connRestored.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Transactions;"));
        Assert.Equal(12, await connRestored.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Categories;"));
        Assert.Equal(15, await connRestored.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM CategorizationRules;"));
        Assert.Equal(50, (await _backupService.GetPolicyConfigAsync()).MaxRetainedBackups);

        // 7. Reset with ReseedDefaultCategoriesAndRules = false and verify re-running schema.sql does NOT resurrect defaults
        await _categorizationService.ResetAllDatabaseAsync(new ResetAllDatabaseRequest
        {
            CreateBackupFirst = false,
            ReseedDefaultCategoriesAndRules = false
        });
        await connRestored.ExecuteAsync(await File.ReadAllTextAsync(schemaPath));
        Assert.Equal(0, await connRestored.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Categories;"));
        Assert.Equal(0, await connRestored.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM CategorizationRules;"));

        // 8. Prune & delete snapshots
        int pruned = _backupService.PruneBackupsToCount(1);
        Assert.True(pruned >= 1);
        Assert.Single(_backupService.ListBackups());
    }

    public void Dispose()
    {
        try
        {
            _backupService.Dispose();
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}

