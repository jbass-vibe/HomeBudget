-- 1. Settings Table
CREATE TABLE IF NOT EXISTS Settings (
    Key TEXT PRIMARY KEY,
    Value TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);

-- 2. IncomeOverrides Table
CREATE TABLE IF NOT EXISTS IncomeOverrides (
    YearMonth TEXT PRIMARY KEY CHECK (YearMonth GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]'),
    IncomeAmount NUMERIC NOT NULL,
    Notes TEXT
);

-- 3. Accounts Table
CREATE TABLE IF NOT EXISTS Accounts (
    AccountId INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountName TEXT NOT NULL UNIQUE,
    Institution TEXT NOT NULL CHECK (Institution IN ('Chase', 'WellsFargo')),
    AccountType TEXT NOT NULL CHECK (AccountType IN ('CreditCard', 'Checking')),
    IsActive INTEGER NOT NULL DEFAULT 1
);

-- 4. Categories Table
CREATE TABLE IF NOT EXISTS Categories (
    CategoryId INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL UNIQUE,
    IsBigBoxMerchantCategory INTEGER NOT NULL DEFAULT 0,
    MonthlyBudget NUMERIC NOT NULL DEFAULT 0,
    SortOrder INTEGER NOT NULL DEFAULT 100,
    ColorHex TEXT NOT NULL DEFAULT '#4F46E5',
    IsActive INTEGER NOT NULL DEFAULT 1
);

-- 5. CategorizationRules Table
CREATE TABLE IF NOT EXISTS CategorizationRules (
    RuleId INTEGER PRIMARY KEY AUTOINCREMENT,
    MatchPattern TEXT NOT NULL,
    IsRegex INTEGER NOT NULL DEFAULT 0,
    CategoryId INTEGER NOT NULL,
    DefaultSubcategory TEXT NULL,
    Priority INTEGER NOT NULL DEFAULT 100,
    FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_CategorizationRules_Priority ON CategorizationRules(Priority ASC);

-- 6. UploadBatches Table
CREATE TABLE IF NOT EXISTS UploadBatches (
    UploadBatchId INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountId INTEGER NOT NULL,
    FileName TEXT NOT NULL,
    UploadedAt TEXT NOT NULL DEFAULT (datetime('now')),
    ImportedRowCount INTEGER NOT NULL DEFAULT 0,
    SkippedDuplicateCount INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (AccountId) REFERENCES Accounts(AccountId)
);

-- 7. Transactions Table
CREATE TABLE IF NOT EXISTS Transactions (
    TransactionId INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountId INTEGER NOT NULL,
    TransactionDate TEXT NOT NULL,
    PostDate TEXT NULL,
    RawDescription TEXT NOT NULL,
    CleanMerchantName TEXT NOT NULL,
    Amount NUMERIC NOT NULL,
    TransactionType TEXT NOT NULL CHECK (
        TransactionType IN ('Expense', 'Refund', 'CreditCardPayoff', 'IncomeDeposit', 'IgnoredTransfer')
    ),
    CategoryId INTEGER NULL,
    Subcategory TEXT NULL,
    IsCategoryManuallyLocked INTEGER NOT NULL DEFAULT 0,
    ParentTransactionId INTEGER NULL,
    DedupHash TEXT NOT NULL,
    UploadBatchId INTEGER NULL,
    Notes TEXT NULL,
    FOREIGN KEY (AccountId) REFERENCES Accounts(AccountId),
    FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId) ON DELETE SET NULL,
    FOREIGN KEY (ParentTransactionId) REFERENCES Transactions(TransactionId) ON DELETE CASCADE,
    FOREIGN KEY (UploadBatchId) REFERENCES UploadBatches(UploadBatchId)
);

CREATE INDEX IF NOT EXISTS IX_Transactions_TransactionDate ON Transactions(TransactionDate);
CREATE INDEX IF NOT EXISTS IX_Transactions_DedupHash ON Transactions(DedupHash);
CREATE INDEX IF NOT EXISTS IX_Transactions_CategoryId_Date ON Transactions(CategoryId, TransactionDate);
CREATE INDEX IF NOT EXISTS IX_Transactions_Type_Date ON Transactions(TransactionType, TransactionDate);

-- 8. DeletedTransactionHashes Table
CREATE TABLE IF NOT EXISTS DeletedTransactionHashes (
    DedupHash TEXT PRIMARY KEY,
    RawDescription TEXT NOT NULL,
    TransactionDate TEXT NOT NULL,
    Amount NUMERIC NOT NULL,
    DeletedAt TEXT NOT NULL DEFAULT (datetime('now'))
);

-- SEED DATA
INSERT OR IGNORE INTO Settings (Key, Value) VALUES
    ('PersistentMonthlyIncome', '10000.00'),
    ('GooglePayrollMatchPattern', 'GOOGLE'),
    ('CurrentMonthIncomeMode', 'Auto'),
    ('GeminiApiKey', '');

INSERT OR IGNORE INTO Accounts (AccountName, Institution, AccountType) VALUES
    ('Wells Fargo Checking', 'WellsFargo', 'Checking'),
    ('Chase Credit Card', 'Chase', 'CreditCard');

INSERT OR IGNORE INTO Categories (Name, IsBigBoxMerchantCategory, MonthlyBudget, SortOrder, ColorHex) VALUES
    ('Housing & Utilities', 0, 3000.00, 10, '#1E40AF'),
    ('Groceries',           0, 800.00,  20, '#15803D'),
    ('Costco',              1, 500.00,  30, '#B91C1C'),
    ('Amazon',              1, 400.00,  40, '#D97706'),
    ('Target',              1, 300.00,  50, '#DC2626'),
    ('Dining & Bars',       0, 500.00,  60, '#EA580C'),
    ('Auto & Gas',          0, 300.00,  70, '#4338CA'),
    ('Home & Garden',       0, 250.00,  80, '#0F766E'),
    ('Entertainment',       0, 200.00,  90, '#7E22CE'),
    ('Health & Medical',    0, 200.00, 100, '#BE185D'),
    ('Travel',              0, 300.00, 110, '#0369A1'),
    ('Subscriptions',       0, 150.00, 120, '#4B5563');

INSERT OR IGNORE INTO CategorizationRules (MatchPattern, IsRegex, CategoryId, Priority)
SELECT 'COSTCO', 0, CategoryId, 1 FROM Categories WHERE Name = 'Costco'
  AND NOT EXISTS (SELECT 1 FROM CategorizationRules WHERE MatchPattern = 'COSTCO');

INSERT OR IGNORE INTO CategorizationRules (MatchPattern, IsRegex, CategoryId, Priority)
SELECT 'AMZN', 0, CategoryId, 2 FROM Categories WHERE Name = 'Amazon'
  AND NOT EXISTS (SELECT 1 FROM CategorizationRules WHERE MatchPattern = 'AMZN');

INSERT OR IGNORE INTO CategorizationRules (MatchPattern, IsRegex, CategoryId, Priority)
SELECT 'AMAZON', 0, CategoryId, 3 FROM Categories WHERE Name = 'Amazon'
  AND NOT EXISTS (SELECT 1 FROM CategorizationRules WHERE MatchPattern = 'AMAZON');

INSERT OR IGNORE INTO CategorizationRules (MatchPattern, IsRegex, CategoryId, Priority)
SELECT 'TARGET', 0, CategoryId, 4 FROM Categories WHERE Name = 'Target'
  AND NOT EXISTS (SELECT 1 FROM CategorizationRules WHERE MatchPattern = 'TARGET');
