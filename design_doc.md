# Local Home Budgeting & Cashflow System — Design Document (v2.9)

**Document Version:** 2.9 (Security-Hardened Revision)  
**Target Platform:** Windows 11 IIS + ASP.NET Core (.NET 10 Minimal APIs) + SQLite (WAL Mode)  
**Network Scope:** Strictly LAN-Only (`LocalSubnet` via Windows Defender Firewall, **Mandatory HTTPS Port 8443**)  
**Primary Security Roles & Fixed Household Accounts:**
- **`User` Role (`user`, Mobile-First on iPhone or Desktop):** Configurable `DisplayName` (cosmetic only; unused for authorization). Reviews monthly budgets, explores the Cashflow Sankey visualization, manages categories (add/edit/delete/merge), recategorizes expenses with both forward rule creation (`applyMovingForward`) and past expense propagation (`applyToPast`), views and adds/edits/deletes individual auto-categorization rules, accesses the Transaction Inspector in read-only mode (filter, search, view, and recategorize without edit/split/delete actions), and manages own passphrase. Supports configurable 90-day persistent ("Remember this device") or browser-session cookies over mandatory HTTPS.
- **`Admin` Role (`admin`, Desktop/Host Administration):** Configurable `DisplayName`. Full system control: multi-file CSV statement ingestion (Chase, Amex, Citi & Wells Fargo), raw transaction editing/splitting/deleting, source account management and renaming, full category/rule topology with bulk back-propagation dry-run and commit, income settings & overrides, local SQLite backup/restore lifecycle (with recent-password step-up confirmation), clear history / reset all (with step-up confirmation), fixed-account user administration (password reset, unlock, enable/disable), and redacted security audit log inspection.

> [!NOTE]
> **Authentication, Transport Security & Role-Based Access Control (RBAC):** Transitioning from the v2.0–v2.8 open LAN trust model, v2.9 mandates LAN HTTPS (`:8443`), deny-by-default server-side authorization (`FallbackPolicy` with `AdminOnly` vs `Authenticated`), cookie authentication backed by DPAPI-encrypted/ACL-restricted Data Protection keys, protected one-time first-run bootstrap setup, partitioned login rate limiting + atomic lockout, CSRF/Origin/same-origin CORS enforcement, one-time break-glass admin recovery, and structured security audit logging.

---

## 1. Summary of Requirements & Architecture Evolution (v2.0 $\rightarrow$ v2.9)

### 1.0 Version Changelog
| Version | Summary of Architectural & Functional Changes |
| :--- | :--- |
| **v2.0 – v2.1** | Initial .NET 10 + SQLite WAL architecture, Chase/Wells Fargo CSV staging (`StagingSessions`), canonical `TransactionType` vocabulary, immutable `DedupHash`, Cashflow Sankey, and iPhone-responsive UI. |
| **v2.2** | Unified multi-mode list sorting (`sortCategories`, `sortTransactions`, `UpdatedAt` tracking) across all dropdowns, Sankey nodes, tables, and drill-downs with `localStorage` persistence. |
| **v2.3** | Smart Coalesced SQLite Backup Policy (`Coalesced`, `MajorOnly`, `ManualOnly`, `EveryChange`) with sliding debounce + minimum cooldown timers and Admin backup telemetry/pruning. |
| **v2.4** | Verified in-app database restore (from local `Backups/` or external `.db` upload with `PRAGMA integrity_check`) and Clear Transaction History (`POST /api/transactions/clear-history`). |
| **v2.5** | Simplified top-level Tabular Cashflow Breakdown (removing top-level per-account columns while retaining dashboard account spend pills and drill-downs) and Reset All Information (`POST /api/admin/reset-all`). |
| **v2.6** | Unified canonical schema/seed contract (`DefaultTaxonomySeeded` guard preventing seed resurrection on restart), pre-schema migration ordering for v1 database upgrades (removing legacy `UploadBatches` FK and `Accounts` `CHECK` constraints), preserved backup retention policy across resets, fail-fast safety backup verification, account-scoped tombstones (`DeletedTransactionHashes.AccountId`), exclusive lifecycle mutex across Reset/Restore/Clear/Backup, and inline triage merchant group expansion + `#backPropSortSelect`. |
| **v2.7** | Added full CSV statement upload support for **American Express (`Amex`)** and **Citibank (`Citibank`)** credit cards (including institution routing, CSV header auto-detection, sign/split-column normalization, checking-account payoff detection, and idempotently seeded default accounts). |
| **v2.8** | Added source account and credit card renaming capability (`PUT /api/accounts/{id}`) with synchronized updates across CSV upload view and Admin panel, preserving foreign-key transaction join integrity and automated backup snapshots (`account_rename`). |
| **v2.9** | Security-Hardened Role-Based User Authentication (RBAC) & Mandatory LAN HTTPS: Neutral fixed two-account architecture (`admin`/`Admin`, `user`/`User`), mandatory TLS/HTTPS (`:8443`, `CookieSecurePolicy.Always`, HSTS), host-token-protected atomic setup, 12–128 char passphrase policy + `IdentityV3` rehash-on-login, partitioned rate limiting + atomic lockout + generic errors, `RememberMe` persistent (90d/180d max) vs session cookies, deny-by-default `FallbackPolicy`, Origin/CSRF/same-origin CORS + security headers + `no-store`, DPAPI/ACL-protected Data Protection keys, one-time break-glass recovery, `SecurityAuditLogs`, step-up confirmation on Reset/Restore, and authoritative all-users identity preservation across restore/reset. |

### 1.1 Core Product Capabilities
1. **Full Category Lifecycle & Dual-Persona Access (Add, Edit, Delete):**
   - Both the **User (iPhone/Mobile)** and **Administrator (Desktop)** can **add, edit, and delete** categories from any category management surface or inline during expense categorization.
   - Deleting a category supports either reassigning its existing transactions/rules to a replacement category or setting those transactions back to `Uncategorized` (`CategoryId = NULL`, `CategorizationSource = 'Uncategorized'`, `IsCategoryManuallyLocked = 0`).

2. **Expense Recategorization with Non-Mutually Exclusive Forward & Historical Propagation Prompts:**
   - Whenever a user or administrator categorizes an uncategorized expense or recategorizes an existing expense, the UI presents an interactive confirmation prompt with two independent (non-mutually exclusive) checkboxes:
     - **Option A (Forward / Moving Forward):** *"Categorize similar expenses based on this change moving forward?"* — Creates or updates a persistent `CategorizationRule` (matched on normalized merchant name / pattern) so all future CSV uploads automatically assign this category.
     - **Option B (Historical / Past):** *"Recategorize similar expenses in the past?"* — Immediately runs a retroactive back-propagation query across existing transactions matching the merchant/pattern and updates their `CategoryId`, `CategorizationSource = 'Rule'`, and `UpdatedAt = datetime('now')` (with optional override for previously locked rows).
   - Users can check **neither** (one-off change for this transaction only), **Option A only**, **Option B only**, or **both Option A and Option B** simultaneously.

3. **Administrator Income & Expense Editing, Notes & Deletion with Multi-Criteria Filtering:**
   - The Administrator can **edit, split, or delete both Income (`IncomeDeposit`) and Expense (`Expense` / `Refund` / `CreditCardPayoff`) transactions** from the database via the **Transaction Inspector**.
   - Real-time, combinable filters include:
     - **Date Range:** Start Date & End Date pickers (plus quick presets: This Month, Last Month, Last 3M, YTD, All Dates).
     - **Transaction Type:** Canonical vocabulary (`All`, `Expense`, `IncomeDeposit`, `Refund`, `CreditCardPayoff`).
     - **Category:** Specific category dropdown, plus an **`Uncategorized Only`** quick-toggle pill.
     - **Account:** Filter by specific bank/credit card account.
     - **Amount Range:** Optional `Min Amount` / `Max Amount` filter.
     - **Search Query:** Instant substring filter across `RawDescription`, `CleanMerchantName`, `Subcategory`, and `Notes`.
   - When deleting an income or expense record (individually or in bulk), the Administrator can choose whether to record its `DedupHash` and `AccountId` in `DeletedTransactionHashes` (Tombstone table, enabled by default) so future overlapping CSV uploads do not resurrect the deleted entry.

4. **Visualization Page (`Uncategorized` Triage, Simplified Top-Level Category Table & Account Spend Summary):**
   - On the **Cashflow & Sankey Visualization** page, if any expenses in the active time window are marked **`Uncategorized`**, the `Uncategorized` node in the Sankey diagram and a dedicated **"Uncategorized Expenses Triage"** card below the diagram are highlighted.
   - Supports toggling between **Merchant-Grouped View** (aggregating repeated uncategorized charges by `CleanMerchantName` with item counts, group subtotals, and click-to-expand inline charge rows) and **Chronological List View**.
   - The **Tabular Cashflow Breakdown** table keeps top-level category summary rows clean and focused (`Category`, `Net Spend`, `% of Spend`, `% of Income`, `Manage`) without separate `"Wells Fargo Checking"` and `"Chase Credit Card"` columns, while keeping account-level spend totals in the dashboard Account Spend Summary and inside expanded category drill-downs.

5. **Comprehensive List Management & Multi-Mode Sorting (v2.2 $\rightarrow$ v2.6):**
   - Every major list, dropdown menu, visualization node column, dry-run preview, and table across the application provides consistent, user-controllable sorting options—including **Alphabetical (A $\rightarrow$ Z / Z $\rightarrow$ A)**, **Recent Changes (`UpdatedAt DESC`)**, **Amount / Spend (High $\rightarrow$ Low / Low $\rightarrow$ High)**, and **Custom / Priority Order**.

6. **Smart Coalesced SQLite Backup Policy, Full Database Restore, Clear History & Clean-Slate Reset (v2.3 $\rightarrow$ v2.6):**
   - **Preventing Backup Sprawl (Debounce + Cooldown + Tiered Triggers):** All category, account, rule, budget, settings, and transaction mutations route through `BackupService.RequestBackupAsync`. By default (`BackupAutoMode = 'Coalesced'`), rapid interactive edits are coalesced across a sliding quiet window (`BackupDebounceSeconds = 30s`) and a minimum interval cooldown (`BackupMinIntervalMinutes = 10m`) so a 20-item categorization session produces **one** consolidated snapshot instead of 20 separate `.db` files. Nested duplicate snapshots inside composite service operations are also eliminated.
   - **Full Admin Backup & Database Restore Suite (`topologyView.js`):** Allows the Administrator to configure auto-backup mode (`Coalesced`, `MajorOnly`, `ManualOnly`, `EveryChange`), retention count (`MaxRetainedBackups`, default `15`), debounce/cooldown timers, flush pending coalesced backups on demand, sort/inspect snapshots with trigger-reason badges, delete individual or bulk-selected snapshots, prune old snapshots, and safely **restore** the live database either from any local snapshot (`POST /api/backups/restore/{fileName}`) or by uploading an external `.db` snapshot file (`POST /api/backups/restore-upload`)—with automatic `pre_restore` safety backup and `PRAGMA integrity_check` verification.
   - **Clear Transaction History (`POST /api/transactions/clear-history`) & Reset All Information (`POST /api/admin/reset-all`):**
     - **Clear Transaction History:** Wipes imported transactions (and optionally account-scoped `DeletedTransactionHashes` tombstones and `StagingSessions`) while preserving all Categories, Auto-Categorization Rules, Accounts, Monthly Budgets, and Income Settings, with a pre-checked option to create a verified `pre_clear_history` backup first.
     - **Reset All Information (Start from Clean History):** Resets the entire live database (dropping and recreating clean v2.6 tables under an exclusive lifecycle mutex) via a confirmation modal (`openResetAllDatabaseModal`), while **strictly preserving all SQLite backup databases in `Backups/` and preserving the user's configured backup retention policy (`MaxRetainedBackups`, `BackupAutoMode`, debounce/cooldown settings)** so future snapshots never prune existing backups unexpectedly.

### 1.2 Operational Safety & Data Integrity Refinements
1. **Canonical Transaction Type Vocabulary:**
   - Standardized across database schema, C# services, API validation, and frontend views around four canonical values:
     - `'Expense'` (Positive amount: household spending outflow)
     - `'Refund'` (Negative amount: merchant return, credit, or non-payroll reimbursement netted against spending)
     - `'IncomeDeposit'` (Positive amount: verified payroll / household income deposit)
     - `'CreditCardPayoff'` (Positive amount: credit card payment or inter-account payoff transfer excluded from both spending and income totals to prevent double-counting)
2. **Pre-Schema Idempotent Upgrade & Single-Source Seed Contract (`DatabaseInitializer`):**
   - On startup, if an existing database already contains user tables, `DatabaseInitializer` runs `ApplyIdempotentSchemaMigrationsAsync` **before** executing index/seed statements in `schema.sql`. This guarantees older v1/v2.0 databases gain `CreatedAt`/`UpdatedAt`, `CategorizationSource`, `UploadBatchId`, and `DeletedTransactionHashes.AccountId`, and have legacy v1 `UploadBatches` foreign keys and `Accounts` `CHECK` constraints removed before any indexes on migrated columns are created.
   - Default category and rule seeding is gated by `Settings.Key = 'DefaultTaxonomySeeded'` (`'1'`), ensuring that once initialized (or after a clean reset with `ReseedDefaultCategoriesAndRules = false`), deleted default categories and rules never resurrect on server restart.
