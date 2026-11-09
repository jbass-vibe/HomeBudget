# Local Home Budgeting & Cashflow System — Design Document (v2.1)

**Document Version:** 2.1  
**Target Platform:** Windows 11 IIS + ASP.NET Core (.NET 10 Minimal APIs) + SQLite (WAL Mode)  
**Network Scope:** Strictly LAN-Only (`LocalSubnet` via Windows Defender Firewall, Port 8080)  
**Primary Personas:**
- **User (Spouse / Mobile-First on iPhone):** Reviews monthly budgets, explores the Cashflow Sankey visualization, manages categories (add/edit/delete), and rapidly categorizes or recategorizes expenses directly from the Visualization and Budgets views with automated forward/backward rule propagation prompts.
- **Administrator (Desktop on Windows 11 PC):** Performs multi-file CSV statement ingestion (Chase & Wells Fargo), manages full category topology and rules, and uses the filtered Transaction Inspector to edit, annotate (`Notes`), split, or delete income and expense entries (with tombstone hash protection).

---

## 1. Summary of Requirements & v2.1 Architecture Refinements

### 1.1 Core v2.0 Product Capabilities
1. **Full Category Lifecycle & Dual-Persona Access (Add, Edit, Delete + Automatic DB Backup):**
   - Both the **User (iPhone/Mobile)** and **Administrator (Desktop)** can **add, edit, and delete** categories from any category management surface or inline during expense categorization.
   - Deleting a category supports either reassigning its existing transactions/rules to a replacement category or setting those transactions back to `Uncategorized` (`CategoryId = NULL`, `CategorizationSource = 'Uncategorized'`, `IsCategoryManuallyLocked = 0`).
   - Every category addition, edit, deletion, or merge immediately triggers an atomic event-driven SQLite snapshot (`VACUUM INTO`).

2. **Expense Recategorization with Non-Mutually Exclusive Forward & Historical Propagation Prompts:**
   - Whenever a user or administrator categorizes an uncategorized expense or recategorizes an existing expense, the UI presents an interactive confirmation prompt with two independent (non-mutually exclusive) checkboxes:
     - **Option A (Forward / Moving Forward):** *"Categorize similar expenses based on this change moving forward?"* — Creates or updates a persistent `CategorizationRule` (matched on normalized merchant name / pattern) so all future CSV uploads automatically assign this category.
     - **Option B (Historical / Past):** *"Recategorize similar expenses in the past?"* — Immediately runs a retroactive back-propagation query across existing transactions matching the merchant/pattern and updates their `CategoryId` and `CategorizationSource = 'Rule'` (with optional override for previously locked rows).
   - Users can check **neither** (one-off change for this transaction only), **Option A only**, **Option B only**, or **both Option A and Option B** simultaneously.
   - Executing this change triggers an immediate SQLite database backup.

3. **Administrator Income & Expense Editing, Notes & Deletion with Multi-Criteria Filtering:**
   - The Administrator can **edit, split, or delete both Income (`IncomeDeposit`) and Expense (`Expense` / `Refund` / `CreditCardPayoff`) transactions** from the database via the **Transaction Inspector**.
   - Real-time, combinable filters include:
     - **Date Range:** Start Date & End Date pickers (plus quick presets: This Month, Last Month, Last 3M, YTD, All Dates).
     - **Transaction Type:** Canonical vocabulary (`All`, `Expense`, `IncomeDeposit`, `Refund`, `CreditCardPayoff`).
     - **Category:** Specific category dropdown, plus an **`Uncategorized Only`** quick-toggle pill.
     - **Account:** Filter by specific bank/credit card account.
     - **Amount Range:** Optional `Min Amount` / `Max Amount` filter.
     - **Search Query:** Instant substring filter across `RawDescription`, `CleanMerchantName`, `Subcategory`, and `Notes`.
   - When deleting an income or expense record (individually or in bulk), the Administrator can choose whether to record its `DedupHash` in `DeletedTransactionHashes` (Tombstone table, enabled by default) so future overlapping CSV uploads do not resurrect the deleted entry.

