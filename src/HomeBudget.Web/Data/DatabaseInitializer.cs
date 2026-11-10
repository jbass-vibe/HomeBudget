using Dapper;
using HomeBudget.Web.Models;

namespace HomeBudget.Web.Data;

public sealed class DatabaseInitializer(
    SqliteConnectionFactory connectionFactory,
    IWebHostEnvironment env,
    ILogger<DatabaseInitializer> logger)
{
    public const string CanonicalTablesAndIndexesSql =
        """
        CREATE TABLE IF NOT EXISTS Accounts (
            AccountId       INTEGER PRIMARY KEY AUTOINCREMENT,
            AccountName     TEXT    NOT NULL,
            Institution     TEXT    NOT NULL,
            AccountType     TEXT    NOT NULL,
            IsActive        INTEGER NOT NULL DEFAULT 1,
            CreatedAt       TEXT    NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS Categories (
            CategoryId               INTEGER PRIMARY KEY AUTOINCREMENT,
            Name                     TEXT    NOT NULL UNIQUE,
            IsBigBoxMerchantCategory INTEGER NOT NULL DEFAULT 0,
            MonthlyBudget            NUMERIC NOT NULL DEFAULT 0.00,
            SortOrder                INTEGER NOT NULL DEFAULT 100,
            ColorHex                 TEXT    NOT NULL DEFAULT '#3B82F6',
            IsActive                 INTEGER NOT NULL DEFAULT 1,
            CreatedAt                TEXT    NOT NULL DEFAULT (datetime('now')),
            UpdatedAt                TEXT    NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS CategorizationRules (
            RuleId              INTEGER PRIMARY KEY AUTOINCREMENT,
            MatchPattern        TEXT    NOT NULL,
            IsRegex             INTEGER NOT NULL DEFAULT 0,
            CategoryId          INTEGER NOT NULL,
            DefaultSubcategory  TEXT    NULL,
            Priority            INTEGER NOT NULL DEFAULT 100,
            CreatedAt           TEXT    NOT NULL DEFAULT (datetime('now')),
            UpdatedAt           TEXT    NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS Transactions (
            TransactionId            INTEGER PRIMARY KEY AUTOINCREMENT,
            AccountId                INTEGER NOT NULL,
            TransactionDate          TEXT    NOT NULL,
            PostDate                 TEXT    NULL,
            RawDescription           TEXT    NOT NULL,
            CleanMerchantName        TEXT    NOT NULL,
            Amount                   NUMERIC NOT NULL,
            TransactionType          TEXT    NOT NULL,
            CategoryId               INTEGER NULL,
            Subcategory              TEXT    NULL,
            CategorizationSource     TEXT    NOT NULL DEFAULT 'Uncategorized',
            IsCategoryManuallyLocked INTEGER NOT NULL DEFAULT 0,
            ParentTransactionId      INTEGER NULL,
            DedupHash                TEXT    NOT NULL,
            UploadBatchId            TEXT    NULL,
            Notes                    TEXT    NULL,
            CreatedAt                TEXT    NOT NULL DEFAULT (datetime('now')),
            UpdatedAt                TEXT    NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (AccountId)  REFERENCES Accounts(AccountId),
            FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId) ON DELETE SET NULL
        );

        CREATE TABLE IF NOT EXISTS DeletedTransactionHashes (
            DedupHash       TEXT    PRIMARY KEY,
            AccountId       INTEGER NULL,
            RawDescription  TEXT    NOT NULL,
            TransactionDate TEXT    NOT NULL,
            Amount          NUMERIC NOT NULL,
            DeletedAt       TEXT    NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS StagingSessions (
            StagingSessionId TEXT PRIMARY KEY,
            AccountId        INTEGER NOT NULL,
            FileNamesJson    TEXT    NOT NULL,
            CandidatesJson   TEXT    NOT NULL,
            CreatedAt        TEXT    NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS Settings (
            Key             TEXT PRIMARY KEY,
            Value           TEXT NOT NULL,
            UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS IncomeOverrides (
            YearMonth       TEXT PRIMARY KEY,
            IncomeAmount    NUMERIC NOT NULL,
            Notes           TEXT NULL,
            UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS IX_Transactions_Date ON Transactions(TransactionDate);
        CREATE INDEX IF NOT EXISTS IX_Transactions_Category ON Transactions(CategoryId);
        CREATE INDEX IF NOT EXISTS IX_Transactions_Type ON Transactions(TransactionType);
        CREATE INDEX IF NOT EXISTS IX_Transactions_Merchant ON Transactions(CleanMerchantName);
        CREATE INDEX IF NOT EXISTS IX_Transactions_DedupHash ON Transactions(DedupHash);
        CREATE INDEX IF NOT EXISTS IX_Transactions_UpdatedAt ON Transactions(UpdatedAt);
        """;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);

        int existingTables = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Transactions', 'Accounts');");

        // If upgrading an existing v1/v2.0 database, run column/table migrations BEFORE schema.sql creates indexes on UpdatedAt
        if (existingTables > 0)
        {
            await ApplyIdempotentSchemaMigrationsAsync(conn);
        }

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
        else
        {
            await conn.ExecuteAsync(CanonicalTablesAndIndexesSql);
        }

        await ApplyIdempotentSchemaMigrationsAsync(conn);

        int alreadySeeded = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Settings WHERE Key = 'DefaultTaxonomySeeded';");

        if (alreadySeeded == 0)
        {
            await SeedDefaultCategoriesAndRulesAsync(conn);
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO Settings (Key, Value) VALUES ('DefaultTaxonomySeeded', '1');");
        }

        logger.LogInformation("SQLite database initialized in WAL mode at {DatabasePath}", connectionFactory.DatabasePath);
    }

    public static async Task SeedDefaultCategoriesAndRulesAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction? tx = null)
    {
        await conn.ExecuteAsync(
            """
            INSERT OR IGNORE INTO Categories (Name, IsBigBoxMerchantCategory, MonthlyBudget, SortOrder, ColorHex, IsActive) VALUES
            ('Housing & Utilities',  0, 3200.00,  10, '#2563EB', 1),
            ('Groceries',            0, 1000.00,  20, '#10B981', 1),
            ('Costco',               1,  600.00,  30, '#E11D48', 1),
            ('Amazon',               1,  400.00,  40, '#F59E0B', 1),
            ('Target',               1,  300.00,  50, '#DC2626', 1),
            ('Walmart',              1,  200.00,  60, '#0284C7', 1),
            ('Dining & Coffee',      0,  500.00,  70, '#8B5CF6', 1),
            ('Transportation & Gas', 0,  350.00,  80, '#14B8A6', 1),
            ('Subscriptions',        0,  150.00,  90, '#6366F1', 1),
            ('Healthcare',           0,  250.00, 100, '#EC4899', 1),
            ('Shopping & Household', 0,  400.00, 110, '#0EA5E9', 1),
            ('Entertainment & Misc', 0,  300.00, 120, '#64748B', 1);
            """,
            transaction: tx);

        int ruleCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM CategorizationRules;",
            transaction: tx);

        if (ruleCount == 0)
        {
            var categories = await conn.QueryAsync<Category>(
                "SELECT CategoryId, Name FROM Categories;",
                transaction: tx);
            var catMap = categories.ToDictionary(x => x.Name, x => x.CategoryId, StringComparer.OrdinalIgnoreCase);

            async Task AddDefaultRule(string pattern, string catName, int priority)
            {
                if (catMap.TryGetValue(catName, out int catId))
                {
                    await conn.ExecuteAsync(
                        "INSERT INTO CategorizationRules (MatchPattern, IsRegex, CategoryId, Priority) VALUES (@Pattern, 0, @CatId, @Priority);",
                        new { Pattern = pattern, CatId = catId, Priority = priority },
                        transaction: tx);
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
    }

    public static async Task RecreateCleanSchemaAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        bool reseedDefaultCategoriesAndRules,
        BackupPolicyConfigDto? preservedBackupPolicy = null)
    {
        var userTables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';",
            transaction: tx)).ToList();

        foreach (var tableName in userTables)
        {
            string safeIdentifier = tableName.Replace("\"", "\"\"");
            await conn.ExecuteAsync($"DROP TABLE IF EXISTS \"{safeIdentifier}\";", transaction: tx);
        }

        int seqExists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_sequence';",
            transaction: tx);
        if (seqExists > 0)
        {
            await conn.ExecuteAsync("DELETE FROM sqlite_sequence;", transaction: tx);
        }

        await conn.ExecuteAsync(CanonicalTablesAndIndexesSql, transaction: tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO Accounts (AccountId, AccountName, Institution, AccountType, IsActive) VALUES
            (1, 'Wells Fargo Household Checking', 'WellsFargo', 'Checking', 1),
            (2, 'Chase Household Credit Card', 'Chase', 'CreditCard', 1),
            (3, 'American Express Credit Card', 'Amex', 'CreditCard', 1),
            (4, 'Citibank Credit Card', 'Citibank', 'CreditCard', 1);
            """,
            transaction: tx);

        string autoMode = preservedBackupPolicy?.AutoBackupMode ?? "Coalesced";
        string debounceSec = (preservedBackupPolicy?.DebounceSeconds ?? 30).ToString();
        string minIntervalMin = (preservedBackupPolicy?.MinIntervalMinutes ?? 10).ToString();
        string maxRetained = (preservedBackupPolicy?.MaxRetainedBackups ?? 15).ToString();

        await conn.ExecuteAsync(
            """
            INSERT INTO Settings (Key, Value) VALUES
            ('PersistentMonthlyIncome', '10000.00'),
            ('GooglePayrollMatchPattern', 'GOOGLE'),
            ('CurrentMonthIncomeMode', 'Auto'),
            ('GeminiApiKey', ''),
            ('BackupAutoMode', @AutoMode),
            ('BackupDebounceSeconds', @DebounceSec),
            ('BackupMinIntervalMinutes', @MinIntervalMin),
            ('MaxRetainedBackups', @MaxRetained),
            ('DefaultTaxonomySeeded', '1');
            """,
            new
            {
                AutoMode = autoMode,
                DebounceSec = debounceSec,
                MinIntervalMin = minIntervalMin,
                MaxRetained = maxRetained
            },
            transaction: tx);

        if (reseedDefaultCategoriesAndRules)
        {
            await SeedDefaultCategoriesAndRulesAsync(conn, tx);
        }
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

        // 2. Check Accounts for CreatedAt & legacy v1 CHECK constraint removal
        var acctColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('Accounts');"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (acctColumns.Count > 0 && !acctColumns.Contains("CreatedAt"))
        {
            await conn.ExecuteAsync("ALTER TABLE Accounts ADD COLUMN CreatedAt TEXT NULL;");
            await conn.ExecuteAsync("UPDATE Accounts SET CreatedAt = datetime('now') WHERE CreatedAt IS NULL;");
        }

        string? accountsDdl = await conn.ExecuteScalarAsync<string?>(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'Accounts';");
        if (!string.IsNullOrWhiteSpace(accountsDdl) &&
            accountsDdl.Contains("CHECK", StringComparison.OrdinalIgnoreCase))
        {
            await conn.ExecuteAsync("PRAGMA foreign_keys = OFF;");
            try
            {
                await using var acctTx = await conn.BeginTransactionAsync();
                await conn.ExecuteAsync(
                    """
                    CREATE TABLE Accounts_v2 (
                        AccountId   INTEGER PRIMARY KEY AUTOINCREMENT,
                        AccountName TEXT    NOT NULL,
                        Institution TEXT    NOT NULL,
                        AccountType TEXT    NOT NULL,
                        IsActive    INTEGER NOT NULL DEFAULT 1,
                        CreatedAt   TEXT    NOT NULL DEFAULT (datetime('now'))
                    );
                    INSERT INTO Accounts_v2 (AccountId, AccountName, Institution, AccountType, IsActive, CreatedAt)
                    SELECT AccountId, AccountName, Institution, AccountType, IsActive, COALESCE(CreatedAt, datetime('now'))
                    FROM Accounts;
                    DROP TABLE Accounts;
                    ALTER TABLE Accounts_v2 RENAME TO Accounts;
                    """,
                    transaction: acctTx);
                await acctTx.CommitAsync();
            }
            finally
            {
                await conn.ExecuteAsync("PRAGMA foreign_keys = ON;");
            }
        }

        if (acctColumns.Count > 0)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO Accounts (AccountName, Institution, AccountType, IsActive, CreatedAt)
                SELECT 'American Express Credit Card', 'Amex', 'CreditCard', 1, datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM Accounts WHERE Institution IN ('Amex', 'AmericanExpress', 'American Express'));

                INSERT INTO Accounts (AccountName, Institution, AccountType, IsActive, CreatedAt)
                SELECT 'Citibank Credit Card', 'Citibank', 'CreditCard', 1, datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM Accounts WHERE Institution IN ('Citibank', 'Citi'));
                """);
        }

        // 3. Check Categories for CreatedAt and UpdatedAt
        var catColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('Categories');"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (catColumns.Count > 0 && !catColumns.Contains("CreatedAt"))
        {
            await conn.ExecuteAsync("ALTER TABLE Categories ADD COLUMN CreatedAt TEXT NULL;");
            await conn.ExecuteAsync("UPDATE Categories SET CreatedAt = datetime('now') WHERE CreatedAt IS NULL;");
            catColumns.Add("CreatedAt");
        }

        if (catColumns.Count > 0 && !catColumns.Contains("UpdatedAt"))
        {
            await conn.ExecuteAsync("ALTER TABLE Categories ADD COLUMN UpdatedAt TEXT NULL;");
            string catBackfill = catColumns.Contains("CreatedAt")
                ? "UPDATE Categories SET UpdatedAt = COALESCE(CreatedAt, datetime('now')) WHERE UpdatedAt IS NULL;"
                : "UPDATE Categories SET UpdatedAt = datetime('now') WHERE UpdatedAt IS NULL;";
            await conn.ExecuteAsync(catBackfill);
        }

        // 4. Check CategorizationRules for CreatedAt and UpdatedAt
        var ruleColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('CategorizationRules');"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ruleColumns.Count > 0 && !ruleColumns.Contains("CreatedAt"))
        {
            await conn.ExecuteAsync("ALTER TABLE CategorizationRules ADD COLUMN CreatedAt TEXT NULL;");
            await conn.ExecuteAsync("UPDATE CategorizationRules SET CreatedAt = datetime('now') WHERE CreatedAt IS NULL;");
            ruleColumns.Add("CreatedAt");
        }

        if (ruleColumns.Count > 0 && !ruleColumns.Contains("UpdatedAt"))
        {
            await conn.ExecuteAsync("ALTER TABLE CategorizationRules ADD COLUMN UpdatedAt TEXT NULL;");
            string ruleBackfill = ruleColumns.Contains("CreatedAt")
                ? "UPDATE CategorizationRules SET UpdatedAt = COALESCE(CreatedAt, datetime('now')) WHERE UpdatedAt IS NULL;"
                : "UPDATE CategorizationRules SET UpdatedAt = datetime('now') WHERE UpdatedAt IS NULL;";
            await conn.ExecuteAsync(ruleBackfill);
        }

        // 5. Check IncomeOverrides for UpdatedAt
        var overrideColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('IncomeOverrides');"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (overrideColumns.Count > 0 && !overrideColumns.Contains("UpdatedAt"))
        {
            await conn.ExecuteAsync("ALTER TABLE IncomeOverrides ADD COLUMN UpdatedAt TEXT NULL;");
            await conn.ExecuteAsync("UPDATE IncomeOverrides SET UpdatedAt = datetime('now') WHERE UpdatedAt IS NULL;");
        }

        // 6. Check DeletedTransactionHashes for AccountId
        var tombstoneColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('DeletedTransactionHashes');"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tombstoneColumns.Count > 0 && !tombstoneColumns.Contains("AccountId"))
        {
            await conn.ExecuteAsync("ALTER TABLE DeletedTransactionHashes ADD COLUMN AccountId INTEGER NULL;");
        }

        // 7. Check Transactions columns for CategorizationSource, UploadBatchId, CreatedAt, and UpdatedAt
        var txColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('Transactions');"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (txColumns.Count > 0)
        {
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

            if (!txColumns.Contains("CreatedAt"))
            {
                await conn.ExecuteAsync("ALTER TABLE Transactions ADD COLUMN CreatedAt TEXT NULL;");
                await conn.ExecuteAsync("UPDATE Transactions SET CreatedAt = COALESCE(TransactionDate, datetime('now')) WHERE CreatedAt IS NULL;");
                txColumns.Add("CreatedAt");
            }

            if (!txColumns.Contains("UpdatedAt"))
            {
                await conn.ExecuteAsync("ALTER TABLE Transactions ADD COLUMN UpdatedAt TEXT NULL;");
                string txBackfill = txColumns.Contains("CreatedAt")
                    ? "UPDATE Transactions SET UpdatedAt = COALESCE(CreatedAt, TransactionDate, datetime('now')) WHERE UpdatedAt IS NULL;"
                    : "UPDATE Transactions SET UpdatedAt = COALESCE(TransactionDate, datetime('now')) WHERE UpdatedAt IS NULL;";
                await conn.ExecuteAsync(txBackfill);
            }

            // 8. Remove legacy UploadBatches table and foreign key from v1 databases so Accounts/Transactions are never blocked by FK Error 19
            int hasUploadBatchesFk = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM pragma_foreign_key_list('Transactions') WHERE \"table\" = 'UploadBatches';");
            int hasUploadBatchesTable = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'UploadBatches';");

            if (hasUploadBatchesFk > 0 || hasUploadBatchesTable > 0)
            {
                await conn.ExecuteAsync("PRAGMA foreign_keys = OFF;");
                try
                {
                    await using var tx = await conn.BeginTransactionAsync();
                    if (hasUploadBatchesFk > 0)
                    {
                        await conn.ExecuteAsync(
                            """
                            CREATE TABLE Transactions_v2 (
                                TransactionId            INTEGER PRIMARY KEY AUTOINCREMENT,
                                AccountId                INTEGER NOT NULL,
                                TransactionDate          TEXT    NOT NULL,
                                PostDate                 TEXT    NULL,
                                RawDescription           TEXT    NOT NULL,
                                CleanMerchantName        TEXT    NOT NULL,
                                Amount                   NUMERIC NOT NULL,
                                TransactionType          TEXT    NOT NULL,
                                CategoryId               INTEGER NULL,
                                Subcategory              TEXT    NULL,
                                CategorizationSource     TEXT    NOT NULL DEFAULT 'Uncategorized',
                                IsCategoryManuallyLocked INTEGER NOT NULL DEFAULT 0,
                                ParentTransactionId      INTEGER NULL,
                                DedupHash                TEXT    NOT NULL,
                                UploadBatchId            TEXT    NULL,
                                Notes                    TEXT    NULL,
                                CreatedAt                TEXT    NOT NULL DEFAULT (datetime('now')),
                                UpdatedAt                TEXT    NOT NULL DEFAULT (datetime('now')),
                                FOREIGN KEY (AccountId)  REFERENCES Accounts(AccountId),
                                FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId) ON DELETE SET NULL
                            );
                            INSERT INTO Transactions_v2 (
                                TransactionId, AccountId, TransactionDate, PostDate, RawDescription, CleanMerchantName,
                                Amount, TransactionType, CategoryId, Subcategory, CategorizationSource,
                                IsCategoryManuallyLocked, ParentTransactionId, DedupHash, UploadBatchId, Notes, CreatedAt, UpdatedAt
                            )
                            SELECT
                                TransactionId, AccountId, TransactionDate, PostDate, RawDescription, CleanMerchantName,
                                Amount, TransactionType, CategoryId, Subcategory, CategorizationSource,
                                IsCategoryManuallyLocked, ParentTransactionId, DedupHash, CAST(UploadBatchId AS TEXT), Notes,
                                COALESCE(CreatedAt, TransactionDate, datetime('now')),
                                COALESCE(UpdatedAt, TransactionDate, datetime('now'))
                            FROM Transactions;
                            DROP TABLE Transactions;
                            ALTER TABLE Transactions_v2 RENAME TO Transactions;
                            """,
                            transaction: tx);
                    }

                    await conn.ExecuteAsync("DROP TABLE IF EXISTS UploadBatches;", transaction: tx);
                    await tx.CommitAsync();
                }
                finally
                {
                    await conn.ExecuteAsync("PRAGMA foreign_keys = ON;");
                }
            }

            await conn.ExecuteAsync(
                """
                CREATE INDEX IF NOT EXISTS IX_Transactions_Date ON Transactions(TransactionDate);
                CREATE INDEX IF NOT EXISTS IX_Transactions_Category ON Transactions(CategoryId);
                CREATE INDEX IF NOT EXISTS IX_Transactions_Type ON Transactions(TransactionType);
                CREATE INDEX IF NOT EXISTS IX_Transactions_Merchant ON Transactions(CleanMerchantName);
                CREATE INDEX IF NOT EXISTS IX_Transactions_DedupHash ON Transactions(DedupHash);
                CREATE INDEX IF NOT EXISTS IX_Transactions_UpdatedAt ON Transactions(UpdatedAt);
                """);

            // Normalize any legacy TransactionType values ('Payment' or 'Transfer') to canonical 'CreditCardPayoff'
            await conn.ExecuteAsync(
                "UPDATE Transactions SET TransactionType = 'CreditCardPayoff' WHERE TransactionType IN ('Payment', 'Transfer');");
        }

        // 9. Ensure Settings table exists and seed default backup policy keys if absent
        await conn.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS Settings (
                Key       TEXT PRIMARY KEY,
                Value     TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            INSERT OR IGNORE INTO Settings (Key, Value) VALUES
                ('BackupAutoMode', 'Coalesced'),
                ('BackupDebounceSeconds', '30'),
                ('BackupMinIntervalMinutes', '10'),
                ('MaxRetainedBackups', '15');
            """);

        // If Categories or Transactions already exist on an upgraded database, mark DefaultTaxonomySeeded = '1'
        // so deleted default categories/rules never resurrect on subsequent startups.
        if (catColumns.Count > 0)
        {
            int existingCats = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Categories;");
            int existingTxs = txColumns.Count > 0
                ? await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Transactions;")
                : 0;
            if (existingCats > 0 || existingTxs > 0)
            {
                await conn.ExecuteAsync(
                    "INSERT OR IGNORE INTO Settings (Key, Value) VALUES ('DefaultTaxonomySeeded', '1');");
            }
        }
    }
}