3. **Hardened Wells Fargo & Chase CSV Classification (`CsvParserService`):**
   - Prevents non-payroll positive checking credits (e.g., merchant refunds, Zelle/Venmo reimbursements, account transfers) from being misclassified as `IncomeDeposit`. Only positive checking rows matching `GooglePayrollMatchPattern` are auto-classified as `IncomeDeposit`; other non-payoff positive credits default to `Refund` and are flagged with `RequiresManualReview = true` during upload staging.
4. **SQLite-Persisted Upload Staging Sessions (`StagingSessions`):**
   - Persists staging sessions in the SQLite `StagingSessions` table (with automatic 24-hour expiration cleanup) so IIS application pool recycles or server restarts do not lose an in-progress CSV import review.
5. **Immutable Import Fingerprint (`DedupHash`) Contract:**
   - `DedupHash` is computed once at CSV parse time from the raw bank record (`AccountId|IsoDate|RawDescription|NormalizedAmount`) and is never mutated when the user edits `CleanMerchantName`, `TransactionDate`, `Amount`, `CategoryId`, `Subcategory`, or `Notes`.
6. **Frontend XSS Prevention (`escapeHtml` Rendering Contract) & Fail-Fast Verified Backups (`BackupService`):**
   - All externally sourced CSV fields and user-editable strings are escaped via `escapeHtml()` before DOM insertion.
   - `BackupService` verifies snapshot file existence and non-zero size after `VACUUM INTO`, coalesces burst edits, validates snapshot integrity (`PRAGMA integrity_check`) before any restore operation, and aborts any destructive Reset/Clear/Restore operation if a requested pre-operation safety snapshot fails.

---

## 2. System Architecture & Runtime Stack

| Layer | Technology / Specification |
| :--- | :--- |
| **Host OS & Web Server** | Windows 11 Pro/Home with IIS (`AspNetCoreModuleV2`, In-Process Hosting on **mandatory HTTPS Port `8443`**; HTTP `:8080` disabled or redirecting/refusing auth traffic) |
| **Backend Framework** | ASP.NET Core Minimal APIs targeting **.NET 10.0 (`net10.0`)** |
| **Authentication & RBAC** | `CookieAuthenticationDefaults.AuthenticationScheme` (`HomeBudget.Auth`, `CookieSecurePolicy.Always`, `RememberMe` 90d persistent / 180d max vs 12h/24h session), ASP.NET Core `PasswordHasher<User>` (`IdentityV3` + auto-rehash), DPAPI-protected persisted Data Protection keys, deny-by-default `FallbackPolicy` (`AdminOnly` / `Authenticated`) |
| **Web Security & Audit** | Partitioned `AddRateLimiter` (IP + username), atomic SQLite lockout counters, `CsrfAndOriginMiddleware` (`Origin`/`Referer` + `X-HomeBudget-Request: 1` + same-origin CORS), CSP / `no-store` security headers, `SecurityAuditService` (`SecurityAuditLogs`) |
| **Database Engine** | **SQLite 3.53+** (`Microsoft.Data.Sqlite` `10.0.0` + `SQLitePCLRaw.lib.e_sqlite3` `3.53.3`) |
| **Concurrency & Durability** | `PRAGMA journal_mode=WAL;`, `PRAGMA synchronous=NORMAL;`, `PRAGMA foreign_keys=ON;`, `PRAGMA busy_timeout=5000;` + `BackupService` lifecycle `SemaphoreSlim` mutex |
| **Data Access Layer** | **Dapper** (`2.1.35`) with custom `SqliteDecimalHandler` and `SqliteBoolHandler` for strict `.NET 10` type safety |
| **Frontend Architecture** | Zero-build Vanilla ES6 Modules (`app.js`, view modules, custom responsive SVG `sankeyChart.js`), centralized list-sorting helpers & `escapeHtml()` sanitization, cross-tab session sync, Mobile-First CSS |
| **Network & Transport** | Bound to `*:8443` (mandatory HTTPS with `mkcert` local CA certificate); Windows Firewall restricts inbound TCP to `LocalSubnet` on `Private` profile (defense-in-depth network boundary only) |

---

## 3. Database Schema & Canonical Seed Data (DDL v2.9)

Default Database file location: `C:\ProgramData\HomeBudget\budget.db` (configurable via `HomeBudget:DatabasePath`)  
Default Automated Backup directory: `<DatabaseDirectory>\Backups\` (e.g., `C:\ProgramData\HomeBudget\Backups\`, configurable via `HomeBudget:BackupDirectoryPath`)

### 3.1 Core Tables & Indexes (`deploy/schema.sql` & `DatabaseInitializer`)

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS Accounts (
    AccountId       INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountName     TEXT    NOT NULL,
    Institution     TEXT    NOT NULL, -- e.g. 'WellsFargo', 'Chase', or any custom institution
    AccountType     TEXT    NOT NULL, -- 'Checking', 'CreditCard', 'Savings', etc.
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
    UpdatedAt                TEXT    NOT NULL DEFAULT (datetime('now')) -- Tracks recent edits or category assignments
);

CREATE TABLE IF NOT EXISTS CategorizationRules (
    RuleId              INTEGER PRIMARY KEY AUTOINCREMENT,
    MatchPattern        TEXT    NOT NULL,
    IsRegex             INTEGER NOT NULL DEFAULT 0, -- 0 = Case-insensitive substring ('Contains'), 1 = Regex
    CategoryId          INTEGER NOT NULL,
    DefaultSubcategory  TEXT    NULL,
    Priority            INTEGER NOT NULL DEFAULT 100, -- Lower number = higher priority (evaluated first)
    CreatedAt           TEXT    NOT NULL DEFAULT (datetime('now')),
    UpdatedAt           TEXT    NOT NULL DEFAULT (datetime('now')), -- Tracks recent rule creation or modification
    FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS Transactions (
    TransactionId            INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountId                INTEGER NOT NULL,
    TransactionDate          TEXT    NOT NULL, -- ISO-8601 'YYYY-MM-DD'
    PostDate                 TEXT    NULL,
    RawDescription           TEXT    NOT NULL,
    CleanMerchantName        TEXT    NOT NULL,
    Amount                   NUMERIC NOT NULL, -- Positive = Expense / IncomeDeposit / CreditCardPayoff; Negative = Refund
    TransactionType          TEXT    NOT NULL, -- Canonical: 'Expense', 'Refund', 'IncomeDeposit', 'CreditCardPayoff'
    CategoryId               INTEGER NULL,     -- NULL = Uncategorized (for Expense/Refund)
    Subcategory              TEXT    NULL,
    CategorizationSource     TEXT    NOT NULL DEFAULT 'Uncategorized', -- 'Manual', 'Rule', 'Uncategorized'
    IsCategoryManuallyLocked INTEGER NOT NULL DEFAULT 0,
    ParentTransactionId      INTEGER NULL,
    DedupHash                TEXT    NOT NULL, -- Immutable import fingerprint
    UploadBatchId            TEXT    NULL,
    Notes                    TEXT    NULL,
    CreatedAt                TEXT    NOT NULL DEFAULT (datetime('now')),
    UpdatedAt                TEXT    NOT NULL DEFAULT (datetime('now')), -- Tracks recent edits or recategorization
    FOREIGN KEY (AccountId)  REFERENCES Accounts(AccountId),
    FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS DeletedTransactionHashes (
    DedupHash       TEXT    PRIMARY KEY,
    AccountId       INTEGER NULL, -- Scopes tombstones to the originating account for per-account Clear History
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
    YearMonth       TEXT PRIMARY KEY, -- 'YYYY-MM'
    IncomeAmount    NUMERIC NOT NULL,
    Notes           TEXT NULL,
    UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS Users (
    UserId               INTEGER PRIMARY KEY AUTOINCREMENT,
    Username             TEXT    NOT NULL UNIQUE COLLATE NOCASE, -- Immutable fixed logins: 'admin', 'user'
    PasswordHash         TEXT    NOT NULL,                       -- ASP.NET Core PasswordHasher IdentityV3 hash
    DisplayName          TEXT    NOT NULL,                       -- Configurable UI label (never used for authorization)
    Role                 TEXT    NOT NULL CHECK (Role IN ('Admin', 'User')),
    SecurityStamp        TEXT    NOT NULL DEFAULT '',            -- Rotated on password change, reset, deactivation, or recovery
    FailedLoginAttempts  INTEGER NOT NULL DEFAULT 0,             -- Atomically incremented failure counter
    LockoutEndUtc        TEXT    NULL,                           -- ISO-8601 lockout expiration (15 min after 5 fails)
    IsActive             INTEGER NOT NULL DEFAULT 1,             -- 0 = Disabled (immediately revokes sessions via ValidatePrincipal)
    LastLoginAt          TEXT    NULL,
    CreatedAt            TEXT    NOT NULL DEFAULT (datetime('now')),
    UpdatedAt            TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS SecurityAuditLogs (
    AuditLogId      INTEGER PRIMARY KEY AUTOINCREMENT,
    TimestampUtc    TEXT    NOT NULL DEFAULT (datetime('now')),
    EventType       TEXT    NOT NULL, -- LOGIN_SUCCESS, LOGIN_FAILURE, ACCOUNT_LOCKED, SETUP_COMPLETED, etc.
    Outcome         TEXT    NOT NULL, -- 'Success', 'Failure', 'Denied'
    ActorUserId     INTEGER NULL,
    ActorUsername   TEXT    NULL,
    TargetUsername  TEXT    NULL,
    SourceIp        TEXT    NULL,
    Details         TEXT    NULL      -- Strictly redacted summary (never contains passwords, hashes, cookies, or tokens)
);

CREATE INDEX IF NOT EXISTS IX_Transactions_Date ON Transactions(TransactionDate);
CREATE INDEX IF NOT EXISTS IX_Transactions_Category ON Transactions(CategoryId);
CREATE INDEX IF NOT EXISTS IX_Transactions_Type ON Transactions(TransactionType);
CREATE INDEX IF NOT EXISTS IX_Transactions_Merchant ON Transactions(CleanMerchantName);
CREATE INDEX IF NOT EXISTS IX_Transactions_DedupHash ON Transactions(DedupHash);
CREATE INDEX IF NOT EXISTS IX_Transactions_UpdatedAt ON Transactions(UpdatedAt);
CREATE INDEX IF NOT EXISTS IX_Users_Username ON Users(Username);
CREATE INDEX IF NOT EXISTS IX_SecurityAuditLogs_Timestamp ON SecurityAuditLogs(TimestampUtc);
```

### 3.2 Canonical Seed Data, `DefaultTaxonomySeeded` & `AuthInitialized` Guards
Both `deploy/schema.sql` and `DatabaseInitializer` share a single canonical seed specification:
1. **Default Household Accounts (seeded idempotently):**
   - `AccountId = 1`: `'Wells Fargo Household Checking'` (`Institution = 'WellsFargo'`, `AccountType = 'Checking'`)
   - `AccountId = 2`: `'Chase Household Credit Card'` (`Institution = 'Chase'`, `AccountType = 'CreditCard'`)
   - `AccountId = 3`: `'American Express Credit Card'` (`Institution = 'Amex'`, `AccountType = 'CreditCard'`)
   - `AccountId = 4`: `'Citibank Credit Card'` (`Institution = 'Citibank'`, `AccountType = 'CreditCard'`)
2. **Default Settings (`INSERT OR IGNORE INTO Settings`):**
   - `PersistentMonthlyIncome = '10000.00'`, `GooglePayrollMatchPattern = 'GOOGLE'`, `CurrentMonthIncomeMode = 'Auto'`, `GeminiApiKey = ''`
   - `BackupAutoMode = 'Coalesced'`, `BackupDebounceSeconds = '30'`, `BackupMinIntervalMinutes = '10'`, `MaxRetainedBackups = '15'`
   - `AuthInitialized = '0'` *(Flipped atomically to `'1'` during first-run setup; never cleared on reset or user-row edits so anonymous setup cannot re-open).*
3. **One-Time Starter Categories (12) & Auto-Categorization Rules (15) Gated by `DefaultTaxonomySeeded`:**
   - Starter categories (`Housing & Utilities`, `Groceries`, `Costco`, `Amazon`, `Target`, `Walmart`, `Dining & Coffee`, `Transportation & Gas`, `Subscriptions`, `Healthcare`, `Shopping & Household`, `Entertainment & Misc`) and 15 starter merchant rules (`COSTCO`, `AMZN`, `AMAZON`, `TARGET`, `WAL-MART`, `WALMART`, `WHOLEFDS`, `TRADER JOE`, `SAFEWAY`, `MORTGAGE`, `PG&E`, `CHEVRON`, `SHELL`, `NETFLIX`, `COFFEE`) are seeded **only** when `Settings` does not yet contain `Key = 'DefaultTaxonomySeeded'`.
   - Immediately after initial seeding (or after `ResetAllDatabaseAsync` even when `ReseedDefaultCategoriesAndRules = false`), `('DefaultTaxonomySeeded', '1')` is recorded in `Settings` so subsequent server restarts never resurrect deleted default categories or rules.