4. **Visualization Page (`Uncategorized` Triage & Inline Categorization Workflow):**
   - On the **Cashflow & Sankey Visualization** page, if any expenses in the active time window are marked **`Uncategorized`**, the `Uncategorized` node in the Sankey diagram and a dedicated **"Uncategorized Expenses Triage"** card below the diagram are highlighted.
   - Supports toggling between **Merchant-Grouped View** (aggregating repeated uncategorized charges by `CleanMerchantName` with item counts and group subtotals) and **Chronological List View**.
   - Categorizing from the triage panel updates the transaction(s), applies selected forward/past propagation options, creates a database backup, and live-refreshes the Sankey diagram in real time.

### 1.2 v2.1 Review Refinements (Correctness, Alignment & Operational Safety)
1. **Canonical Transaction Type Vocabulary:**
   - Standardized across database schema, C# services, API validation, and frontend views around four canonical values:
     - `'Expense'` (Positive amount: household spending outflow)
     - `'Refund'` (Negative amount: merchant return, credit, or non-payroll reimbursement netted against spending)
     - `'IncomeDeposit'` (Positive amount: verified payroll / household income deposit)
     - `'CreditCardPayoff'` (Positive amount: credit card payment or inter-account payoff transfer excluded from both spending and income totals to prevent double-counting)
2. **Schema & Design Alignment + Idempotent Schema Evolution:**
   - Reconciles `schema.sql` and C# entities (`IsBigBoxMerchantCategory`, `IsRegex`, `DefaultSubcategory`, `CategorizationSource`, `UploadBatchId`) and adds an idempotent column-migration check in `DatabaseInitializer` (`PRAGMA table_info`) so existing `budget.db` files automatically receive new columns (`CategorizationSource`, `StagingSessions` table) on startup without data loss.
3. **Hardened Wells Fargo & Chase CSV Classification (`CsvParserService`):**
   - Prevents non-payroll positive checking credits (e.g., merchant refunds, Zelle/Venmo reimbursements, account transfers) from being misclassified as `IncomeDeposit`. Only positive checking rows matching `GooglePayrollMatchPattern` are auto-classified as `IncomeDeposit`; other non-payoff positive credits default to `Refund` and are flagged with `RequiresManualReview = true` during upload staging.
4. **SQLite-Persisted Upload Staging Sessions (`StagingSessions`):**
   - Replaces volatile in-memory-only staging (`ConcurrentDictionary`) with SQLite persistence in the `StagingSessions` table (with automatic 24-hour expiration cleanup) so IIS application pool recycles or server restarts do not lose an in-progress CSV import review.
5. **Immutable Import Fingerprint (`DedupHash`) Contract:**
   - `DedupHash` is computed once at CSV parse time from the raw bank record (`AccountId|IsoDate|RawDescription|NormalizedAmount`) and is never mutated when the user edits `CleanMerchantName`, `TransactionDate`, `Amount`, `CategoryId`, `Subcategory`, or `Notes`.
   - Splitting a transaction preserves the base `DedupHash` on leg 1, assigns `{BaseHash}_SPLIT_{i}` to legs 2+, sets `ParentTransactionId`, and ensures tombstoning any split leg also tombstones the base hash.
6. **Frontend XSS Prevention (`escapeHtml` Rendering Contract):**
   - All externally sourced CSV fields and user-editable strings (`RawDescription`, `CleanMerchantName`, `AccountName`, `Category.Name`, `Subcategory`, `Notes`, `MatchPattern`, `FileName`) are escaped via a centralized `escapeHtml()` helper before DOM insertion via `innerHTML`.
