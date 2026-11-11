# Local Home Budgeting & Cashflow System — Design Document (v2.8)

**Document Version:** 2.8  
**Target Platform:** Windows 11 IIS + ASP.NET Core (.NET 10 Minimal APIs) + SQLite (WAL Mode)  
**Network Scope:** Strictly LAN-Only (`LocalSubnet` via Windows Defender Firewall, Port 8080)  
**Primary Personas:**
- **User (Spouse / Mobile-First on iPhone):** Reviews monthly budgets, explores the Cashflow Sankey visualization, manages categories (add/edit/delete), and rapidly categorizes or recategorizes expenses directly from the Visualization and Budgets views with automated forward/backward rule propagation prompts and flexible list sorting.
- **Administrator (Desktop on Windows 11 PC):** Performs multi-file CSV statement ingestion (Chase, American Express, Citibank & Wells Fargo), renames and manages source accounts/cards, manages full category topology, rules, and local SQLite backup/restore lifecycle & policies, clears transaction history or resets all live database information to a clean state while preserving backup databases, and uses the filtered Transaction Inspector to edit, annotate (`Notes`), split, or delete income and expense entries (with tombstone hash protection).

> [!NOTE]
> **Household LAN Trust Model:** This system runs exclusively on a private household LAN behind a Windows Defender Firewall `LocalSubnet` rule. The **User** and **Administrator** personas represent ergonomic device/view workflows (mobile daily budgeting vs. desktop statement administration) rather than authenticated security boundaries; destructive operations are protected by explicit confirmation modals and automatic pre-operation SQLite snapshots rather than login/password gates.

---

## 1. Summary of Requirements & Architecture Evolution (v2.0 $\rightarrow$ v2.8)

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
| **Host OS & Web Server** | Windows 11 Pro/Home with IIS (`AspNetCoreModuleV2`, In-Process Hosting on Port `8080`) |
| **Backend Framework** | ASP.NET Core Minimal APIs targeting **.NET 10.0 (`net10.0`)** |
| **Database Engine** | **SQLite 3.53+** (`Microsoft.Data.Sqlite` `10.0.0` + `SQLitePCLRaw.lib.e_sqlite3` `3.53.3`) |
| **Concurrency & Durability** | `PRAGMA journal_mode=WAL;`, `PRAGMA synchronous=NORMAL;`, `PRAGMA foreign_keys=ON;`, `PRAGMA busy_timeout=5000;` + `BackupService` lifecycle `SemaphoreSlim` mutex |
| **Data Access Layer** | **Dapper** (`2.1.35`) with custom `SqliteDecimalHandler` and `SqliteBoolHandler` for strict `.NET 10` type safety |
| **Frontend Architecture** | Zero-build Vanilla ES6 Modules (`app.js`, view modules, custom responsive SVG `sankeyChart.js`), centralized list-sorting helpers & `escapeHtml()` sanitization, Mobile-First CSS |
| **Network Isolation** | Bound to `*:8080`; Windows Firewall rule restricts inbound TCP `8080` to `LocalSubnet` on `Private` profile |

---

## 3. Database Schema & Canonical Seed Data (DDL v2.6)

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