### 3.3 Idempotent Schema Upgrade & Startup Ordering Strategy (`DatabaseInitializer`)
On startup, `DatabaseInitializer.InitializeAsync()` inspects whether legacy tables already exist:
1. **Pre-Schema Migration on Existing Databases (Transactional):** If `Transactions` or `Accounts` already exists in `sqlite_master`, `ApplyIdempotentSchemaMigrationsAsync()` executes **before** running `schema.sql` inside an explicit transaction with automatic rollback on failure so that migrated columns (`UpdatedAt`, `CreatedAt`, `CategorizationSource`, `UploadBatchId`, `DeletedTransactionHashes.AccountId`) and security tables (`Users`, `SecurityAuditLogs`) exist before creating `IX_Transactions_UpdatedAt` or other indexes.
2. **Column Backfills & Table Normalization (`ApplyIdempotentSchemaMigrationsAsync`):**
   - Ensures `StagingSessions`, `Settings`, `Users`, and `SecurityAuditLogs` exist, creating missing identity columns idempotently (`SecurityStamp`, `FailedLoginAttempts`, `LockoutEndUtc`, `IsActive`, `LastLoginAt`), and prunes expired staging sessions older than 24 hours.
   - Adds missing `CreatedAt` and/or `UpdatedAt` columns to `Accounts`, `Categories`, `CategorizationRules`, `IncomeOverrides`, and `Transactions`, backfilling from existing timestamps or `TransactionDate`.
   - Adds missing `AccountId INTEGER NULL` to `DeletedTransactionHashes` and backfills `AccountId` from `DedupHash` or matching transactions where possible.
   - Adds missing `CategorizationSource` and `UploadBatchId` to `Transactions`, backfilling `CategorizationSource` (`'Manual'`, `'Rule'`, or `'Uncategorized'`).
   - Rebuilds `Accounts` under `PRAGMA foreign_keys = OFF` if a legacy v1 `CHECK (Institution IN ('Chase', 'WellsFargo'))` constraint is detected, allowing arbitrary institutions/account types.
   - Rebuilds `Transactions` under `PRAGMA foreign_keys = OFF` if a legacy v1 `UploadBatches` foreign key is present and drops the obsolete `UploadBatches` table.
   - Normalizes legacy `TransactionType` values (`'Payment'`, `'Transfer'`) to `'CreditCardPayoff'` and marks `DefaultTaxonomySeeded = '1'` on existing populated databases; if `Users` already contains rows on an upgraded DB, ensures `AuthInitialized = '1'`.

---

## 4. Detailed Functional Specifications

### 4.1 Category Lifecycle & Unified Recategorization Propagation

#### A. Category CRUD (Both User & Administrator)
- **Where Accessible:**
  1. **Categories, Rules & Backups View** (`topologyView.js`).
  2. **Monthly Budgets View** (`budgetsView.js`: `+ Add Category` button at top, plus `Edit / Delete` gear icon on each category card).
  3. **Cashflow & Sankey View** (`cashflowView.js`: `+ Add Category` button in header and triage panel, `Edit / Delete` button in tabular breakdown, and `+ Create New Category...` inside quick-categorize dropdowns).
  4. **Transaction Inspector** (`inspectorView.js`: `+ Add New Category...` option inside category dropdowns and the Categorize modal).
- **Operations & Timestamp Tracking:**
  - **Add Category (`POST /api/categories`):** Validates non-empty `Name` and `MonthlyBudget >= 0`. Sets `CreatedAt = datetime('now')`, `UpdatedAt = datetime('now')`, and requests a `"category_create"` backup via `BackupService.RequestBackupAsync("category_create", isMajorOperation: false)`.
  - **Edit Category (`PUT /api/categories/{id}`):** Updates `Name`, `IsBigBoxMerchantCategory`, `MonthlyBudget`, `SortOrder`, `ColorHex`, and sets `UpdatedAt = datetime('now')`. Requests a `"category_update"` backup via `BackupService.RequestBackupAsync("category_update", isMajorOperation: false)`.
  - **Delete Category (`DELETE /api/categories/{id}?replacementCategoryId={optionalId}`):**
    - If `replacementCategoryId` is provided: atomically reassigns all `Transactions` and `CategorizationRules` from `id` to `replacementCategoryId` (updating `UpdatedAt = datetime('now')` on the target category and affected rows), then deletes category `id`.
    - If `replacementCategoryId` is omitted: sets `CategoryId = NULL`, `CategorizationSource = 'Uncategorized'`, `IsCategoryManuallyLocked = 0`, and `UpdatedAt = datetime('now')` on affected `Transactions`, deletes rules tied to `id`, and deletes category `id`.
    - Requests a `"category_delete"` major backup via `BackupService.RequestBackupAsync("category_delete", isMajorOperation: true)`.

#### B. Expense Categorization / Recategorization with Forward & Historical Options
Whenever a user or administrator assigns or changes the category of an expense:
1. **Similarity Matching Pattern Resolution:**
   - Pre-fills the match pattern input with the transaction's `CleanMerchantName` and displays a live preview count from `GET /api/transactions/similar-count`.
2. **Non-Mutually Exclusive Propagation Options:**
   - **[Checkbox A] Apply to similar expenses moving forward (`applyMovingForward: bool`)** — Upserts a rule in `CategorizationRules` with `UpdatedAt = datetime('now')`.
   - **[Checkbox B] Recategorize similar expenses in the past (`applyToPast: bool`)** — Updates matching historical `Expense`/`Refund` rows with `CategoryId = targetCategoryId`, `CategorizationSource = 'Rule'`, and `UpdatedAt = datetime('now')`.
3. **Recent-Activity Touch on Target Category:**
   - Assigning a transaction to `targetCategoryId` also updates `Categories.UpdatedAt = datetime('now')` for `targetCategoryId`, so sorting category lists by **"Recent Changes"** surfaces both recently edited categories and categories that were most recently assigned to expenses.

---

### 4.2 Unified List Management & Multi-Mode Sorting Specification (v2.2 $\rightarrow$ v2.6)

To make navigating categories, visualizations, budgets, rules, backups, and transaction lists fast and consistent on both mobile (iPhone) and desktop, the system provides a **Unified List Sorting Architecture** across every list in the application. All sort preferences are persisted in `state` and `localStorage` via `setSortPreference(stateKey, value)` so user choices survive view navigation and page reloads. In addition, expanded category drill-downs (`state.expandedCashflowCategories` and `state.expandedBudgetCategories`) remain open when changing sort modes or recategorizing items.

#### A. Category Dropdown Menus & Pickers (`sortCategories` Helper in `app.js`)
Applies consistently to all `<select>` category menus across the application:
- Unified Categorize Expense Modal (`app.js`)
- Cashflow Uncategorized Triage row/group dropdowns (`cashflowView.js`)
- CSV Upload Staging row category dropdowns (`uploadView.js`)
- Transaction Inspector filter bar, table row dropdowns, and Split Transaction modal (`inspectorView.js`)
- Category Delete Reassignment & Category Merge modals (`app.js`, `topologyView.js`)

**Supported Category Dropdown Sort Modes (`state.categoryDropdownSort`):**
| Sort Mode Key | Label | Ordering Logic |
| :--- | :--- | :--- |
| `alpha_asc` *(Default)* | **Alphabetical (A $\rightarrow$ Z)** | `Name.localeCompare(b.Name)` (case-insensitive) so users can jump to any category by name in dropdown menus. |
| `alpha_desc` | **Alphabetical (Z $\rightarrow$ A)** | Reverse alphabetical `b.Name.localeCompare(a.Name)`. |
| `recent` | **Recent Changes** | `COALESCE(LastActivityAt, UpdatedAt, CreatedAt) DESC, Name ASC` — places categories that were most recently created, edited, or assigned to a transaction at the top of the dropdown. |
| `custom_order` | **Custom Sort Order** | `SortOrder ASC, Name ASC` — respects the manual numeric order configured in the Topology table. |
| `budget_desc` | **Highest Budget ($)** | `MonthlyBudget DESC, Name ASC` — places major household budget categories at the top. |

#### B. Unified Transaction List Sorting (`sortTransactions` Helper in `app.js`)
Every transaction list in the application shares the canonical `sortTransactions(transactions, sortMode)` helper so users have the exact same sorting options wherever transactions are displayed:

| Sort Mode Key | Label | Ordering Logic |
| :--- | :--- | :--- |
| `date_desc` *(Default)* | **Newest Date First** | `TransactionDate DESC, TransactionId DESC` |
| `date_asc` | **Oldest Date First** | `TransactionDate ASC, TransactionId ASC` |
| `recent` / `recent_changes` | **Recent Changes** | `COALESCE(UpdatedAt, CreatedAt, TransactionDate) DESC, TransactionId DESC` — surfaces recently categorized, edited, or split transactions first. |
| `amount_desc` | **Highest Amount ($)** | `ABS(Amount) DESC, TransactionDate DESC` |
| `amount_asc` | **Lowest Amount ($)** | `ABS(Amount) ASC, TransactionDate DESC` |
| `alpha_asc` / `merchant_asc` | **Merchant / Description (A $\rightarrow$ Z)** | `(CleanMerchantName \|\| RawDescription) COLLATE NOCASE ASC, TransactionDate DESC` |
| `alpha_desc` / `merchant_desc` | **Merchant / Description (Z $\rightarrow$ A)** | `(CleanMerchantName \|\| RawDescription) COLLATE NOCASE DESC, TransactionDate DESC` |
| `account_asc` | **Account (A $\rightarrow$ Z)** | `AccountName COLLATE NOCASE ASC, TransactionDate DESC` |

**Surfaces Powered by `sortTransactions`:**
1. **Cashflow & Sankey — Expanded Category Transaction Lists (`cashflowView.js`, `state.cashflowTransactionSort`):**
   - Controlled via the **`Sort Transactions:` (`#cashflowTxSortSelect`)** selector in the **Tabular Cashflow Breakdown** header **and** the inline **`.cat-detail-tx-sort`** selector inside each expanded category row (`#cat_detail_{idx}`).
   - Expanding any category row shows its constituent transactions ordered by `state.cashflowTransactionSort` (with date, merchant, account, optional subcategory/notes, amount, and a `Recategorize` button). Changing either the table-level or inline sort selector immediately re-orders all expanded category transaction lists while keeping the expanded rows open.
2. **Cashflow & Sankey — Uncategorized Expenses Triage (`cashflowView.js`, `state.triageSort`):**
   - Controlled via **`Sort Triage:` (`#triageSortSelect`)**, supporting all standard transaction sort modes (`amount_desc`, `amount_asc`, `date_desc`, `date_asc`, `recent`, `alpha_asc`, `alpha_desc`) plus **`count_desc` (Most Frequent Merchant)** when grouped by merchant. Merchant groups can also be expanded inline (`state.expandedCashflowMerchants`) to inspect individual charges sorted by `state.triageSort`.
3. **Monthly Budgets — Category Card Transaction Drill-Downs & Uncategorized Banner (`budgetsView.js`, `state.budgetsTransactionSort`):**
   - Controlled via **`Sort Transactions:` (`#budgetsTxSortSelect`)** in the Monthly Budgets toolbar **and** the inline **`.budget-drill-tx-sort`** selector inside each expanded category card (`#drill_{categoryId}`).
   - Applies to both the uncategorized expenses banner for the selected month and every category card's expandable transaction list, preserving open drill-downs across sort changes.
4. **CSV Upload Staging Review Table (`uploadView.js`, `state.uploadTransactionSort`):**
   - Controlled via **`Sort Staged Rows:` (`#uploadTxSortSelect`)**, supporting **`status_priority` (Review / Collisions First — Default)** as well as `date_desc`, `date_asc`, `amount_desc`, `amount_asc`, `alpha_asc`, and `alpha_desc`, while preserving any in-progress user edits in the staging table.
5. **Transaction Inspector (`inspectorView.js` & `GET /api/transactions`, `state.inspectorTransactionSort`):**
   - Controlled via **`Sort Transactions:` (`#fltSort`)**, persisted across sessions and evaluated in SQL (`ORDER BY`) across all matching pages.
6. **Rule Back-Propagation Dry-Run Preview (`topologyView.js`, `state.backPropSort`):**
   - Controlled via **`#backPropSortSelect`** inside the dry-run preview panel so the Administrator can sort proposed historical rule changes by `date_desc`, `amount_desc`, `alpha_asc`, or `category_asc` before committing.

#### C. Cashflow Sankey Visualization & Category Breakdown Sorting (`cashflowView.js` & `sankeyChart.js`)
The Sankey graph is organized into a 3-to-4-column flow: **Column 1 (`Household Income` / `Spending Outrun`) $\rightarrow$ Column 2 (`Total Spending` / `Net Savings`) $\rightarrow$ Column 3 (`Spending Categories`) $\rightarrow$ Column 4 (`Subcategories`, when enabled)**. On the **Cashflow & Sankey Visualization** page, the **`Sort Categories:` (`#cashflowCatSortSelect`)** selector in the page toolbar controls the vertical ordering of **both** the **Sankey Diagram Category Nodes (Column 3) & Subcategory Nodes (Column 4)** and the **Tabular Cashflow Breakdown category rows**:

| Sort Mode Key | Label | Ordering Logic in Sankey & Table |
| :--- | :--- | :--- |
| `spend_desc` *(Default)* | **Highest Spend ($)** | `NetSpend DESC, CategoryName ASC` — largest spending flows appear at the top of the Sankey diagram and table. |
| `spend_asc` | **Lowest Spend ($)** | `NetSpend ASC, CategoryName ASC` — smallest positive spending flows appear first. |
| `alpha_asc` | **Alphabetical (A $\rightarrow$ Z)** | `CategoryName ASC` (with `Uncategorized` pinned at the bottom of the category list so named categories stay strictly A $\rightarrow$ Z). Subcategory nodes in Column 4 also sort alphabetically within each category. |
| `alpha_desc` | **Alphabetical (Z $\rightarrow$ A)** | `CategoryName DESC` (with `Uncategorized` pinned at the bottom). |
| `recent` | **Recent Changes** | Ordered by `LastActivityAt DESC` (the latest `UpdatedAt` across the category and its transactions in the window), surfacing categories that were recently recategorized or modified at the top of the visualization and table. |
| `custom_order` | **Custom Order** | Ordered by `Categories.SortOrder ASC, CategoryName ASC`. |

#### D. Monthly Budgets View Card Sorting (`budgetsView.js`)
In the **Monthly Budgets** header, **`Sort Categories:` (`#budgetsSortSelect`)** orders the budget progress cards:
1. **`custom_order` (Custom Order — Default):** `SortOrder ASC, Name ASC`.
2. **`alpha_asc` (Alphabetical A $\rightarrow$ Z):** `Name ASC`.
3. **`alpha_desc` (Alphabetical Z $\rightarrow$ A):** `Name DESC`.
4. **`recent` (Recent Changes):** Ordered by most recent category/transaction modification (`LastActivityAt DESC`).
5. **`pct_used_desc` (% Budget Used — High $\rightarrow$ Low):** Surfaces over-budget (`Red`) and warning (`Amber`) categories at the top.
6. **`spend_desc` (Highest Spent $):** `NetSpent DESC`.
7. **`remaining_asc` (Least Remaining $):** `Remaining ASC` (most over-budget or tightest remaining budget first).

#### E. Categories Topology, Auto-Categorization Rules & Backups Sorting (`topologyView.js`)
1. **Spending Category Topology Table (`#topologyCatSortSelect`):**
   - Sortable by **Custom Order (`custom_order`)**, **Alphabetical A–Z (`alpha_asc`)**, **Alphabetical Z–A (`alpha_desc`)**, **Recent Changes (`recent`)**, and **Highest Budget (`budget_desc`)**.
2. **Auto-Categorization Rules Table (`#topologyRuleSortSelect`):**
   - Sortable by **Evaluation Priority (`priority_asc`)**, **Match Pattern A–Z (`alpha_asc`)**, **Target Category A–Z (`category_asc`)**, and **Recent Changes (`recent`)**.
3. **SQLite Backup Snapshots Table (`#backupSortSelect`):**
   - Sortable by **Newest First (`date_desc`)**, **Oldest First (`date_asc`)**, **Largest Size (`size_desc`)**, **Smallest Size (`size_asc`)**, and **Trigger Reason A–Z (`reason_asc`)**.

---

### 4.3 CSV Ingestion, Bank Classification & Persistent Staging

#### A. Bank-Specific CSV Classification Rules (`CsvParserService`)
1. **Chase Credit Card (7-Column Headered CSV — `Transaction Date,Post Date,Description,Category,Type,Amount,Memo`):**
   - **Credit Card Payoff (`'CreditCardPayoff'`):** `Type` column equals `"Payment"` OR `Description` matches payment patterns (`"PAYMENT THANK YOU"`, `"AUTOMATIC PAYMENT"`, `"ONLINE PAYMENT"`, `"AUTOPAY"`). Stored with positive `Math.Abs(csvAmt)`.
   - **Expense (`'Expense'`):** `csvAmt < 0` (and not a payment). Stored with positive `Math.Abs(csvAmt)`.
   - **Refund (`'Refund'`):** `csvAmt > 0` (and not a payment). Stored with negative `-Math.Abs(csvAmt)`.

2. **American Express Credit Card (3- or 5+-Column Headered CSV — `Date,Description,Amount` or `Date,Description,Card Member,Account #,Amount`):**
   - **Credit Card Payoff (`'CreditCardPayoff'`):** `Description` matches payment patterns (`"ONLINE PAYMENT"`, `"AUTOPAY PAYMENT"`, `"MOBILE PAYMENT"`, `"PAYMENT - THANK YOU"`, `"PAYMENT RECEIVED"`). Stored with positive `Math.Abs(csvAmt)`.
   - **Expense (`'Expense'`):** `csvAmt > 0` (Amex exports purchases as positive numbers). Stored with positive `Math.Abs(csvAmt)`.
   - **Refund (`'Refund'`):** `csvAmt < 0` (and not a payment). Stored with negative `-Math.Abs(csvAmt)`.

3. **Citibank Credit Card (5-Column Split `Status,Date,Description,Debit,Credit` or Single `Amount` Headered CSV):**
   - **Credit Card Payoff (`'CreditCardPayoff'`):** `Description` matches payment patterns (`"ONLINE PAYMENT"`, `"AUTOPAY"`, `"CITI AUTOPAY"`, `"PAYMENT THANK YOU"`). Stored with positive `Math.Abs(amt)`.
   - **Expense (`'Expense'`):** Populated positive `Debit` column (or positive `Amount` when no `Credit` is present and not a payment). Stored with positive `Math.Abs(debitAmt)`.
   - **Refund (`'Refund'`):** Populated `Credit` column (or negative `Debit`/`Amount`, and not a payment). Stored with negative `-Math.Abs(creditAmt)`.

4. **Wells Fargo Checking (5-Column Headerless CSV):**
   - **Credit Card Payoff (`'CreditCardPayoff'`):** `Description` matches credit card payment patterns for Chase, American Express, Citibank, or generic card autopays (`"CHASE CREDIT CRD"`, `"AMEX EPAY"`, `"AMERICAN EXPRESS"`, `"CITI CARD"`, `"CITICARDS"`, `"CITI AUTOPAY"`, `"CITIBANK CRDT"`, `"CREDIT CARD"`). Stored with positive `Math.Abs(csvAmt)`.
   - **Verified Payroll / Income (`'IncomeDeposit'`):** `csvAmt > 0` AND `Description` matches the configured `GooglePayrollMatchPattern` (or explicit `"DIRECT DEP"` / `"PAYROLL"` keywords). Stored with positive `Math.Abs(csvAmt)`.
   - **Other Positive Checking Credits (`'Refund'` + Manual Review Flag):** `csvAmt > 0` that do **not** match payroll or credit card payoff patterns. Defaulted to `'Refund'` (`-Math.Abs(csvAmt)`) with `RequiresManualReview = true` in the staging table so the Administrator explicitly confirms the classification before committing.
   - **Expense (`'Expense'`):** `csvAmt < 0` (and not a credit card payoff). Stored with positive `Math.Abs(csvAmt)`.

#### B. Deduplication & Immutable Provenance (`DedupHash`)
- **Base Fingerprint:** `ComputeBaseHash(accountId, isoDate, rawDesc, normalizedAmt)` computes the first 24 hex characters of `SHA256("{accountId}|{isoDate}|{rawDesc.Trim().ToUpperInvariant()}|{Math.Abs(normalizedAmt):F2}")`.
- **Immutability Across Edits:** Subsequent edits to `TransactionDate`, `Amount`, `CleanMerchantName`, `TransactionType`, `CategoryId`, `Subcategory`, or `Notes` via the Transaction Inspector update `UpdatedAt = datetime('now')` but do **not** overwrite `DedupHash`.
- **Account-Scoped Tombstone Protection on Splits & Deletes:** Deleting a transaction (or a split child leg) with `addToTombstone = true`, or skipping a staged row with `SkipAndRemember`, records `DedupHash` (and its base hash stripped of any `_SPLIT_n` suffix) together with `AccountId` in `DeletedTransactionHashes`.

---

### 4.4 Smart Coalesced SQLite Backup Policy & Admin Backup Management (v2.3 $\rightarrow$ v2.6)

#### A. Root Cause of Excessive Backups & Policy Architecture
In v2.0–v2.2, every single mutation endpoint invoked `CreateSnapshotImmediateAsync()` synchronously. During normal interactive sessions—such as triaging 15 uncategorized expenses, adjusting several monthly category budgets, or editing a transaction with inline rule creation and back-propagation—this produced dozens of back-to-back full-database `VACUUM INTO` copies within minutes.

To prevent backup sprawl while preserving strong local data safety, `BackupService` introduces a **Tiered & Coalesced Backup Policy** controlled via `Settings` and manageable at runtime in the Admin Panel:

| Setting Key | Default | Range / Options | Description |
| :--- | :--- | :--- | :--- |
| `BackupAutoMode` | `'Coalesced'` | `'Coalesced'`, `'MajorOnly'`, `'ManualOnly'`, `'EveryChange'` | Controls which events trigger automatic backups and whether rapid interactive edits are coalesced. |
| `BackupDebounceSeconds` | `30` | `5` – `600` seconds | Sliding quiet-period window used in `'Coalesced'` mode to batch rapid consecutive edits into a single snapshot. |
| `BackupMinIntervalMinutes` | `10` | `0` – `1440` minutes | Minimum cooldown interval between automatic snapshots. Edits occurring during an active cooldown are coalesced into a single trailing snapshot when the cooldown expires. |
| `MaxRetainedBackups` | `15` | `1` – `200` snapshots | Maximum number of `.db` snapshots retained on disk before oldest snapshots are automatically pruned. |

#### B. Behavior of `BackupAutoMode` Modes (`BackupService.RequestBackupAsync`)
1. **`'Coalesced'` *(Default — Smart Debounce + Cooldown)*:**
   - **Major / Bulk Operations (`isMajorOperation = true`: `csv_upload`, `category_delete`, `category_merge`, `transaction_split`, `bulk_delete`):**
     - Cancels any pending debounce timer (since an immediate snapshot captures all prior uncommitted backup state). If outside `BackupMinIntervalMinutes`, executes `VACUUM INTO` immediately; if inside the cooldown window, schedules a single trailing snapshot at the end of the cooldown.
   - **Routine Interactive Edits (`isMajorOperation = false`: `recategorize`, `category_create`, `category_update`, `budget_edit`, `rule_create`, `rule_update`, `rule_delete`, `backprop`, `data_edit`, `transaction_delete`, `settings_update`, `account_create`, `income_override`, `income_override_delete`):**
     - Increments the in-memory pending change counter (`PendingChangesCount`), records the trigger reason, and resets the sliding debounce timer (`max(BackupDebounceSeconds, remainingCooldown)`).
     - Once the quiet window elapses, writes **one** consolidated snapshot named `budget_<timestamp>_coalesced_<N>changes.db`.
2. **`'MajorOnly'` *(CSV Imports, Category Deletes/Merges, Transaction Splits & Bulk Deletes Only)*:**
   - Routine single-row edits (`isMajorOperation = false`) do **not** create or schedule snapshots. Only major operations (`isMajorOperation = true`) trigger automatic snapshots (respecting `BackupMinIntervalMinutes`).
3. **`'ManualOnly'` *(Disable Automatic Backups)*:**
   - Automatic backups are disabled across all endpoints (`RequestBackupAsync` returns `null`). Snapshots are only created when the Administrator clicks **Create Manual Snapshot Now** or explicitly requests a pre-operation safety snapshot before Restore, Clear History, or Reset All.
4. **`'EveryChange'` *(Legacy Immediate Mode)*:**
   - Creates an immediate snapshot on each top-level mutation (while suppressing duplicate nested snapshots inside composite operations such as `UpdateTransactionAsync` with rule creation + back-propagation).

#### C. Admin Panel Backup, Restore, Clear History & Reset All Contract (`topologyView.js` Section 4 & `inspectorView.js`)
The **Local SQLite Backup, Restore & Database Management** card in the Admin Panel (`Categories, Rules, Income & Backups`) and the **Transaction Inspector** header provide end-to-end control over backup policies, storage, retention, recovery, and transaction history reset:
1. **Storage & Queue Telemetry Bar:**
   - Displays **Total Snapshots (`N / MaxRetained`)**, **Total Disk Usage (`KB / MB`)**, **Latest Snapshot Timestamp**, and **Pending Coalesced Changes (`N` queued edits)** with a one-click **"Flush Pending Now"** button.
2. **Inline Backup Policy Configuration:**
   - Allows the Administrator to adjust `Auto-Backup Mode`, `Quiet Debounce (sec)`, `Min Cooldown (min)`, and `Max Retained Snapshots` and save them via `PUT /api/backups/config` (which also immediately prunes excess files if `MaxRetainedBackups` was reduced).