7. **Verified Backup Reporting (`BackupService`):**
   - `BackupService` resolves its default directory relative to the configured SQLite database directory if `HomeBudget:BackupDirectoryPath` is not explicitly set, verifies the output snapshot file exists and is non-zero bytes after `VACUUM INTO`, logs failures at `LogError`, and returns an explicit status so the UI never displays a false-positive backup toast.
8. **Explicit Scope Boundaries (Single-Household LAN Appliance):**
   - The application is intentionally designed as a zero-login, single-household appliance bound strictly to the private home LAN (`LocalSubnet` firewall rule on TCP 8080). Multi-tenant user authentication/RBAC is an explicit **non-goal** to preserve zero-friction mobile access for the household.

---

## 2. System Architecture & Runtime Stack

| Layer | Technology / Specification |
| :--- | :--- |
| **Host OS & Web Server** | Windows 11 Pro/Home with IIS (`AspNetCoreModuleV2`, In-Process Hosting on Port `8080`) |
| **Backend Framework** | ASP.NET Core Minimal APIs targeting **.NET 10.0 (`net10.0`)** |
| **Database Engine** | **SQLite 3.53+** (`Microsoft.Data.Sqlite` `10.0.0` + `SQLitePCLRaw.lib.e_sqlite3` `3.53.3`) |
| **Concurrency & Durability** | `PRAGMA journal_mode=WAL;`, `PRAGMA synchronous=NORMAL;`, `PRAGMA foreign_keys=ON;`, `PRAGMA busy_timeout=5000;` |
| **Data Access Layer** | **Dapper** (`2.1.35`) with custom `SqliteDecimalHandler` and `SqliteBoolHandler` for strict `.NET 10` type safety |
| **Frontend Architecture** | Zero-build Vanilla ES6 Modules (`app.js`, view modules, custom responsive SVG `sankeyChart.js`), centralized `escapeHtml()` sanitization, Mobile-First CSS |
| **Network Isolation** | Bound to `*:8080`; Windows Firewall rule restricts inbound TCP `8080` to `LocalSubnet` on `Private` profile |

---

## 3. Database Schema (DDL v2.1)

Default Database file location: `C:\ProgramData\HomeBudget\budget.db` (configurable via `HomeBudget:DatabasePath`)  
Default Automated Backup directory: `<DatabaseDirectory>\Backups\` (e.g., `C:\ProgramData\HomeBudget\Backups\`, configurable via `HomeBudget:BackupDirectoryPath`)

### 3.1 Core Tables (`deploy/schema.sql`)

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS Accounts (
    AccountId       INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountName     TEXT    NOT NULL,
    Institution     TEXT    NOT NULL, -- 'WellsFargo', 'Chase'
    AccountType     TEXT    NOT NULL, -- 'Checking', 'CreditCard'
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
    CreatedAt                TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS CategorizationRules (
    RuleId              INTEGER PRIMARY KEY AUTOINCREMENT,
    MatchPattern        TEXT    NOT NULL,
    IsRegex             INTEGER NOT NULL DEFAULT 0, -- 0 = Case-insensitive substring ('Contains'), 1 = Regex
    CategoryId          INTEGER NOT NULL,
    DefaultSubcategory  TEXT    NULL,
    Priority            INTEGER NOT NULL DEFAULT 100, -- Lower number = higher priority (evaluated first)
    CreatedAt           TEXT    NOT NULL DEFAULT (datetime('now')),
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
```

### 3.2 Idempotent Schema Upgrade Strategy (`DatabaseInitializer`)
Because SQLite `CREATE TABLE IF NOT EXISTS` does not add new columns to pre-existing tables, `DatabaseInitializer.InitializeAsync()` runs a lightweight, idempotent schema evolution step after executing `schema.sql`:
1. Queries `PRAGMA table_info(Transactions);` and adds `CategorizationSource TEXT NOT NULL DEFAULT 'Uncategorized'` via `ALTER TABLE` if missing on an older database.
2. Backfills `CategorizationSource`: rows with `CategoryId IS NOT NULL` and `IsCategoryManuallyLocked = 1` $\rightarrow$ `'Manual'`; rows with `CategoryId IS NOT NULL` and `IsCategoryManuallyLocked = 0` $\rightarrow$ `'Rule'`; rows with `CategoryId IS NULL` $\rightarrow$ `'Uncategorized'`.
3. Ensures `StagingSessions` exists and prunes expired staging sessions older than 24 hours (`DELETE FROM StagingSessions WHERE CreatedAt < datetime('now', '-1 day');`).