CREATE INDEX IF NOT EXISTS IX_Transactions_Date ON Transactions(TransactionDate);
CREATE INDEX IF NOT EXISTS IX_Transactions_Category ON Transactions(CategoryId);
CREATE INDEX IF NOT EXISTS IX_Transactions_Type ON Transactions(TransactionType);
CREATE INDEX IF NOT EXISTS IX_Transactions_Merchant ON Transactions(CleanMerchantName);
CREATE INDEX IF NOT EXISTS IX_Transactions_DedupHash ON Transactions(DedupHash);
CREATE INDEX IF NOT EXISTS IX_Transactions_UpdatedAt ON Transactions(UpdatedAt);
```

### 3.2 Canonical Seed Data & `DefaultTaxonomySeeded` Guard
Both `deploy/schema.sql` and `DatabaseInitializer` share a single canonical seed specification:
1. **Default Household Accounts (seeded idempotently):**
   - `AccountId = 1`: `'Wells Fargo Household Checking'` (`Institution = 'WellsFargo'`, `AccountType = 'Checking'`)
   - `AccountId = 2`: `'Chase Household Credit Card'` (`Institution = 'Chase'`, `AccountType = 'CreditCard'`)
   - `AccountId = 3`: `'American Express Credit Card'` (`Institution = 'Amex'`, `AccountType = 'CreditCard'`)
   - `AccountId = 4`: `'Citibank Credit Card'` (`Institution = 'Citibank'`, `AccountType = 'CreditCard'`)
2. **Default Settings (`INSERT OR IGNORE INTO Settings`):**
   - `PersistentMonthlyIncome = '10000.00'`, `GooglePayrollMatchPattern = 'GOOGLE'`, `CurrentMonthIncomeMode = 'Auto'`, `GeminiApiKey = ''`
   - `BackupAutoMode = 'Coalesced'`, `BackupDebounceSeconds = '30'`, `BackupMinIntervalMinutes = '10'`, `MaxRetainedBackups = '15'`
3. **One-Time Starter Categories (12) & Auto-Categorization Rules (15) Gated by `DefaultTaxonomySeeded`:**
   - Starter categories (`Housing & Utilities`, `Groceries`, `Costco`, `Amazon`, `Target`, `Walmart`, `Dining & Coffee`, `Transportation & Gas`, `Subscriptions`, `Healthcare`, `Shopping & Household`, `Entertainment & Misc`) and 15 starter merchant rules (`COSTCO`, `AMZN`, `AMAZON`, `TARGET`, `WAL-MART`, `WALMART`, `WHOLEFDS`, `TRADER JOE`, `SAFEWAY`, `MORTGAGE`, `PG&E`, `CHEVRON`, `SHELL`, `NETFLIX`, `COFFEE`) are seeded **only** when `Settings` does not yet contain `Key = 'DefaultTaxonomySeeded'`.
   - Immediately after initial seeding (or after `ResetAllDatabaseAsync` even when `ReseedDefaultCategoriesAndRules = false`), `('DefaultTaxonomySeeded', '1')` is recorded in `Settings` so subsequent server restarts never resurrect deleted default categories or rules.

### 3.3 Idempotent Schema Upgrade & Startup Ordering Strategy (`DatabaseInitializer`)
On startup, `DatabaseInitializer.InitializeAsync()` inspects whether legacy tables already exist:
1. **Pre-Schema Migration on Existing Databases:** If `Transactions` or `Accounts` already exists in `sqlite_master`, `ApplyIdempotentSchemaMigrationsAsync()` executes **before** running `schema.sql` so that migrated columns (`UpdatedAt`, `CreatedAt`, `CategorizationSource`, `UploadBatchId`, `DeletedTransactionHashes.AccountId`) are added prior to creating `IX_Transactions_UpdatedAt` or other indexes.
2. **Column Backfills & Table Normalization (`ApplyIdempotentSchemaMigrationsAsync`):**
   - Ensures `StagingSessions` and `Settings` exist and prunes expired staging sessions older than 24 hours.
   - Adds missing `CreatedAt` and/or `UpdatedAt` columns to `Accounts`, `Categories`, `CategorizationRules`, `IncomeOverrides`, and `Transactions`, backfilling from existing timestamps or `TransactionDate`.
   - Adds missing `AccountId INTEGER NULL` to `DeletedTransactionHashes` and backfills `AccountId` from `DedupHash` or matching transactions where possible.
   - Adds missing `CategorizationSource` and `UploadBatchId` to `Transactions`, backfilling `CategorizationSource` (`'Manual'`, `'Rule'`, or `'Uncategorized'`).
   - Rebuilds `Accounts` under `PRAGMA foreign_keys = OFF` if a legacy v1 `CHECK (Institution IN ('Chase', 'WellsFargo'))` constraint is detected, allowing arbitrary institutions/account types.
   - Rebuilds `Transactions` under `PRAGMA foreign_keys = OFF` if a legacy v1 `UploadBatches` foreign key is present and drops the obsolete `UploadBatches` table.
   - Normalizes legacy `TransactionType` values (`'Payment'`, `'Transfer'`) to `'CreditCardPayoff'` and marks `DefaultTaxonomySeeded = '1'` on existing populated databases.

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
   - Both restore workflows include a pre-checked option to **create an automatic `pre_restore` safety backup** of the current live database first.
   - The backend verifies the target snapshot file is non-empty and passes `PRAGMA integrity_check;`, creates the optional `pre_restore` snapshot (aborting if requested and snapshot creation fails), uses `SqliteConnection.BackupDatabase` to atomically copy the snapshot into the live `budget.db`, and runs `DatabaseInitializer.ApplyIdempotentSchemaMigrationsAsync` so even older snapshots upgrade cleanly to the current schema.
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
   - Executes under `BackupService.ExecuteExclusiveLifecycleActionAsync` (canceling any pending debounce timer and holding the backup/lifecycle mutex) and uses `DatabaseInitializer.RecreateCleanSchemaAsync` to drop all user tables (including any legacy `UploadBatches` table) under `PRAGMA foreign_keys = OFF` and recreate a clean v2.6 schema.
   - **Strict Backup & Policy Preservation Guarantee:**
     - Resetting all live database information **never deletes or modifies any backup `.db` files** in `Backups/`.
     - Preserves the user's configured backup policy settings (`BackupAutoMode`, `BackupDebounceSeconds`, `BackupMinIntervalMinutes`, `MaxRetainedBackups`) across the reset so subsequent snapshots never prune existing backup files back down to `15`.
     - If `CreateBackupFirst = true` and the `"pre_reset_all"` safety snapshot fails, the reset aborts immediately without modifying the database.
     - Records `('DefaultTaxonomySeeded', '1')` in `Settings` regardless of whether `ReseedDefaultCategoriesAndRules` is checked or unchecked, ensuring a clean zero-category reset stays empty across future application restarts.
   - Presents a dedicated confirmation modal with:
     - **`[x] Back up database before resetting ("pre_reset_all" snapshot)`** *(Checked by default)*.
     - **`[x] Re-seed default starter Categories (12) & Auto-Categorization Rules (15)`** *(Checked by default; unchecking starts with 0 categories and 0 rules)*.
     - **Explicit Confirmation Checkbox (`#resetAllConfirmCheck`):** Requires the administrator to check *"I understand this will reset all live database information (backup databases will NOT be deleted)"* before the reset button is enabled.