3. **Snapshot List Sorting, Single/Bulk Deletion & Pruning:**
   - Sort snapshots by **Newest First (`date_desc`)**, **Oldest First (`date_asc`)**, **Largest Size (`size_desc`)**, **Smallest Size (`size_asc`)**, or **Trigger Reason (`reason_asc`)**.
   - Select individual or all snapshots via checkboxes to **Bulk Delete Selected (`POST /api/backups/bulk-delete`)**, delete a single row (`DELETE /api/backups/{fileName}`), or **Prune Oldest Snapshots (`POST /api/backups/prune`)** down to any target count.
4. **Verified In-App Database Restore (`POST /api/backups/restore/{fileName}` & `POST /api/backups/restore-upload`):**
   - **Per-Row or Top-Level Restore Modal:** Clicking **`↺ Restore Database...`** in the header toolbar or **`↺ Restore DB...`** on any snapshot row opens the Database Restore Modal, supporting two modes:
     - **Restore from Local Snapshot:** Select any snapshot from `Backups/` (pre-selected when launched from a row).
     - **Upload & Restore External `.db` Snapshot:** Upload a `.db` file previously downloaded to the user's computer or Google Drive (`POST /api/backups/restore-upload`), which saves the file into `Backups/`, verifies its SQLite integrity, and restores it.
   - Both restore workflows enforce recent-password **Step-Up Confirmation** (`ConfirmPassword`) and include a pre-checked option to **create an automatic `pre_restore` safety backup** of the current live database first.
   - **Authoritative Identity Preservation & Transactional Recovery:** Financial-data restores **never** roll back authentication credentials, security stamps, lockouts, roles, or account activation states from an older snapshot:
     1. Acquires `BackupService.ExecuteExclusiveLifecycleActionAsync` mutex (canceling pending debounced backups and blocking concurrent lifecycle operations).
     2. Captures the complete live identity & audit snapshot in memory: **ALL** `Users` rows (both `IsActive = 1` and `IsActive = 0`, including `PasswordHash`, `SecurityStamp`, `FailedLoginAttempts`, `LockoutEndUtc`, `Role`, `LastLoginAt`), `Settings.AuthInitialized`, and `SecurityAuditLogs`.
     3. Verifies target snapshot `PRAGMA integrity_check;`, creates the `pre_restore` safety snapshot if enabled (aborting on failure), stages a temporary copy, applies `DatabaseInitializer.ApplyIdempotentSchemaMigrationsAsync`, and atomically upserts/replaces the authoritative live `Users`, `AuthInitialized`, and `SecurityAuditLogs` inside a single SQLite `BEGIN IMMEDIATE` transaction before swapping into `budget.db` (if any migration or identity-reapply step fails, the live database remains untouched or rolls back cleanly from the pre-restore safety copy).
5. **Clear Transaction History with Pre-Clear Backup (`POST /api/transactions/clear-history`):**
   - Available from both the **Admin Panel (Section 4)** and the **Transaction Inspector** header via the **`🗑 Clear Transaction History...`** button (`openClearTransactionHistoryModal`).
   - Designed for rapid iteration on category topology, rules, and CSV parsing without losing configured `Categories`, `CategorizationRules`, `Accounts`, `MonthlyBudget` targets, or `Settings` / `IncomeOverrides`.
   - Presents an interactive modal with:
     - **Scope Selector:** Clear **All Accounts (Full Transaction History)** or only transactions belonging to a **Specific Account**.
     - **`[x] Create a database backup snapshot ("pre_clear_history") before clearing`** *(Checked by default)*: Immediately writes a verified SQLite snapshot before deleting any rows; if snapshot creation fails, the operation aborts before deleting data.
     - **`[x] Also reset deleted transaction tombstones (DeletedTransactionHashes)`** *(Checked by default)*: Clears tombstone hashes (scoped to `AccountId` when a single account is selected, or all tombstones when clearing all accounts) so previously deleted transactions do not block re-importing CSV files.
     - **`[x] Clear uncommitted CSV upload staging sessions (StagingSessions)`** *(Checked by default, scoped to `AccountId` when selected)*.
6. **Reset All Information — Start from Clean History (`POST /api/admin/reset-all`):**
   - Available in the **Admin Panel (Section 4)** and the **Transaction Inspector** header via the **`⚠️ Reset All Information...`** button (`openResetAllDatabaseModal`).
   - Executes under `BackupService.ExecuteExclusiveLifecycleActionAsync` (canceling any pending debounce timer and holding the backup/lifecycle mutex) and uses `DatabaseInitializer.RecreateCleanSchemaAsync` to drop financial tables under `PRAGMA foreign_keys = OFF` and recreate clean tables.
   - **Strict Backup, Policy & Authoritative Identity Preservation Guarantee:**
     - Resetting all live financial information **never deletes or modifies any backup `.db` files** in `Backups/` or Data Protection keys in `keys/`.
     - Preserves the user's configured backup policy settings (`BackupAutoMode`, `BackupDebounceSeconds`, `BackupMinIntervalMinutes`, `MaxRetainedBackups`) across the reset so subsequent snapshots never prune existing backup files back down to `15`.
     - **Strictly preserves ALL `Users` rows (active and inactive), `Settings.AuthInitialized`, and `SecurityAuditLogs`** atomically inside a transaction so resetting financial/budget data never destroys user credentials, reactivates disabled users, resets lockouts, or re-opens anonymous setup.
     - If `CreateBackupFirst = true` and the `"pre_reset_all"` safety snapshot fails, the reset aborts immediately without modifying the database.
     - Records `('DefaultTaxonomySeeded', '1')` in `Settings` regardless of whether `ReseedDefaultCategoriesAndRules` is checked or unchecked, ensuring a clean zero-category reset stays empty across future application restarts.
   - Presents a dedicated confirmation modal with:
     - **`[x] Back up database before resetting ("pre_reset_all" snapshot)`** *(Checked by default)*.
     - **`[x] Re-seed default starter Categories (12) & Auto-Categorization Rules (15)`** *(Checked by default; unchecking starts with 0 categories and 0 rules)*.
     - **Explicit Confirmation Checkbox (`#resetAllConfirmCheck`):** Requires the administrator to check *"I understand this will reset all live database information (backup databases and user accounts will NOT be deleted)"* and enter **Current Admin Password** (step-up confirmation) before the reset button is enabled.

---

### 4.5 Role-Based User Authentication, Security Architecture & Mandatory LAN HTTPS (v2.9)

#### A. Neutral Role-Based Access Control (RBAC) & Fixed Household Accounts
The system transitions from an open household trust model to an authenticated dual-role security architecture built on neutral security roles (`Admin` and `User`):
- **Neutral Security Semantics:** `Admin` and `User` are security roles, not relationship or gender labels. Each user record also stores a configurable `DisplayName` (e.g., `"Alex"` or `"Partner"`, or `"Husband"` / `"Wife"` if chosen during setup) that is purely cosmetic in the UI and **never** referenced for authorization decisions.
- **Fixed Two-Account Scope & Account Lifecycle Rules:**
  - HomeBudget intentionally limits accounts to two fixed logins (`admin` with immutable `Role = 'Admin'`, and `user` with immutable `Role = 'User'`). Self-registration and arbitrary user creation/deletion endpoints are not exposed.
  - `Username` and `Role` are immutable after setup; `DisplayName` may be updated without affecting permissions.
  - **Deactivation / Re-Activation (`IsActive`):** The `Admin` may disable or re-enable the `User` account (`PUT /api/users/{id}/active`). Disabling an account immediately rotates its `SecurityStamp`, causing active sessions on all devices to be rejected (`401 Unauthorized`) on their next request. The system **strictly forbids disabling the last active `Admin`** account (`400 Bad Request`). Deactivated user records remain in `Users` so `SecurityAuditLogs` history remains intact.
- **Server-Side Enforcement Contract:** Navigation tabs, read-only tables, and modal buttons are hidden/adapted in the frontend strictly for usability. Every API authorization rule is enforced server-side via ASP.NET Core authorization policies (`"AdminOnly"`, `"Authenticated"`, and deny-by-default `FallbackPolicy`).

| Capability / Surface | `Admin` (`admin`) | `User` (`user`) | Server Security Enforcement |
| :--- | :---: | :---: | :--- |
| **Cashflow & Sankey Visualization** | Full Access | Full Access | `Authenticated` (`GET /api/cashflow`) |
| **Monthly Budgets View** | Full Access | Full Access | `Authenticated` (`GET /api/budgets/*`, `PUT /api/categories/{id}/budget`) |
| **Category Lifecycle (Add/Edit/Delete/Merge)** | Full Access | Full Access | `Authenticated` (`/api/categories/*`) |
| **Expense Recategorization (`applyMovingForward` & `applyToPast`)** | Full Access | Full Access | `Authenticated` (`POST /api/transactions/{id}/categorize` permits rule creation + past match updates) |
| **Rule CRUD (View/Add/Edit/Delete Individual Rules)** | Full Access | Full Access | `Authenticated` (`GET /api/topology`, `POST/PUT/DELETE /api/rules/*`) |
| **Rule Back-Propagation Dry-Run & Bulk Commit** | Full Access | **Blocked (`403`)** | **`AdminOnly`** (`POST /api/rules/back-propagate`) |
| **Categories, Rules & Admin Page (`topologyView.js`)** | All 5 Sections + Audit | Sections 1 & 2 Only | UI hides Sections 3–5 for `User` and skips Admin fetches; server enforces **`AdminOnly`** on underlying endpoints |
| **CSV Statement Ingestion (Upload & Stage)** | Full Access | **Blocked (`403`)** | **`AdminOnly`** (`POST /api/uploads/stage`, `POST /api/uploads/commit`) |
| **Transaction Inspector (`inspectorView.js`)** | Full Edit / Split / Delete | Read-Only View | `GET /api/transactions` is `Authenticated`; `PUT/POST split/DELETE/bulk-delete/clear-history` are **`AdminOnly`** |
| **Source Account Management (Add/Edit/Rename)** | Full Access | **Blocked (`403`)** | **`AdminOnly`** (`POST /api/accounts`, `PUT /api/accounts/{id}`) |
| **Income Settings & Monthly Overrides** | Full Access | **Blocked (`403`)** | **`AdminOnly`** (`GET/PUT /api/settings`, `PUT/DELETE /api/settings/income-overrides`) *(Cashflow/Budgets compute effective income directly)* |
| **Database Lifecycle (Clear History, Reset All)** | Full Access | **Blocked (`403`)** | **`AdminOnly`** + recent-password step-up confirmation required on `Reset All` |
| **SQLite Backup Management & Restore** | Full Access | **Blocked (`403`)** | **`AdminOnly`** (`/api/backups/*`) + recent-password step-up confirmation required on `Restore` |
| **User Administration & Security Audit Logs** | Full Access | Self-Service Password Only | `Authenticated` (`PUT /api/auth/change-password`); **`AdminOnly`** (`GET /api/users`, password reset, unlock, active status, `GET /api/audit-logs`) |

#### B. Mandatory Transport Security (LAN HTTPS / TLS Prerequisite)
LAN-only network isolation does not prevent credential or session-cookie interception on shared home Wi-Fi networks:
1. **HTTPS Is Required Before Enabling Authentication:**
   - All login, first-run setup, and authenticated API traffic MUST traverse TLS/HTTPS (IIS HTTPS binding on Port `8443` using a locally trusted `mkcert` certificate).
   - Authentication cookies enforce `CookieSecurePolicy.Always` unconditionally in deployed production mode so browsers never transmit `HomeBudget.Auth` over plaintext HTTP.
   - Plain HTTP requests on Port `8080` either redirect to `https://<host>:8443` (or the HTTP listener is disabled after TLS validation); non-HTTPS `/api/auth/*` and `/api/*` requests are rejected fail-closed (`400 Bad Request` / `403 Forbidden`) when `RequireHttps` is enabled (all non-loopback/production IIS traffic).
   - HTTPS responses emit `Strict-Transport-Security: max-age=31536000; includeSubDomains`.
2. **Certificate Issuance, Client Trust & Renewal Lifecycle:**
   - **Issuance:** Generated on the Windows 11 host using `mkcert` (`homebudget.local`, host LAN IP, `localhost`, `127.0.0.1`) exported to `.pfx` and imported into `LocalMachine\My` (IIS Port `8443`). Windows Firewall restricts inbound TCP `8443` to `LocalSubnet` on the `Private` profile.
   - **Mobile Client Trust:** One-time import and full-trust enablement of `mkcert`'s `rootCA.pem` on household iOS/iPadOS/macOS/Windows client devices (`Settings > General > About > Certificate Trust Settings`).
   - **Expiration & Renewal:** `mkcert` leaf certificates remain valid for ~825 days (~27 months). Renewal (or re-issuance after a host LAN IP change) only requires running the PowerShell `mkcert` renewal command and updating the IIS Port `8443` certificate binding—existing `budget.db`, `Users`, and Data Protection keys remain untouched.

#### C. Protected First-Run Setup Bootstrap & Atomic Completion
1. **Host-Authorized Bootstrap Secret (`setup-token.txt`):**
   - If the application starts with `Users` empty and `Settings.AuthInitialized != '1'`, `AuthService` generates a cryptographically random 32-byte URL-safe one-time **Setup Bootstrap Token** (`RandomNumberGenerator.GetBytes(32)`) and writes it atomically to `<DatabaseDirectory>\setup-token.txt` (restricted via NTFS ACLs to `BUILTIN\Administrators` and the IIS AppPool identity).
   - `GET /api/auth/status` returns `{ isSetupRequired, isAuthenticated, user }` without ever leaking the bootstrap token over HTTP/HTTPS. An unauthenticated LAN device cannot claim setup without physically obtaining the token from the host PC.