---

## 4. Detailed Functional Specifications

### 4.1 Category Lifecycle & Unified Recategorization Propagation

#### A. Category CRUD (Both User & Administrator)
- **Where Accessible:**
  1. **Categories, Rules & Backups View** (`topologyView.js`).
  2. **Monthly Budgets View** (`budgetsView.js`: `+ Add Category` button at top, plus `Edit / Delete` gear icon on each category card).
  3. **Cashflow & Sankey View** (`cashflowView.js`: `+ Add Category` button in header and triage panel, `Edit / Delete` button in tabular breakdown, and `+ Create New Category...` inside quick-categorize dropdowns).
  4. **Transaction Inspector** (`inspectorView.js`: `+ Add New Category...` option inside category dropdowns and the Categorize modal).
- **Operations & Validation:**
  - **Add Category (`POST /api/categories`):** Validates non-empty `Name` (unique, case-insensitive) and `MonthlyBudget >= 0`. Inserts category and invokes `BackupService.CreateSnapshotImmediateAsync("category_create")`.
  - **Edit Category (`PUT /api/categories/{id}`):** Validates non-empty `Name` and `MonthlyBudget >= 0`. Updates `Name`, `IsBigBoxMerchantCategory`, `MonthlyBudget`, `SortOrder`, and `ColorHex`. Invokes `BackupService.CreateSnapshotImmediateAsync("category_update")`.
  - **Delete Category (`DELETE /api/categories/{id}?replacementCategoryId={optionalId}`):**
    - If `replacementCategoryId` is provided (and `!= id`): atomically reassigns all `Transactions` and `CategorizationRules` from `id` to `replacementCategoryId`, then deletes category `id`.
    - If `replacementCategoryId` is omitted: sets `CategoryId = NULL`, `CategorizationSource = 'Uncategorized'`, and `IsCategoryManuallyLocked = 0` on affected `Transactions`, deletes rules tied to `id`, and deletes category `id`.
    - Invokes `BackupService.CreateSnapshotImmediateAsync("category_delete")`.

#### B. Expense Categorization / Recategorization with Forward & Historical Options
Whenever a user or administrator assigns or changes the category of an expense (from **Cashflow & Sankey**, **Monthly Budgets**, or **Transaction Inspector**):

1. **Similarity Matching Pattern Resolution:**
   - Pre-fills the match pattern input with the transaction's `CleanMerchantName` (editable by the user).
   - Queries `GET /api/transactions/similar-count?pattern={pattern}&excludeTransactionId={id}` to display a live count: *"(N similar past expenses found)"*.

2. **Non-Mutually Exclusive Propagation Options:**
   - **[Checkbox A] Apply to similar expenses moving forward (`applyMovingForward: bool`)**
     - Upserts a rule in `CategorizationRules` (`IsRegex = 0`, `MatchPattern = pattern`, `CategoryId = targetCategoryId`, `DefaultSubcategory = COALESCE(subcategory, DefaultSubcategory)`).
     - Deterministic Rule Precedence: Rules are evaluated in ascending `Priority` order (`ORDER BY Priority ASC, LENGTH(MatchPattern) DESC, RuleId ASC`), so more specific rules at the same priority level win deterministically.
   - **[Checkbox B] Recategorize similar expenses in the past (`applyToPast: bool`)**
     - Updates existing `Expense` and `Refund` rows where `TransactionId != id` and `(CleanMerchantName LIKE %pattern% OR RawDescription LIKE %pattern%)`, setting `CategoryId = targetCategoryId`, `CategorizationSource = 'Rule'`, and `Subcategory = COALESCE(@Subcategory, Subcategory)`.
     - Respects `includeManuallyLockedInPast` (defaults to `true` in interactive recategorization so all historical instances of that merchant align).