---

## 5. Updated API Contract Summary (v2.8)

| Method & Route | Persona | Description | Backup Behavior (`Coalesced` Default) |
| :--- | :--- | :--- | :--- |
| `GET /api/accounts` | Both | List active bank and credit card accounts. | None |
| `POST /api/accounts` | Admin | Create a new account (`AccountName`, `Institution`, `AccountType`). | Coalesced (`account_create`) |
| `PUT /api/accounts/{id}` | Admin / Both | Rename or edit source account display name, institution format, and account type. | Coalesced (`account_rename`) |
| `GET /api/categories` | Both | List active categories with optional `?sortBy=alpha_asc\|recent\|custom_order\|budget_desc`. | None |
| `POST /api/categories` | Both | Create a new category. | Coalesced (`category_create`) |
| `PUT /api/categories/{id}` | Both | Edit category properties and update `UpdatedAt`. | Coalesced (`category_update`) |
| `PUT /api/categories/{id}/budget` | Both | Update a category's `MonthlyBudget` target and `UpdatedAt`. | Coalesced (`budget_edit`) |
| `DELETE /api/categories/{id}` | Both | Delete category (optional `?replacementCategoryId=` to reassign transactions/rules). | **Major** (`category_delete`) |
| `POST /api/categories/{id}/merge-into/{targetId}` | Both | Merge source category into target category and update target `UpdatedAt`. | **Major** (`category_merge`) |
| `GET /api/topology` | Both | Return all active categories and `CategorizationRules`. | None |
| `POST /api/rules` | Both | Create a categorization rule. | Coalesced (`rule_create`) |
| `PUT /api/rules/{id}` | Both | Update an existing categorization rule and `UpdatedAt`. | Coalesced (`rule_update`) |
| `DELETE /api/rules/{id}` | Both | Delete a categorization rule. | Coalesced (`rule_delete`) |
| `POST /api/rules/back-propagate` | Both | Dry-run or commit retroactive rule evaluation across historical transactions. | Coalesced (when `dryRun = false`, `backprop`) |
| `POST /api/uploads/stage` | Admin | Parse Chase/Wells Fargo CSV files, classify rows, compute `DedupHash`, and persist staging session in SQLite. | None |
| `POST /api/uploads/commit` | Admin | Commit approved staged rows from SQLite `StagingSessions` into `Transactions` / `DeletedTransactionHashes`. | **Major** (`csv_upload`) |
| `POST /api/transactions/{id}/categorize` | Both | Categorize or recategorize an expense with non-mutually exclusive `applyMovingForward` and `applyToPast` flags. | Coalesced (`recategorize`) |
| `GET /api/transactions/similar-count` | Both | Returns count of past transactions matching `pattern`. | None |
| `GET /api/transactions` | Both / Admin | Query transactions with filters and `sortBy` (`date_desc`, `date_asc`, `recent_changes`, `merchant_asc`, `merchant_desc`, `amount_desc`, `amount_asc`). | None |
| `PUT /api/transactions/{id}` | Admin | Edit transaction fields and `Notes` (single consolidated backup request even when creating/back-propagating a rule). | Coalesced (`data_edit`) |
| `POST /api/transactions/{id}/split` | Admin | Split a transaction into multiple categorized child legs. | **Major** (`transaction_split`) |
| `DELETE /api/transactions/{id}` | Admin | Delete an income or expense transaction (with `?addToTombstone=true/false`). | Coalesced (`transaction_delete`) |
| `POST /api/transactions/bulk-delete` | Admin | Bulk-delete selected income/expense transactions in one atomic transaction. | **Major** (`bulk_delete`) |
| `POST /api/transactions/clear-history` | Admin | Clear all transactions (or a single account's transactions), optionally resetting account-scoped `DeletedTransactionHashes` and `StagingSessions`, with an optional pre-clear backup (`createBackupFirst = true`). | **Immediate Pre-Clear** (`pre_clear_history`) |
| `POST /api/admin/reset-all` | Admin | Reset all live database tables to a clean state (optionally creating a `pre_reset_all` snapshot first and optionally re-seeding default starter categories/rules) while preserving all backup databases and backup retention policy. | **Immediate Pre-Reset** (`pre_reset_all`) |
| `GET /api/cashflow` | Both | Returns cashflow summary, Sankey nodes/links, `uncategorizedTransactions`, and `?categorySort=` ordering. | None |
| `GET /api/budgets/{yearMonth?}` | Both | Returns monthly budget summary, category progress cards, and uncategorized transactions for the month. | None |
| `GET /api/settings` | Both | Returns income configuration and monthly overrides. | None |
| `PUT /api/settings` | Admin | Updates persistent monthly income, payroll match pattern, and income mode. | Coalesced (`settings_update`) |
| `PUT /api/settings/income-overrides` | Admin | Upserts a monthly income override for a specific `YearMonth`. | Coalesced (`income_override`) |
| `DELETE /api/settings/income-overrides/{yearMonth}` | Admin | Deletes a monthly income override. | Coalesced (`income_override_delete`) |
| `GET /api/backups` | Admin | Returns backup directory path, active policy `config`, storage/queue `status`, and `backups` list. | None |
| `PUT /api/backups/config` | Admin | Updates `BackupAutoMode`, `BackupDebounceSeconds`, `BackupMinIntervalMinutes`, and `MaxRetainedBackups`, and prunes excess snapshots. | None |
| `POST /api/backups/trigger` | Admin | Immediately flushes any pending coalesced changes and creates a verified manual snapshot. | **Immediate** (`manual`) |
| `GET /api/backups/download/{fileName}` | Admin | Downloads a specific `.db` backup file. | None |
| `DELETE /api/backups/{fileName}` | Admin | Deletes a single `.db` backup snapshot from disk. | None |
| `POST /api/backups/bulk-delete` | Admin | Deletes multiple selected `.db` backup snapshots in one operation. | None |
| `POST /api/backups/prune` | Admin | Prunes older snapshots so only the `keepCount` newest `.db` files remain. | None |
| `POST /api/backups/restore/{fileName}` | Admin | Verifies snapshot integrity, optionally creates a `pre_restore` safety snapshot, atomically restores `budget.db`, and runs schema migrations. | **Immediate** (`pre_restore`) |
| `POST /api/backups/restore-upload` | Admin | Uploads an external `.db` snapshot file, verifies SQLite integrity, optionally creates a `pre_restore` safety snapshot, and restores `budget.db`. | **Immediate** (`pre_restore`) |

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
| 🛡️ Backup Databases Preserved: Existing SQLite backup snapshots in   |
|    Backups/ are NEVER deleted by this operation.                      |
|                                                                       |
| [x] Back up current database first ("pre_reset_all" safety snapshot)  |
|                                                                       |
| [x] Re-seed default starter Categories (12) & Rules (15)              |
|     (Uncheck to start with zero categories and zero rules)            |
|                                                                       |
| [x] I understand this will reset all live database information        |
|     (backup databases will NOT be deleted)                            |
|                                                                       |
|                      [ Cancel ]  [ Backup & Reset All Information ]   |
+-----------------------------------------------------------------------+
```

---

## 7. Trust Model, Error Handling & Verification Strategy

### 7.1 Household LAN Trust Model & Persona Boundaries
- **Network Boundary:** The service is deployed on a trusted home PC behind a Windows Defender Firewall rule restricting TCP port `8080` to `LocalSubnet` (`Private` network profile).
- **Ergonomic Personas:** Because only household members on the local Wi-Fi/LAN can reach the application, endpoints do not require user authentication tokens. Instead, destructive administrative actions (`Reset All Information`, `Clear Transaction History`, `Restore Database`, `Bulk Delete`) enforce multi-step confirmation modals and pre-checked SQLite safety snapshots (`pre_reset_all`, `pre_clear_history`, `pre_restore`).

### 7.2 Error Handling & Diagnostic Reporting Contract
- **Structured Problem Details (`Results.Problem` / `{ error }`):** Endpoints performing database mutations or administrative operations catch `InvalidOperationException` and `SqliteException`, log full diagnostic context on the server, and return structured JSON (`{ error }` or RFC 7807 `ProblemDetails` with `detail` and `title`).
- **Frontend `apiFetch` Extraction (`app.js`):** The client `apiFetch` wrapper parses `errObj.error || errObj.detail || errObj.title` from non-2xx JSON responses and displays actionable error messages in toast notifications so database or validation errors are immediately visible.

### 7.3 Automated Verification & Upgrade Testing Strategy
1. **Frontend Module & Contract Verification (`node tests/frontend_verification.mjs`):**
   - Verifies ES6 syntax (`node --check`) across all frontend modules (`app.js`, `sankeyChart.js`, and all view modules), validates sorting algorithms (`sortCategories`, `sortTransactions`), verifies XSS escaping (`escapeHtml`), and asserts that all required modal and toolbar element IDs (`#resetAllDatabaseAdminBtn`, `#clearHistoryAdminBtn`, `#restoreDatabaseModalBtn`, `#backPropSortSelect`, etc.) are mounted.
2. **Backend Integration & Schema Upgrade Verification (`tests/HomeBudget.Tests`):**
   - Covers legacy v1/v2.0 database migration (`ApplyIdempotentSchemaMigrationsAsync`, including `UploadBatches` FK removal, `Accounts` `CHECK` removal, and `DeletedTransactionHashes.AccountId`), CSV ingestion & provenance immutability, coalesced backup scheduling/pruning, verified restore, account-scoped Clear History, and Reset All Information (both with and without default re-seeding, verifying backup retention policy preservation and non-resurrection across restarts).



