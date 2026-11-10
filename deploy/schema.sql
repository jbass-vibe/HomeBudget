PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;

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

-- Default Seed Accounts
INSERT INTO Accounts (AccountName, Institution, AccountType, IsActive)
SELECT 'Wells Fargo Household Checking', 'WellsFargo', 'Checking', 1
WHERE NOT EXISTS (SELECT 1 FROM Accounts WHERE Institution = 'WellsFargo');

INSERT INTO Accounts (AccountName, Institution, AccountType, IsActive)
SELECT 'Chase Household Credit Card', 'Chase', 'CreditCard', 1
WHERE NOT EXISTS (SELECT 1 FROM Accounts WHERE Institution = 'Chase');

INSERT INTO Accounts (AccountName, Institution, AccountType, IsActive)
SELECT 'American Express Credit Card', 'Amex', 'CreditCard', 1
WHERE NOT EXISTS (SELECT 1 FROM Accounts WHERE Institution IN ('Amex', 'AmericanExpress', 'American Express'));

INSERT INTO Accounts (AccountName, Institution, AccountType, IsActive)
SELECT 'Citibank Credit Card', 'Citibank', 'CreditCard', 1
WHERE NOT EXISTS (SELECT 1 FROM Accounts WHERE Institution IN ('Citibank', 'Citi'));


-- Default Settings
INSERT OR IGNORE INTO Settings (Key, Value) VALUES
('PersistentMonthlyIncome', '10000.00'),
('GooglePayrollMatchPattern', 'GOOGLE'),
('CurrentMonthIncomeMode', 'Auto'),
('GeminiApiKey', ''),
('BackupAutoMode', 'Coalesced'),
('BackupDebounceSeconds', '30'),
('BackupMinIntervalMinutes', '10'),
('MaxRetainedBackups', '15');

-- Default Seed Categories (Gated by DefaultTaxonomySeeded so deleted defaults never resurrect on restart)
INSERT OR IGNORE INTO Categories (Name, IsBigBoxMerchantCategory, MonthlyBudget, SortOrder, ColorHex, IsActive)
SELECT Name, IsBigBoxMerchantCategory, MonthlyBudget, SortOrder, ColorHex, IsActive
FROM (
    SELECT 'Housing & Utilities'  AS Name, 0 AS IsBigBoxMerchantCategory, 3200.00 AS MonthlyBudget,  10 AS SortOrder, '#2563EB' AS ColorHex, 1 AS IsActive UNION ALL
    SELECT 'Groceries',                    0,                             1000.00,                   20,              '#10B981',            1 UNION ALL
    SELECT 'Costco',                       1,                              600.00,                   30,              '#E11D48',            1 UNION ALL
    SELECT 'Amazon',                       1,                              400.00,                   40,              '#F59E0B',            1 UNION ALL
    SELECT 'Target',                       1,                              300.00,                   50,              '#DC2626',            1 UNION ALL
    SELECT 'Walmart',                      1,                              200.00,                   60,              '#0284C7',            1 UNION ALL
    SELECT 'Dining & Coffee',              0,                              500.00,                   70,              '#8B5CF6',            1 UNION ALL
    SELECT 'Transportation & Gas',         0,                              350.00,                   80,              '#14B8A6',            1 UNION ALL
    SELECT 'Subscriptions',                0,                              150.00,                   90,              '#6366F1',            1 UNION ALL
    SELECT 'Healthcare',                   0,                              250.00,                  100,              '#EC4899',            1 UNION ALL
    SELECT 'Shopping & Household',         0,                              400.00,                  110,              '#0EA5E9',            1 UNION ALL
    SELECT 'Entertainment & Misc',         0,                              300.00,                  120,              '#64748B',            1
)
WHERE NOT EXISTS (SELECT 1 FROM Settings WHERE Key = 'DefaultTaxonomySeeded');

-- Default Seed Auto-Categorization Rules (Gated by DefaultTaxonomySeeded)
INSERT INTO CategorizationRules (MatchPattern, IsRegex, CategoryId, Priority)
SELECT r.Pattern, 0, c.CategoryId, r.Priority
FROM (
    SELECT 'COSTCO'     AS Pattern, 'Costco'               AS CatName, 10 AS Priority UNION ALL
    SELECT 'AMZN',                  'Amazon',                          15 UNION ALL
    SELECT 'AMAZON',                'Amazon',                          15 UNION ALL
    SELECT 'TARGET',                'Target',                          20 UNION ALL
    SELECT 'WAL-MART',              'Walmart',                         25 UNION ALL
    SELECT 'WALMART',               'Walmart',                         25 UNION ALL
    SELECT 'WHOLEFDS',              'Groceries',                       30 UNION ALL
    SELECT 'TRADER JOE',            'Groceries',                       30 UNION ALL
    SELECT 'SAFEWAY',               'Groceries',                       30 UNION ALL
    SELECT 'MORTGAGE',              'Housing & Utilities',             40 UNION ALL
    SELECT 'PG&E',                  'Housing & Utilities',             40 UNION ALL
    SELECT 'CHEVRON',               'Transportation & Gas',            50 UNION ALL
    SELECT 'SHELL',                 'Transportation & Gas',            50 UNION ALL
    SELECT 'NETFLIX',               'Subscriptions',                   60 UNION ALL
    SELECT 'COFFEE',                'Dining & Coffee',                 70
) r
JOIN Categories c ON c.Name = r.CatName COLLATE NOCASE
WHERE NOT EXISTS (SELECT 1 FROM CategorizationRules)
  AND NOT EXISTS (SELECT 1 FROM Settings WHERE Key = 'DefaultTaxonomySeeded');

INSERT OR IGNORE INTO Settings (Key, Value) VALUES ('DefaultTaxonomySeeded', '1');