3. **Unified Backend Endpoint (`POST /api/transactions/{id}/categorize`):**
   - Executes inline category creation (if requested), target transaction update (`CategorizationSource = 'Manual'`, `IsCategoryManuallyLocked = 1`), forward rule upsert, and historical back-propagation inside a single atomic SQLite transaction, followed by an immediate backup snapshot (`"recategorize"`).

---

### 4.2 CSV Ingestion, Bank Classification & Persistent Staging

#### A. Bank-Specific CSV Classification Rules (`CsvParserService`)
1. **Chase Credit Card (7-Column Headered CSV):**
   - **Credit Card Payoff (`'CreditCardPayoff'`):** `Type` column equals `"Payment"` (case-insensitive) OR `Description` contains `"PAYMENT THANK YOU"` or `"AUTOMATIC PAYMENT"`. Stored with positive `Math.Abs(csvAmt)`.
   - **Expense (`'Expense'`):** `csvAmt < 0` (and not a payment). Stored with positive `Math.Abs(csvAmt)`.
   - **Refund (`'Refund'`):** `csvAmt > 0` (and not a payment). Stored with negative `-Math.Abs(csvAmt)`.

2. **Wells Fargo Checking (5-Column Headerless CSV):**
   - **Credit Card Payoff (`'CreditCardPayoff'`):** `Description` matches known credit card payment patterns (`"CHASE CREDIT CRD"`, `"CREDIT CARD"`, or `"EPAY"` + `"CHASE"`). Stored with positive `Math.Abs(csvAmt)`.
   - **Verified Payroll / Income (`'IncomeDeposit'`):** `csvAmt > 0` AND `Description` matches the configured `GooglePayrollMatchPattern` (or explicit `"DIRECT DEP"` / `"PAYROLL"` keywords). Stored with positive `Math.Abs(csvAmt)`.
   - **Other Positive Checking Credits (`'Refund'` + Manual Review Flag):** `csvAmt > 0` that do **not** match payroll or credit card payoff patterns (e.g., merchant refunds, Zelle/Venmo reimbursements, mobile check deposits, or inter-bank transfers). Defaulted to `'Refund'` (`-Math.Abs(csvAmt)`) with `RequiresManualReview = true` in the staging table so the Administrator explicitly confirms whether each row is a `Refund`, `IncomeDeposit`, or `CreditCardPayoff` before committing.
   - **Expense (`'Expense'`):** `csvAmt < 0` (and not a credit card payoff). Stored with positive `Math.Abs(csvAmt)`.

#### B. Deduplication & Immutable Provenance (`DedupHash`)
- **Base Fingerprint:** `ComputeBaseHash(accountId, isoDate, rawDesc, normalizedAmt)` computes the first 24 hex characters of `SHA256("{accountId}|{isoDate}|{rawDesc.Trim().ToUpperInvariant()}|{normalizedAmt:F2}")`.
- **Same-Day Identical Charges:** Within a staged upload, the 1st occurrence uses `BaseDedupHash`, while the $k$-th identical occurrence on the same date/account/amount uses `{BaseDedupHash}_OCC_{k}` and is marked `DedupStatus = 'PotentialCollision'` for review.
- **Immutability Across Edits:** Subsequent edits to `TransactionDate`, `Amount`, `CleanMerchantName`, `TransactionType`, `CategoryId`, `Subcategory`, or `Notes` via the Transaction Inspector do **not** overwrite `DedupHash`, ensuring future CSV uploads covering the same date range still recognize the row as already imported (`SkippedDuplicate`).
- **Tombstone Protection on Splits & Deletes:** Deleting a transaction (or a split child leg) with `addToTombstone = true` records both the row's `DedupHash` and its base hash (stripping any `_SPLIT_n` suffix) in `DeletedTransactionHashes`.