2. **Serialized Atomic Setup Transaction (`POST /api/auth/setup`):**
   - Enforces IP rate limiting, CSRF/Origin validation, constant-time token verification (`CryptographicOperations.FixedTimeEquals`), an in-process `SemaphoreSlim` setup lock, and a single SQLite `BEGIN IMMEDIATE` transaction that:
     1. Re-verifies `SELECT COUNT(*) FROM Users == 0` and `AuthInitialized != '1'` (returning `409 Conflict` if already completed or racing).
     2. Validates both `admin` and `user` passphrases via `PasswordPolicyValidator`.
     3. Inserts both `admin` (`Role = 'Admin'`) and `user` (`Role = 'User'`) atomically with fresh `SecurityStamp` UUIDs.
     4. Records `Settings('AuthInitialized', '1')` and `SETUP_COMPLETED` in `SecurityAuditLogs`.
   - Upon transaction commit, `setup-token.txt` is immediately deleted. If setup is interrupted before completion, `setup-token.txt` remains valid until a setup transaction commits cleanly.
   - Because `Settings.AuthInitialized = '1'` persists permanently across clean resets and accidental row changes, first-run setup can **never** be re-triggered remotely over the LAN.

#### D. Password Policy & Versioned Framework Hashing
- **Passphrase-Friendly Length Limits:** Minimum **12 characters**, maximum **128 UTF-8 characters** across setup, self-service change, admin reset, and emergency recovery. No arbitrary uppercase/digit/symbol composition rules that discourage strong passphrases.
- **Exact Character Handling:** Passwords are **never** silently truncated or `.Trim()`med—leading and trailing spaces and full Unicode characters are preserved. Frontend inputs explicitly allow paste and browser/iOS password-manager autofill (`autocomplete="current-password"` / `"new-password"`).
- **Trivial/Weak Password Screening:** Rejects passwords matching the account's `Username` or `DisplayName`, single repeated-character strings, or entries in an embedded privacy-preserving offline blocklist of common breached passphrases/sequences (e.g., `"password1234"`, `"123456789012"`, `"qwertyuiop12"`).
- **Versioned Hash Format & Transparent Upgrades:** Uses ASP.NET Core's `PasswordHasher<User>` (`PasswordHasherCompatibilityMode.IdentityV3` — PBKDF2-HMAC-SHA512 versioned format) rather than a custom hand-rolled hash format. During login, if `VerifyHashedPassword` returns `PasswordVerificationResult.SuccessRehashNeeded`, `AuthService` transparently rehashes and persists the upgraded hash in SQLite inside the login transaction.

#### E. Login Rate Limiting, Atomic Lockout & Generic Error Contract
1. **Partitioned Request Rate Limiting (`AddRateLimiter`):**
   - `POST /api/auth/login` and `POST /api/auth/setup` enforce sliding/fixed-window rate limits partitioned by **client IP** (e.g., max 10 requests/minute and 30 requests/15 minutes per IP) AND **target username** (preventing credential stuffing while ensuring a noisy client IP hits `429 Too Many Requests` with `Retry-After` before locking out another household member).
2. **Atomic Lockout Counter Updates:**
   - Failed password verification executes an atomic SQLite `UPDATE` statement so concurrent requests never lose counter increments:
     ```sql
     UPDATE Users
     SET FailedLoginAttempts = FailedLoginAttempts + 1,
         LockoutEndUtc = CASE
             WHEN FailedLoginAttempts + 1 >= 5 THEN datetime('now', '+15 minutes')
             ELSE LockoutEndUtc
         END,
         UpdatedAt = datetime('now')
     WHERE UserId = @UserId;
     ```
   - After 5 consecutive failures, the account enters a 15-minute lockout (`LockoutEndUtc`). Successful authentication (once lockout expires or is cleared by Admin) resets `FailedLoginAttempts = 0` and `LockoutEndUtc = NULL`.
3. **Generic Public Errors (Anti-Enumeration) & Admin Unlock:**
   - Unknown usernames perform a dummy PBKDF2 verification to normalize response timing and return `401 Unauthorized` `{ error: "Invalid username or password." }`—identical to wrong-password responses.
   - Inactive accounts and locked-out accounts do not confirm username existence to unauthenticated callers; locked-out attempts return a generic cooldown error (`429 Too Many Requests` / generic message `"Sign-in temporarily throttled or credentials invalid. Please wait before retrying or contact the Administrator."`). Exact lockout status, remaining minutes, and an **Unlock Account** button (`POST /api/users/{id}/unlock`) are exposed exclusively inside the authenticated `Admin` panel.

#### F. Session Cookie Policy, `RememberMe`, Revocation & Step-Up Re-Authentication
1. **Explicit Cookie Attributes:**
   - `Name = "HomeBudget.Auth"`, `HttpOnly = true`, `SecurePolicy = CookieSecurePolicy.Always`, `SameSite = SameSiteMode.Lax`, `Path = "/"`.
   - Unauthenticated or unauthorized API calls return JSON `401` / `403` via `OnRedirectToLogin` / `OnRedirectToAccessDenied` (never HTML redirects).
2. **`RememberMe` Persistent vs. Session Cookie Semantics:**
   - **`RememberMe = true` ("Remember this device (90 days)"):** Sets `IsPersistent = true` with `ExpiresUtc = DateTimeOffset.UtcNow.AddDays(90)` and `SlidingExpiration = true`, **capped by an explicit absolute maximum session lifetime of 180 days** via a signed `AuthIssuedAt` claim validated inside `OnValidatePrincipal` (preventing infinite sliding renewal on stale devices).
   - **`RememberMe = false`:** Sets `IsPersistent = false` (non-persistent browser session cookie cleared when the browser closes) with a 12-hour idle window and 24-hour absolute maximum lifetime.
3. **Immediate Server-Side Revocation (`SecurityStamp`):**
   - `Users.SecurityStamp` and `AuthIssuedAt` are embedded in cookie claims and verified against SQLite inside `CookieAuthenticationEvents.ValidatePrincipal`.
   - **Self-Service Password Change (`PUT /api/auth/change-password`):** Rotates `SecurityStamp` (revoking all other devices) and immediately re-issues a fresh cookie for the current device/session.
   - **Admin Password Reset, Deactivation, & Emergency Recovery:** Rotates `SecurityStamp` to immediately terminate active sessions across all devices for that account.
   - **Logout (`POST /api/auth/logout`):** Signs out the current browser session cookie (`HttpContext.SignOutAsync`), broadcasts a cross-tab `localStorage` signal (`homebudget:auth-sync`) so sibling open tabs return to login cleanly, and supports `?allDevices=true` to rotate `SecurityStamp` across all devices if requested.
4. **Sensitive Response Caching & High-Impact Step-Up Re-Authentication:**
   - All `/api/*` responses include `Cache-Control: no-store, no-cache, must-revalidate` and `Pragma: no-cache` so browsers and Safari's back/forward cache do not persist financial data after sign-out.
   - Destructive operations (`POST /api/admin/reset-all`, `POST /api/backups/restore/{fileName}`, `POST /api/backups/restore-upload`) require the Administrator's current password (`ConfirmPassword`) inside the confirmation modal, re-verified by `AuthService` right before executing under the lifecycle mutex.

#### G. CSRF, Strict CORS & Browser-Origin Strategy
HomeBudget combines layered browser-boundary controls rather than relying on any single check:
1. **Strict Same-Origin CORS Policy:** No `AllowAnyOrigin()` and no cross-origin `AllowCredentials()`. Only exact same-origin requests are served.
2. **`Origin` / `Referer` Header Validation (`CsrfAndOriginMiddleware`):** All state-changing HTTP methods (`POST`, `PUT`, `DELETE`, `PATCH`) must originate from the same `Scheme://Host[:Port]` whenever `Origin` (or fallback `Referer`) is supplied; mismatched or cross-origin requests fail closed with `403 Forbidden` and emit a `CSRF_REJECTED` audit event.
3. **Custom Header Requirement (`X-HomeBudget-Request: 1`):** Enforced across **every** state-changing `/api/*` endpoint—explicitly including `POST /api/auth/login`, `POST /api/auth/setup`, `POST /api/auth/logout`, and multipart upload endpoints (`POST /api/uploads/stage`, `POST /api/backups/restore-upload`, which bypass Minimal API default form antiforgery with `.DisableAntiforgery()` and instead rely on `CsrfAndOriginMiddleware` before reading request streams).
4. **Cookie `SameSite = SameSiteMode.Lax`:** Prevents browsers from attaching `HomeBudget.Auth` to third-party form submissions or background XHR/fetch requests.
5. **Security Boundary Note:** CSRF, `SameSite`, CORS, and `Origin` checks do **not** defend against Cross-Site Scripting (XSS); XSS mitigation relies on strict output encoding (`escapeHtml()`) and the Content Security Policy (CSP) header.

