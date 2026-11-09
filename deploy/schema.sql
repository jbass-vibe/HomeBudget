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
    CategoryId              INTEGER PRIMARY KEY AUTOINCREMENT,
    Name                    TEXT    NOT NULL UNIQUE,
    IsBigBoxMerchantCategory INTEGER NOT NULL DEFAULT 0,
    MonthlyBudget           NUMERIC NOT NULL DEFAULT 0.00,
    SortOrder               INTEGER NOT NULL DEFAULT 100,
    ColorHex                TEXT    NOT NULL DEFAULT '#3B82F6',
    IsActive                INTEGER NOT NULL DEFAULT 1,
    CreatedAt               TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS CategorizationRules (
    RuleId              INTEGER PRIMARY KEY AUTOINCREMENT,
    MatchPattern        TEXT    NOT NULL,
    IsRegex             INTEGER NOT NULL DEFAULT 0,
    CategoryId          INTEGER NOT NULL,
    DefaultSubcategory  TEXT    NULL,
    Priority            INTEGER NOT NULL DEFAULT 100,
    CreatedAt           TEXT    NOT NULL DEFAULT (datetime('now')),
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
    IsCategoryManuallyLocked INTEGER NOT NULL DEFAULT 0,
    ParentTransactionId      INTEGER NULL,
    DedupHash                TEXT    NOT NULL,
    UploadBatchId            TEXT    NULL,
    Notes                    TEXT    NULL,
    CreatedAt                TEXT    NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (AccountId)  REFERENCES Accounts(AccountId),
    FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS DeletedTransactionHashes (
    DedupHash       TEXT PRIMARY KEY,
    RawDescription  TEXT NOT NULL,
    TransactionDate TEXT NOT NULL,
    Amount          NUMERIC NOT NULL,
    DeletedAt       TEXT NOT NULL DEFAULT (datetime('now'))
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

-- Default Seed Accounts (Only inserted if Accounts table is empty)
INSERT INTO Accounts (AccountName, Institution, AccountType, IsActive)
SELECT 'Wells Fargo Household Checking', 'WellsFargo', 'Checking', 1
WHERE NOT EXISTS (SELECT 1 FROM Accounts);

INSERT INTO Accounts (AccountName, Institution, AccountType, IsActive)
SELECT 'Chase Household Credit Card', 'Chase', 'CreditCard', 1
WHERE (SELECT COUNT(*) FROM Accounts) = 1;

-- Default Seed Categories
INSERT OR IGNORE INTO Categories (Name, IsBigBoxMerchantCategory, MonthlyBudget, SortOrder, ColorHex, IsActive) VALUES
('Housing & Utilities', 0, 3200.00, 10, '#2563EB', 1),
('Groceries',           0, 1000.00, 20, '#10B981', 1),
('Costco',              1,  600.00, 30, '#E11D48', 1),
('Amazon',              1,  400.00, 40, '#F59E0B', 1),
('Target',              1,  300.00, 50, '#DC2626', 1),
('Walmart',             1,  200.00, 60, '#0284C7', 1),
('Dining & Coffee',     0,  500.00, 70, '#8B5CF6', 1),
('Transportation & Gas',0,  350.00, 80, '#14B8A6', 1),
('Subscriptions',       0,  150.00, 90, '#6366F1', 1),
('Healthcare',          0,  250.00, 100, '#EC4899', 1),
('Shopping & Household',0,  400.00, 110, '#0EA5E9', 1),
('Entertainment & Misc',0,  300.00, 120, '#64748B', 1);

-- Default Settings
INSERT OR IGNORE INTO Settings (Key, Value) VALUES
('PersistentMonthlyIncome', '10000.00'),
('GooglePayrollMatchPattern', 'GOOGLE'),
('CurrentMonthIncomeMode', 'Auto'),
('GeminiApiKey', '');