#### C. SQLite-Persisted Staging (`IngestionStagingService`)
- `POST /api/uploads/stage` serializes the generated `StagingSessionDto` into the `StagingSessions` SQLite table.
- `POST /api/uploads/commit` loads the session from `StagingSessions` inside a transaction, applies the user's per-row decisions (`Import`, `Skip`, or `TombstoneDelete`), sets `CategorizationSource` (`'Manual'` if overridden during staging, `'Rule'` if matched by rule, `'Uncategorized'` otherwise), deletes the consumed `StagingSessions` row, and triggers a `"csv_upload"` backup snapshot.

---

### 4.3 Transaction Inspector (`inspectorView.js`)

1. **Multi-Criteria Filtering (`GET /api/transactions`):**
   - Supports combinable query parameters: `startDate`, `endDate`, `accountId`, `categoryId`, `uncategorizedOnly`, `transactionType` (`Expense`, `IncomeDeposit`, `Refund`, `CreditCardPayoff`), `minAmount`, `maxAmount`, `search` (matches `RawDescription`, `CleanMerchantName`, `Subcategory`, or `Notes`), `sortBy`, `page`, and `pageSize`.
2. **Full Field Editing (`PUT /api/transactions/{id}`):**
   - Inline table inputs for `TransactionDate`, `CleanMerchantName`, `TransactionType`, `Amount`, `CategoryId`, `Subcategory`, and **`Notes`**.
   - Saving an edit updates `CategorizationSource = 'Manual'` (if category is set), preserves `DedupHash`, and triggers a `"data_edit"` backup snapshot.
3. **Transaction Splitting (`POST /api/transactions/{id}/split`):**
   - Splits a parent transaction into $\ge 2$ child legs whose amounts sum to the parent's `Amount` (within $\$0.01$), each with its own `CategoryId`, `Subcategory`, and `Notes`.
4. **Single & Bulk Deletion (`DELETE /api/transactions/{id}`, `POST /api/transactions/bulk-delete`):**
   - Validates that `transactionIds` is non-empty and capped at a safe batch limit ($\le 500$ IDs per request).
   - Optionally records `DedupHash` (and base hash if split) in `DeletedTransactionHashes` and triggers a `"transaction_delete"` backup snapshot.

---

### 4.4 Visualization Page (`Cashflow & Sankey`) Uncategorized Expense Triage

On the **Cashflow & Sankey Visualization** page (`cashflowView.js`):

1. **Interactive `Uncategorized` Node in Sankey Diagram:**
   - When `Uncategorized` expenses exist in the selected time window, the Sankey diagram renders an **`Uncategorized`** node (`id: "cat_uncat"`) highlighted in amber (`#F59E0B`).
   - Tapping or clicking the **`Uncategorized`** node smoothly scrolls to the **Uncategorized Expenses Triage Panel** directly below the chart.

2. **Dedicated "Uncategorized Expenses Triage" Panel with Merchant Grouping Toggle:**
   - Displays a view-mode toggle pill: **`Group by Merchant (N groups)`** vs. **`Chronological List (M items)`**.
   - **Merchant-Grouped Mode:** Aggregates uncategorized transactions by `CleanMerchantName`, displaying the merchant name, transaction count badge, date span, total dollar amount, and a **`Categorize Merchant...`** action that opens the Unified Categorization Modal pre-populated with that merchant's pattern so all matching charges are categorized in one step.
   - **Chronological List Mode:** Lists individual uncategorized transactions ordered by date with inline category pickers and `Categorize...` buttons.

---

### 4.5 Security, Validation & Backup Reliability