#### H. Data Protection Key Ring Security & Lifecycle
Authentication tickets are encrypted with ASP.NET Core Data Protection keys stored at `<DatabaseDirectory>\keys\` (`C:\ProgramData\HomeBudget\keys\`), treated as a separate security asset from `budget.db` financial backups:
- **Outside Web Root:** Resides under `C:\ProgramData\HomeBudget\keys\`, never inside IIS `wwwroot`.
- **At-Rest Encryption (Windows DPAPI):** Configured with `.PersistKeysToFileSystem(new DirectoryInfo(keysDir)).SetApplicationName("HomeBudget")`, plus `.ProtectKeysWithDpapi(protectToLocalMachine: true)` when `OperatingSystem.IsWindows()` so key XML files are encrypted on disk by Windows DPAPI.
- **Restrictive NTFS ACLs:** Provisioning script disables inheritance and grants permissions exclusively to `IIS AppPool\HomeBudget` and `BUILTIN\Administrators`.
- **Rotation, Retention & Loss Recovery:** Keys auto-rotate on ASP.NET Core's 90-day lifetime while retaining prior validation keys until existing 90-day cookies expire. Keys are **never** included in downloadable `.db` snapshots. If the `keys/` directory is accidentally lost or corrupted, the host logs a diagnostic warning and cleanly rejects old cookies (`401 Unauthorized`)—requiring household users to re-authenticate without crashing the application.

#### I. Break-Glass Emergency Admin Recovery (One-Time Host-Local Mechanism)
HomeBudget explicitly avoids storing any emergency password in `appsettings.json`, git repositories, or persistent configuration files:
1. **One-Time Recovery Token File (`admin-recovery.token`):** If the Administrator loses access or locks out `admin`, they log directly onto the Windows 11 host PC and write a one-time fresh temporary password (>= 12 chars, validated by `PasswordPolicyValidator`) to `<DatabaseDirectory>\admin-recovery.token` (NTFS-restricted to `Administrators` and IIS AppPool identity) or run the host-local PowerShell break-glass script which restarts/triggers recovery once.
2. **Single-Use Atomic Consumption:** On startup (or recovery trigger), `AuthService` inspects `<DatabaseDirectory>\admin-recovery.token`:
   - Reads the temporary password, validates policy compliance, hashes it for `admin`, rotates `SecurityStamp`, resets `FailedLoginAttempts = 0` & `LockoutEndUtc = NULL`, ensures `IsActive = 1`, and writes an `EMERGENCY_RECOVERY` entry to `SecurityAuditLogs` (**without** logging the secret or token value).
   - **Immediately deletes `admin-recovery.token`** (and records a one-time recovery timestamp/digest check so an accidental duplicate file/restart cannot endlessly replay the same secret).
   - Prompts/requires the Administrator to immediately set a permanent passphrase via `PUT /api/auth/change-password` upon login and verify `admin-recovery.token` no longer exists.

#### J. Security Audit Logging (`SecurityAuditService` & `SecurityAuditLogs`)
A dedicated `SecurityAuditService` records security-relevant events in `SecurityAuditLogs` and structured host logs:
- **Recorded Events:** `LOGIN_SUCCESS`, `LOGIN_FAILURE`, `ACCOUNT_LOCKED`, `ACCOUNT_UNLOCKED`, `SETUP_COMPLETED`, `PASSWORD_CHANGED`, `ADMIN_PASSWORD_RESET`, `USER_STATUS_CHANGED`, `EMERGENCY_RECOVERY`, `SESSION_REVOKED`, `AUTHZ_DENIED` (unexpected 403s on sensitive admin endpoints), `CSRF_REJECTED`, and `STEPUP_FAILED`.
- **Fields & Redaction Rule:** Captures `TimestampUtc`, `EventType`, `Outcome`, `ActorUserId`, `ActorUsername`, `TargetUsername`, `SourceIp`, and a brief sanitized `Details` summary. **Never** logs plaintext passwords, password hashes, cookie values, Data Protection keys, setup tokens, recovery secrets, or full request bodies.
- **Retention & Admin Inspection:** `SecurityAuditLogs` are retained across financial resets/restores (pruned automatically beyond 365 days or 10,000 entries to bound disk growth) and surfaced read-only in the Admin Panel via `GET /api/audit-logs`.

#### K. Browser Security Headers & Static File Exposure
All responses pass through a security-headers middleware enforcing:
- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: DENY`
- `Referrer-Policy: no-referrer`
- `Permissions-Policy: camera=(), microphone=(), geolocation=()`
- `Content-Security-Policy`: `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'` *(permits existing inline `style="..."` templates and SVG attributes while blocking all inline scripts and external script/connect origins)*
- `Cache-Control: no-store, no-cache, must-revalidate` & `Pragma: no-cache` on all `/api/*` routes.
- All persistent runtime artifacts (`budget.db`, `Backups/`, `keys/`, `setup-token.txt`, `admin-recovery.token`) reside strictly in `C:\ProgramData\HomeBudget\`, outside the IIS `wwwroot` static directory.

#### L. Security Acceptance Checklist (Release Gate)
Authentication is only considered complete when every item below passes automated and manual verification:
1. No private `/api/*` endpoint is reachable anonymously (`FallbackPolicy` deny-by-default verified).
2. No `AdminOnly` endpoint can be invoked by a `User` even via crafted `curl`/HTTP requests (`403 Forbidden`).
3. No login, setup, or authenticated API request succeeds or issues cookies over plaintext HTTP (`CookieSecurePolicy.Always`).
4. First-run setup rejects missing/invalid bootstrap tokens, handles concurrent calls atomically, and never re-opens after setup or `Reset All`.
5. CSRF/Origin checks reject missing `X-HomeBudget-Request` headers, invalid `Origin`/`Referer`, and cross-origin requests (`403 Forbidden`) across login, setup, logout, uploads, and mutations.
6. Login rate limiting (`429`) and atomic 5-failure / 15-minute lockout behave accurately without leaking username existence.
7. `RememberMe` true (90d persistent, 180d max) vs false (browser session cookie, 12h/24h max) behaves as specified on desktop and iPhone Safari.
8. Password changes, Admin resets, and `IsActive = 0` deactivation immediately revoke prior sessions via `SecurityStamp`; disabling the last active `Admin` is blocked.
9. Data Protection keys carry restrictive NTFS ACLs + DPAPI encryption outside `wwwroot` and survive IIS worker recycles and host reboots.
10. Financial database restores and `Reset All` preserve **all** current `Users` (active and inactive), `Settings.AuthInitialized`, and `SecurityAuditLogs` atomically under the lifecycle mutex.
11. Break-glass emergency recovery works offline on the host, consumes its token immediately, cannot replay, and records a redacted audit entry.
12. All security events appear in `SecurityAuditLogs` with zero leaked passwords, hashes, cookies, or tokens.

---

## 5. Complete API Contract & Authorization Matrix (v2.9)

Every mapped route in `src/HomeBudget.Web/Program.cs` is listed below. ASP.NET Core `FallbackPolicy` denies unauthenticated requests by default (`401 Unauthorized` JSON); any new endpoint must explicitly declare `[AllowAnonymous]`, `"Authenticated"`, or `"AdminOnly"`. All state-changing methods (`POST`, `PUT`, `DELETE`, `PATCH`) enforce `CsrfAndOriginMiddleware`.

| Method & Route | Auth Policy | Step-Up? | Description | Backup Behavior (`Coalesced` Default) |
| :--- | :--- | :---: | :--- | :--- |
| **Authentication, Users & Security Audit (`/api/auth/*`, `/api/users/*`, `/api/audit-logs`)** | | | | |
| `GET /api/auth/status` | `AllowAnonymous` | — | Returns `{ isSetupRequired, isAuthenticated, currentUser? }` (never leaks setup token). | None |
| `POST /api/auth/setup` | `AllowAnonymous` *(Bootstrap Token + 0 Users + Rate Limited)* | — | Atomic one-time creation of `admin` & `user` accounts + sets `AuthInitialized = '1'`. | **Major** (`initial_setup`) |
| `POST /api/auth/login` | `AllowAnonymous` *(Rate Limited)* | — | Validates credentials via dummy-padded constant-time path, checks atomic lockout, rehashes if needed, issues `HomeBudget.Auth`. | None |
| `POST /api/auth/logout` | `Authenticated` | — | Signs out session cookie (`?allDevices=true` optionally rotates `SecurityStamp`). | None |
| `GET /api/auth/me` | `Authenticated` | — | Returns current authenticated user profile (`userId`, `username`, `displayName`, `role`). | None |
| `PUT /api/auth/change-password` | `Authenticated` | Current Password | Self-service passphrase change (`12`–`128` chars), rotates `SecurityStamp`, re-issues current session cookie. | None |
| `GET /api/users` | `AdminOnly` | — | Lists fixed accounts with `lastLoginAt`, `isActive`, `isLockedOut`, and remaining lockout minutes. | None |
| `PUT /api/users/{id:int}/reset-password` | `AdminOnly` | — | Sets new password for target account, clears lockout, and rotates `SecurityStamp`. | None |
| `POST /api/users/{id:int}/unlock` | `AdminOnly` | — | Clears `FailedLoginAttempts` and `LockoutEndUtc` on a locked account. | None |
| `PUT /api/users/{id:int}/active` | `AdminOnly` | — | Enables/disables `user` account; immediately rotates `SecurityStamp` on disable; rejects disabling last active `Admin`. | None |
| `GET /api/audit-logs` | `AdminOnly` | — | Queries paginated redacted `SecurityAuditLogs` entries for Admin inspection. | None |
| **Household Accounts & Categories** | | | | |
| `GET /api/accounts` | `Authenticated` | — | Lists active bank and credit card accounts (needed for Inspector/Cashflow dropdowns). | None |
| `POST /api/accounts` | `AdminOnly` | — | Creates a new source account (`AccountName`, `Institution`, `AccountType`). | Coalesced (`account_create`) |
| `PUT /api/accounts/{id:int}` | `AdminOnly` | — | Renames or edits source account display name, institution format, and account type. | Coalesced (`account_rename`) |
| `GET /api/categories` | `Authenticated` | — | Lists active categories with optional `?sortBy=alpha_asc\|recent\|custom_order\|budget_desc`. | None |
| `POST /api/categories` | `Authenticated` | — | Creates a new category. | Coalesced (`category_create`) |
| `PUT /api/categories/{id:int}` | `Authenticated` | — | Edits category properties and updates `UpdatedAt`. | Coalesced (`category_update`) |
| `DELETE /api/categories/{id:int}` | `Authenticated` | — | Deletes category (optional `?replacementCategoryId=` to reassign transactions/rules). | **Major** (`category_delete`) |
| `PUT /api/categories/{id:int}/budget` | `Authenticated` | — | Updates a category's `MonthlyBudget` target and `UpdatedAt`. | Coalesced (`budget_edit`) |
| `POST /api/categories/{id:int}/merge-into/{targetId:int}` | `Authenticated` | — | Merges source category into target category and updates target `UpdatedAt`. | **Major** (`category_merge`) |
| **Taxonomy & Auto-Categorization Rules** | | | | |
| `GET /api/topology` | `Authenticated` | — | Returns all active categories and `CategorizationRules`. | None |
| `POST /api/rules` | `Authenticated` | — | Creates an auto-categorization rule. | Coalesced (`rule_create`) |
| `PUT /api/rules/{id:int}` | `Authenticated` | — | Updates an existing categorization rule and `UpdatedAt`. | Coalesced (`rule_update`) |
| `DELETE /api/rules/{id:int}` | `Authenticated` | — | Deletes a categorization rule. | Coalesced (`rule_delete`) |
| `POST /api/rules/back-propagate` | **`AdminOnly`** | — | Bulk dry-run or commit of retroactive rule evaluation across all historical transactions. | Coalesced (when `dryRun = false`, `backprop`) |
| **Cashflow & Monthly Budgets** | | | | |
| `GET /api/cashflow` | `Authenticated` | — | Returns cashflow summary (including effective monthly income), Sankey nodes/links, `uncategorizedTransactions`, and `?categorySort=`. | None |
| `GET /api/budgets/{yearMonth?}` | `Authenticated` | — | Returns monthly budget summary, category progress cards, and uncategorized transactions for the month. | None |
| **CSV Uploads & Transactions** | | | | |
| `POST /api/uploads/stage` | `AdminOnly` | — | Parses Chase/Amex/Citi/Wells Fargo CSVs, computes `DedupHash`, persists `StagingSessions` *(enforces `CsrfAndOriginMiddleware` explicitly alongside `.DisableAntiforgery()`)*. | None |
| `POST /api/uploads/commit` | `AdminOnly` | — | Commits approved staged rows into `Transactions` / `DeletedTransactionHashes`. | **Major** (`csv_upload`) |
| `GET /api/transactions/similar-count` | `Authenticated` | — | Returns count of past transactions matching `pattern` for the Categorize modal. | None |
| `POST /api/transactions/{id:int}/categorize` | `Authenticated` | — | Categorizes or recategorizes an expense with non-mutually exclusive `applyMovingForward` and `applyToPast` flags. | Coalesced (`recategorize`) |
| `GET /api/transactions` | `Authenticated` | — | Queries transactions with multi-criteria filters and `sortBy` (read-only table for `User`; edit actions gated below). | None |
| `PUT /api/transactions/{id:int}` | `AdminOnly` | — | Edits raw transaction fields and `Notes`. | Coalesced (`data_edit`) |
| `POST /api/transactions/{id:int}/split` | `AdminOnly` | — | Splits a transaction into multiple categorized child legs. | **Major** (`transaction_split`) |
| `DELETE /api/transactions/{id:int}` | `AdminOnly` | — | Deletes an income or expense transaction (with `?addToTombstone=true/false`). | Coalesced (`transaction_delete`) |
| `POST /api/transactions/bulk-delete` | `AdminOnly` | — | Bulk-deletes selected transactions in one atomic transaction. | **Major** (`bulk_delete`) |
| `POST /api/transactions/clear-history` | `AdminOnly` | — | Clears transactions (all or single account) with optional pre-clear backup and tombstone/staging cleanup. | **Immediate Pre-Clear** (`pre_clear_history`) |
| `POST /api/admin/reset-all` | `AdminOnly` | **Yes** | Resets live financial tables while preserving all backups, retention policy, `Users`, `AuthInitialized`, and `SecurityAuditLogs`. | **Immediate Pre-Reset** (`pre_reset_all`) |
| **Settings & Income Overrides *(Admin Only)*** | | | | |
| `GET /api/settings` | **`AdminOnly`** | — | Returns Admin household income config, `googlePayrollMatchPattern`, `hasGeminiApiKey`, and `incomeOverrides`. | None |
| `PUT /api/settings` | `AdminOnly` | — | Updates persistent monthly income, payroll match pattern, income mode, and optional Gemini API key. | Coalesced (`settings_update`) |
| `PUT /api/settings/income-overrides` | `AdminOnly` | — | Upserts a monthly income override for a specific `YearMonth`. | Coalesced (`income_override`) |
| `DELETE /api/settings/income-overrides/{yearMonth}` | `AdminOnly` | — | Deletes a monthly income override. | Coalesced (`income_override_delete`) |
| **SQLite Backups & Restore** | | | | |
| `GET /api/backups` | `AdminOnly` | — | Returns backup directory metadata, policy `config`, storage/queue `status`, and snapshots list. | None |
| `PUT /api/backups/config` | `AdminOnly` | — | Updates backup mode, debounce, cooldown, and max retained snapshots; prunes excess snapshots. | None |
| `POST /api/backups/trigger` | `AdminOnly` | — | Flushes pending coalesced changes and creates a verified manual snapshot. | **Immediate** (`manual`) |
| `GET /api/backups/download/{fileName}` | `AdminOnly` | — | Downloads a specific `.db` financial backup file. | None |
| `DELETE /api/backups/{fileName}` | `AdminOnly` | — | Deletes a single `.db` backup snapshot from disk. | None |
| `POST /api/backups/bulk-delete` | `AdminOnly` | — | Deletes multiple selected `.db` snapshots in one operation. | None |
| `POST /api/backups/prune` | `AdminOnly` | — | Prunes older snapshots so only `keepCount` newest `.db` files remain. | None |
| `POST /api/backups/restore/{fileName}` | `AdminOnly` | **Yes** | Verifies snapshot integrity, optionally takes `pre_restore` snapshot, restores financial data, migrates schema, and preserves authoritative `Users`/`SecurityAuditLogs`. | **Immediate** (`pre_restore`) |
| `POST /api/backups/restore-upload` | `AdminOnly` | **Yes** | Uploads external `.db` snapshot *(enforces `CsrfAndOriginMiddleware` alongside `.DisableAntiforgery()`)*, verifies integrity, restores financial data, and preserves authoritative identity state. | **Immediate** (`pre_restore`) |

---

## 6. UI/UX Wireframe Flows

### 6.1 Unified Categorization Modal (with Category Dropdown Sort Selector)
```
+-----------------------------------------------------------------------+
| Categorize Expense                                                [X] |
+-----------------------------------------------------------------------+
| Transaction:  2026-03-14 • TRADER JOE'S #124                $84.52    |
|                                                                       |
| 1. Select Category   Sort List: [ A-Z ▼ ]   [ + New Category ]        |
|    (Sort options: Alphabetical A-Z | Recent Changes | Custom | $)     |
|    [ Groceries (Budget: $1,000.00)                               ▼ ]  |
|                                                                       |
| 2. Automatic Categorization Options (Select one, both, or neither):   |
|    Match Merchant Pattern: [ TRADER JOE'S                        ]    |
|                                                                       |
|    [x] Moving Forward: Automatically categorize future expenses       |
|        matching "TRADER JOE'S" as Groceries.                          |
|                                                                       |
|    [x] In the Past: Recategorize existing past expenses               |
|        matching "TRADER JOE'S" as Groceries (6 past matches found).   |
|                                                                       |
|                                       [ Cancel ]  [ Save & Apply ]    |
+-----------------------------------------------------------------------+
```

### 6.2 Visualization Page (`Cashflow & Sankey`) — Simplified Top-Level Table + Account Spend Summary
```
+-----------------------------------------------------------------------+
| Cashflow & Sankey Visualization                                       |
| Window: [ Current MTD ] [ Prior Month ] [ Last 3M ] [ Custom ]        |
| Sort Categories: [ Highest Spend ▼ ]  (Lowest $ | A-Z | Z-A | Recent) |
+-----------------------------------------------------------------------+
|   [ S A N K E Y    D I A G R A M   (Nodes ordered by selected sort) ] |
+-----------------------------------------------------------------------+
| ⚠️ Uncategorized Expenses Triage (4 items • $277.30)                  |
| View: [ Group by Merchant (3) ] [ List (4) ]   Sort: [ Highest $ ▼ ]  |
|-----------------------------------------------------------------------|
| ▸ CITY WATER UTILITY (1 charge • Mar 11)   | $137.51 | [ Categorize ] |
| ▾ COSTCO GAS (2 charges • Mar 11 – Mar 18) | $124.80 | [ Categorize ] |
|   • 2026-03-18 COSTCO GAS #042 (Chase Household Credit Card)   $64.80 |
|   • 2026-03-11 COSTCO GAS #042 (Chase Household Credit Card)   $60.00 |
| ▸ APPLE.COM/BILL (1 charge • Mar 15)       |  $14.99 | [ Categorize ] |
+-----------------------------------------------------------------------+
| Tabular Cashflow Breakdown                                            |
| Sort Category Rows: [ Highest Spend ▼ ]  Sort Tx: [ Newest Date ▼ ]   |
| Account Spend in Window:                                              |
| [ Wells Fargo Household Checking: $3,200 ] [ Chase Household CC: $980]|
|-----------------------------------------------------------------------|
| Category                 | Net Spend | % Spend | % Income | Manage    |
|-----------------------------------------------------------------------|
| ▾ ● Groceries (6)        |   $542.18 |   28.4% |     5.4% | [ Edit ]  |
|   +---------------------------------------------------------------+   |
|   | 6 Transactions (Chase Household Credit Card: $542.18)         |   |
|   | 2026-03-14 • TRADER JOE'S (Chase Household CC) | $142.80|[Rec]|   |
|   | 2026-03-08 • WHOLE FOODS (Chase Household CC)  |  $98.40|[Rec]|   |
|   +---------------------------------------------------------------+   |
+-----------------------------------------------------------------------+
```

### 6.3 Admin Backup, Restore, Transaction History & Clean-Slate Reset Panel
```
+-----------------------------------------------------------------------+
| 4. Local SQLite Backup, Restore & Database Management                 |
|    [ 🗑 Clear Transaction History... ] [ ⚠️ Reset All Information... ] |
|    [ ↺ Restore Database... ]          [ + Create Manual Snapshot Now ]|
|-----------------------------------------------------------------------|
| Snapshots: 8 / 15 Max  | Disk Used: 1.4 MB | Pending Edits: 0 Queued  |
|-----------------------------------------------------------------------|
| Auto-Backup Mode: [ Smart Coalesced (Debounce + Cooldown) ▼ ]         |
| Debounce (sec): [ 30 ]  Min Cooldown (min): [ 10 ]  Max Kept: [ 15 ]  |
|                                                [ Save Backup Policy ] |
|-----------------------------------------------------------------------|
| Sort: [ Newest First ▼ ]  [ Delete Selected (2) ] [ Prune Oldest... ] |
|-----------------------------------------------------------------------|
| [x] budget_20261109_192000_manual.db     | Manual    | 180 KB |       |
|     [ ⬇ Download ] [ ↺ Restore DB... ] [ 🗑 Delete ]                  |
| [x] budget_20261109_184512_csv_upload.db | CSV Import| 180 KB |       |
|     [ ⬇ Download ] [ ↺ Restore DB... ] [ 🗑 Delete ]                  |
+-----------------------------------------------------------------------+
```

### 6.4 Clear Transaction History Modal (with Pre-Clear Backup Protection)
```
+-----------------------------------------------------------------------+
| Clear Transaction History                                         [X] |
+-----------------------------------------------------------------------+
| Reset imported transactions while preserving your Categories,         |
| Auto-Categorization Rules, Accounts, Monthly Budgets, and Settings.   |
|                                                                       |
| Scope: [ All Accounts (Clear Entire Transaction History)          ▼ ] |
|                                                                       |
| [x] Back up database before clearing ("pre_clear_history" snapshot)   |
|     Creates a verified SQLite snapshot first so you can restore       |
|     your current transactions at any time.                            |
|                                                                       |
| [x] Also clear deleted transaction tombstones                         |
|     Allows re-importing previously deleted CSV rows cleanly.          |
|                                                                       |
| [x] Also clear uncommitted CSV staging sessions                       |
|                                                                       |
|                      [ Cancel ]  [ Backup & Clear Transaction History ]|
+-----------------------------------------------------------------------+
```

### 6.5 Reset All Information — Start from Clean History Modal
```
+-----------------------------------------------------------------------+
| ⚠️ Reset All Information (Start from Clean History)               [X] |
+-----------------------------------------------------------------------+
| Resets all live database information (Transactions, Tombstones,       |
| Staging Sessions, Categories, Rules, and Income Overrides) to start   |
| from a clean slate.                                                   |
|                                                                       |
| 🛡️ Preserved Assets: Existing SQLite backup snapshots in Backups/,    |
|    Data Protection keys, user credentials, and audit logs are NEVER   |
|    deleted by this operation.                                         |
|                                                                       |
| [x] Back up current database first ("pre_reset_all" safety snapshot)  |
|                                                                       |
| [x] Re-seed default starter Categories (12) & Rules (15)              |
|     (Uncheck to start with zero categories and zero rules)            |
|                                                                       |
| [x] I understand this will reset all live database information        |
|     (backup databases and user accounts will NOT be deleted)          |
|                                                                       |
| Step-Up Confirmation (Admin Password): [ ••••••••••••••••••••••••• ]  |
|                                                                       |
|                      [ Cancel ]  [ Backup & Reset All Information ]   |
+-----------------------------------------------------------------------+
```

---

## 7. Trust Model, Error Handling & Verification Strategy

### 7.1 Authoritative Authenticated LAN Trust Model
- **Network Boundary Is Defense-in-Depth Only:** The service is deployed on a Windows 11 host behind a Windows Defender Firewall rule restricting inbound HTTPS traffic (TCP port `8443`) to `LocalSubnet` on the `Private` network profile. Local network membership is strictly a network exposure boundary—**never** an identity, authentication, or authorization mechanism.
- **Mandatory Transport Encryption (HTTPS):** Because household credentials and session cookies traverse Wi-Fi/LAN connections, TLS/HTTPS (`:8443`) is a **mandatory prerequisite** before enabling authentication. Authentication cookies set `SecurePolicy = CookieSecurePolicy.Always`, and authentication or authenticated `/api/*` requests arriving over unencrypted HTTP are refused or redirected to HTTPS.
- **Deny-by-Default Server Authorization:** ASP.NET Core configures `AuthorizationOptions.FallbackPolicy` to require an authenticated user principal by default. Every `/api/*` endpoint requires authentication (and role checks for `AdminOnly` routes) unless explicitly allowlisted with `[AllowAnonymous]` (`GET /api/auth/status`, rate-limited/bootstrap-protected `POST /api/auth/setup`, and rate-limited `POST /api/auth/login`).
- **Server-Side Enforcement Contract:** Frontend tab hiding, read-only buttons, and hidden admin panels are strictly UX conveniences. Every security decision (`Admin` vs `User`, `IsActive`, `SecurityStamp`, CSRF/Origin validation, and step-up re-authentication) is enforced on the server before data access or mutations execute.
- **Safeguards for High-Impact Actions:** Destructive administrative actions (`Reset All Information`, `Restore Database`, `Clear Transaction History`, `Bulk Delete`) enforce server-side `AdminOnly` authorization, multi-step confirmation modals, pre-checked SQLite safety snapshots (`pre_reset_all`, `pre_clear_history`, `pre_restore`), and recent-password step-up confirmation on `Reset All` and `Restore Database`.

### 7.2 Error Handling & Diagnostic Reporting Contract
- **Structured Problem Details (`Results.Problem` / `{ error }`):** Endpoints performing database mutations or administrative operations catch `InvalidOperationException` and `SqliteException`, log full diagnostic context on the server (with strict redaction of passwords, hashes, cookies, setup tokens, and recovery secrets), and return structured JSON (`{ error }` or RFC 7807 `ProblemDetails` with `detail` and `title`).
- **Consistent API Auth Errors (No Browser Redirects):** Unauthenticated API requests return `401 Unauthorized` JSON and unauthorized role requests return `403 Forbidden` JSON—never HTML login redirects.
- **Generic Login Failure Messages:** Authentication failures for unknown usernames and wrong passwords return identical public responses (`401 Unauthorized` `"Invalid username or password."`) without revealing account existence.
- **Frontend `apiFetch` Extraction (`app.js`):** The client `apiFetch` wrapper parses `errObj.error || errObj.detail || errObj.title` from non-2xx JSON responses, triggers safe local-route re-authentication flows on `401` across active tabs, and displays actionable toast notifications.

### 7.3 Automated Verification, Security & Upgrade Testing Strategy
1. **Frontend Module & Contract Verification (`node tests/frontend_verification.mjs`):**
   - Verifies ES6 syntax (`node --check`) across all frontend modules (`app.js`, `sankeyChart.js`, and all view modules including `loginView.js`), validates sorting algorithms (`sortCategories`, `sortTransactions`), verifies XSS escaping (`escapeHtml`), asserts CSRF header dispatch in `apiFetch`, verifies multi-tab logout sync and safe local return-route sanitization, and asserts role-based element rendering rules (`Admin` vs `User`).
2. **Backend Schema, Upgrade & Lifecycle Integration Verification (`tests/HomeBudget.Tests/DatabaseAndServicesIntegrationTests.cs`):**
   - Covers legacy v1/v2.0/v2.8 database upgrades (`ApplyIdempotentSchemaMigrationsAsync` including transactional rollback on simulated migration failure), CSV ingestion & provenance immutability, coalesced backup scheduling/pruning, verified restore from older backups lacking `Users`/`SecurityAuditLogs`, account-scoped Clear History, and Reset All Information—verifying authoritative preservation of **all** `Users` rows (active and inactive), `Settings.AuthInitialized`, `SecurityAuditLogs`, and backup retention policies under the exclusive lifecycle mutex.
3. **Authentication, RBAC & Browser-Security Negative Verification (`tests/HomeBudget.Tests/AuthServiceTests.cs` & `AuthAndSecurityIntegrationTests.cs`):**
   - **Route Authorization Matrix & Fail-Closed Guard:** Reflection/endpoint-catalog test failing if any mapped `/api/*` route lacks an explicit policy/allowlist, plus full positive and negative request matrix across Anonymous, `User`, and `Admin` identities for all endpoints (including `/api/settings`, `/api/rules/back-propagate`, `/api/users/*`, `/api/audit-logs`, `/api/backups/*`, `/api/uploads/*`, `/api/transactions/*`).
   - **Setup, Lockout & Rate Limiting:** Invalid/reused bootstrap token rejection, concurrent `POST /api/auth/setup` race prevention, permanent disablement after setup (and after reset), rate-limit `429` enforcement, concurrent failed-login atomic counter integrity, lockout threshold/expiry boundaries, generic error equivalence, and Admin unlock.
   - **Passwords, Cookies, CSRF, Headers & Recovery:** `12`–`128` char UTF-8 & whitespace preservation, common-password rejection, `PasswordHasher<User>` `SuccessRehashNeeded` auto-upgrade on login, `RememberMe` persistent vs session cookie issuance and absolute lifetime ceiling, `HttpOnly`/`Secure`/`SameSite=Lax` flags, HTTPS enforcement, `SecurityStamp` revocation on password change/reset/deactivation, last-admin deactivation guard, missing `X-HomeBudget-Request` / invalid `Origin` / `Referer` / cross-origin CORS rejection (`403`), `Cache-Control: no-store` & CSP header checks, one-time consumed break-glass recovery, missing/corrupt Data Protection key safe failure, and audit log secret redaction assertions.



