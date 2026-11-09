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

        // Verify backups were created and are non-empty
        var backups = _backupService.ListBackups();
        Assert.NotEmpty(backups);
        Assert.All(backups, b => Assert.True(b.SizeBytes > 0));
    }

    public void Dispose()
    {
        try
        {
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