1. **Frontend XSS Protection (`escapeHtml`):**
   - `app.js` exports `escapeHtml(str)` which converts `&`, `<`, `>`, `"`, and `'` to their HTML entities.
   - Every view module (`app.js`, `budgetsView.js`, `cashflowView.js`, `inspectorView.js`, `topologyView.js`, `uploadView.js`) escapes all database/CSV text before inserting into `innerHTML` templates.
2. **API Boundary Validation:**
   - Category creation/update rejects empty names or negative budgets (`400 Bad Request`).
   - Rule creation/update rejects empty `MatchPattern`, invalid regex syntax when `IsRegex = true`, or non-existent `CategoryId` (`400 Bad Request`).
   - Transaction updates validate `TransactionType` against the canonical set (`Expense`, `Refund`, `IncomeDeposit`, `CreditCardPayoff`) and verify referenced `CategoryId` exists.
3. **Backup Verification & Reporting (`BackupService`):**
   - `CreateSnapshotImmediateAsync(reason)` executes `VACUUM INTO`, verifies `File.Exists(fullPath)` and `new FileInfo(fullPath).Length > 0`, prunes snapshots beyond `MaxRetainedBackups` (default 30), and returns the created filename (or `null` if an error occurred).

---

## 5. Updated API Contract Summary (v2.1)

| Method & Route | Persona | Description | Triggers DB Backup? |
| :--- | :--- | :--- | :--- |
| `GET /api/accounts` | Both | List active bank and credit card accounts. | No |
| `POST /api/accounts` | Admin | Create a new account (`AccountName`, `Institution`, `AccountType`). | **Yes** (`account_create`) |
| `GET /api/categories` | Both | List active categories ordered by `SortOrder`. | No |
| `POST /api/categories` | Both | Create a new category (`Name`, `IsBigBoxMerchantCategory`, `MonthlyBudget`, `SortOrder`, `ColorHex`). | **Yes** (`category_create`) |
| `PUT /api/categories/{id}` | Both | Edit category properties. | **Yes** (`category_update`) |
| `PUT /api/categories/{id}/budget` | Both | Update a category's `MonthlyBudget` target. | **Yes** (`budget_edit`) |
| `DELETE /api/categories/{id}` | Both | Delete category (optional `?replacementCategoryId=` to reassign transactions/rules; otherwise sets transactions to `Uncategorized`). | **Yes** (`category_delete`) |
| `POST /api/categories/{id}/merge-into/{targetId}` | Both | Merge source category into target category. | **Yes** (`category_merge`) |
| `GET /api/topology` | Both | Return all active categories and ordered `CategorizationRules`. | No |
| `POST /api/rules` | Both | Create a categorization rule (validating regex syntax if `IsRegex = true`). | **Yes** (`rule_create`) |
| `PUT /api/rules/{id}` | Both | Update an existing categorization rule. | **Yes** (`rule_update`) |
| `DELETE /api/rules/{id}` | Both | Delete a categorization rule. | **Yes** (`rule_delete`) |
| `POST /api/rules/back-propagate` | Both | Dry-run or commit retroactive rule evaluation across historical transactions. | **Yes** (when `dryRun = false`, `backprop`) |
| `POST /api/uploads/stage` | Admin | Parse Chase/Wells Fargo CSV files, classify rows, compute `DedupHash`, and persist staging session in SQLite. | No |
| `POST /api/uploads/commit` | Admin | Commit approved staged rows from SQLite `StagingSessions` into `Transactions` / `DeletedTransactionHashes`. | **Yes** (`csv_upload`) |
| `POST /api/transactions/{id}/categorize` | Both | Categorize or recategorize an expense (optionally creating a new category inline) with non-mutually exclusive `applyMovingForward` and `applyToPast` flags. | **Yes** (`recategorize`) |
| `GET /api/transactions/similar-count` | Both | Returns count of past transactions matching `pattern` to preview in the categorization modal. | No |
| `GET /api/transactions` | Both / Admin | Query transactions with filters: `startDate`, `endDate`, `accountId`, `categoryId`, `uncategorizedOnly`, `transactionType`, `minAmount`, `maxAmount`, `search`, `sortBy`, `page`, `pageSize`. | No |
| `PUT /api/transactions/{id}` | Admin | Edit transaction fields (`Amount`, `TransactionDate`, `TransactionType`, `CleanMerchantName`, `CategoryId`, `Subcategory`, `Notes`). | **Yes** (`data_edit`) |
| `POST /api/transactions/{id}/split` | Admin | Split a transaction into multiple categorized child legs. | **Yes** (`data_edit`) |
| `DELETE /api/transactions/{id}` | Admin | Delete an income or expense transaction (with `?addToTombstone=true/false`). | **Yes** (`transaction_delete`) |
| `POST /api/transactions/bulk-delete` | Admin | Bulk-delete selected income/expense transactions in one atomic transaction. | **Yes** (`transaction_delete`) |
| `GET /api/cashflow` | Both | Returns cashflow summary, Sankey nodes/links, and `uncategorizedTransactions` for the active window. | No |
| `GET /api/budgets/{yearMonth?}` | Both | Returns monthly budget summary, category progress cards, and uncategorized transactions for the month. | No |
| `GET /api/settings` | Both | Returns income configuration and monthly overrides. | No |
| `PUT /api/settings` | Admin | Updates persistent monthly income, payroll match pattern, and income mode. | **Yes** (`settings_update`) |
| `PUT /api/settings/income-overrides` | Admin | Upserts a monthly income override for a specific `YearMonth`. | **Yes** (`income_override`) |
| `DELETE /api/settings/income-overrides/{yearMonth}` | Admin | Deletes a monthly income override. | **Yes** (`income_override_delete`) |
| `GET /api/backups` | Admin | Lists available SQLite backup snapshots and backup directory path. | No |
| `POST /api/backups/trigger` | Admin | Triggers an immediate manual SQLite backup snapshot. | **Yes** (`manual`) |
| `GET /api/backups/download/{fileName}` | Admin | Downloads a specific `.db` backup file. | No |

---

## 6. UI/UX Wireframe Flows

### 6.1 Unified Categorization / Recategorization Modal
```
+-----------------------------------------------------------------------+
| Categorize Expense                                                [X] |
+-----------------------------------------------------------------------+
| Transaction:  2026-03-14 • TRADER JOE'S #124                $84.52    |
|                                                                       |
| 1. Choose Category:                                                   |
|    [ Groceries       ▼ ]   or   [ + New Category ]                    |
|                                                                       |
|    (If "+ New Category" clicked, expands inline:)                     |
|    New Category Name: [ Specialty Groceries ]  Budget: [ $300.00 ]    |
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

### 6.2 Visualization Page (`Cashflow & Sankey`) Uncategorized Triage Section
```
+-----------------------------------------------------------------------+
| Cashflow & Sankey Visualization                                       |
| [ Current MTD ] [ Prior Month ] [ Last 3M ] [ Last 6M ] [ Custom ]    |
+-----------------------------------------------------------------------+
|   [ S A N K E Y    D I A G R A M   (Tap "Uncategorized" node) ]       |
+-----------------------------------------------------------------------+
| ⚠️ Uncategorized Expenses Triage (4 items • $277.30)                  |
| View: [ Group by Merchant (3) ] [ Chronological List (4) ]            |
|-----------------------------------------------------------------------|
| COSTCO GAS (2 charges • Mar 11 – Mar 18)   | $124.80 | [ Categorize ] |
| APPLE.COM/BILL (1 charge • Mar 15)         |  $14.99 | [ Categorize ] |
| CITY WATER UTILITY (1 charge • Mar 11)     | $137.51 | [ Categorize ] |
+-----------------------------------------------------------------------+
```
