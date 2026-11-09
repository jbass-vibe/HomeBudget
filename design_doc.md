# Local Home Budgeting & Cashflow System — Design Document (v2.2)

**Document Version:** 2.2  
**Target Platform:** Windows 11 IIS + ASP.NET Core (.NET 10 Minimal APIs) + SQLite (WAL Mode)  
**Network Scope:** Strictly LAN-Only (`LocalSubnet` via Windows Defender Firewall, Port 8080)  
**Primary Personas:**
- **User (Spouse / Mobile-First on iPhone):** Reviews monthly budgets, explores the Cashflow Sankey visualization, manages categories (add/edit/delete), and rapidly categorizes or recategorizes expenses directly from the Visualization and Budgets views with automated forward/backward rule propagation prompts and flexible list sorting.
- **Administrator (Desktop on Windows 11 PC):** Performs multi-file CSV statement ingestion (Chase & Wells Fargo), manages full category topology and rules, and uses the filtered Transaction Inspector to edit, annotate (`Notes`), split, or delete income and expense entries (with tombstone hash protection).

---

## 1. Summary of Requirements & Architecture Evolution (v2.0 $\rightarrow$ v2.2)

### 1.1 Core Product Capabilities
1. **Full Category Lifecycle & Dual-Persona Access (Add, Edit, Delete + Automatic DB Backup):**
   - Both the **User (iPhone/Mobile)** and **Administrator (Desktop)** can **add, edit, and delete** categories from any category management surface or inline during expense categorization.
   - Deleting a category supports either reassigning its existing transactions/rules to a replacement category or setting those transactions back to `Uncategorized` (`CategoryId = NULL`, `CategorizationSource = 'Uncategorized'`, `IsCategoryManuallyLocked = 0`).
   - Every category addition, edit, deletion, or merge immediately triggers an atomic event-driven SQLite snapshot (`VACUUM INTO`).

2. **Expense Recategorization with Non-Mutually Exclusive Forward & Historical Propagation Prompts:**
   - Whenever a user or administrator categorizes an uncategorized expense or recategorizes an existing expense, the UI presents an interactive confirmation prompt with two independent (non-mutually exclusive) checkboxes:
     - **Option A (Forward / Moving Forward):** *"Categorize similar expenses based on this change moving forward?"* — Creates or updates a persistent `CategorizationRule` (matched on normalized merchant name / pattern) so all future CSV uploads automatically assign this category.
     - **Option B (Historical / Past):** *"Recategorize similar expenses in the past?"* — Immediately runs a retroactive back-propagation query across existing transactions matching the merchant/pattern and updates their `CategoryId`, `CategorizationSource = 'Rule'`, and `UpdatedAt = datetime('now')` (with optional override for previously locked rows).
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

5. **Comprehensive List Management & Multi-Mode Sorting (New in v2.2):**
   - Every major list, dropdown menu, visualization node column, and table across the application provides consistent, user-controllable sorting options—including **Alphabetical (A $\rightarrow$ Z / Z $\rightarrow$ A)**, **Recent Changes (`UpdatedAt DESC`)**, **Amount / Spend (High $\rightarrow$ Low / Low $\rightarrow$ High)**, and **Custom / Priority Order**:
     - **Category Dropdown Menus (`<select>` pickers across all modals, triage rows, upload staging, and filters):** Can be sorted **Alphabetically (A $\rightarrow$ Z)**, by **Recent Changes / Recently Used (`UpdatedAt DESC`)**, by **Custom Sort Order (`SortOrder ASC`)**, or by **Budget Amount (`MonthlyBudget DESC`)**.
     - **Cashflow Sankey Diagram & Tabular Breakdown:** Category nodes in the Sankey visualization and rows in the companion cashflow table can be sorted by **Highest Spend (`spend_desc`)**, **Lowest Spend (`spend_asc`)**, **Alphabetical (`alpha_asc`)**, **Recent Changes (`recent`)**, or **Custom Order (`custom_order`)**.
     - **Monthly Budget Cards:** Sortable by **Custom Order**, **Alphabetical (A $\rightarrow$ Z)**, **Recent Changes**, **% Budget Used (Over-Budget First)**, **Highest Spend**, or **Lowest Remaining Budget**.
     - **Uncategorized Triage, Categorization Rules & Transaction Inspector:** Support sorting by **Alphabetical**, **Recent Changes (`UpdatedAt DESC`)**, **Date**, and **Amount / Priority**.

### 1.2 Operational Safety & Data Integrity Refinements
1. **Canonical Transaction Type Vocabulary:**
   - Standardized across database schema, C# services, API validation, and frontend views around four canonical values:
     - `'Expense'` (Positive amount: household spending outflow)
     - `'Refund'` (Negative amount: merchant return, credit, or non-payroll reimbursement netted against spending)
     - `'IncomeDeposit'` (Positive amount: verified payroll / household income deposit)
     - `'CreditCardPayoff'` (Positive amount: credit card payment or inter-account payoff transfer excluded from both spending and income totals to prevent double-counting)
2. **Schema & Design Alignment + Idempotent Schema Evolution:**
   - Reconciles `schema.sql` and C# entities (`IsBigBoxMerchantCategory`, `IsRegex`, `DefaultSubcategory`, `CategorizationSource`, `UploadBatchId`, `UpdatedAt`) and runs an idempotent column-migration check in `DatabaseInitializer` (`PRAGMA table_info`) so existing `budget.db` files automatically receive new columns (`CategorizationSource`, `UpdatedAt`, `StagingSessions` table) on startup without data loss.
3. **Hardened Wells Fargo & Chase CSV Classification (`CsvParserService`):**
   - Prevents non-payroll positive checking credits (e.g., merchant refunds, Zelle/Venmo reimbursements, account transfers) from being misclassified as `IncomeDeposit`. Only positive checking rows matching `GooglePayrollMatchPattern` are auto-classified as `IncomeDeposit`; other non-payoff positive credits default to `Refund` and are flagged with `RequiresManualReview = true` during upload staging.
4. **SQLite-Persisted Upload Staging Sessions (`StagingSessions`):**
   - Persists staging sessions in the SQLite `StagingSessions` table (with automatic 24-hour expiration cleanup) so IIS application pool recycles or server restarts do not lose an in-progress CSV import review.
5. **Immutable Import Fingerprint (`DedupHash`) Contract:**
   - `DedupHash` is computed once at CSV parse time from the raw bank record (`AccountId|IsoDate|RawDescription|NormalizedAmount`) and is never mutated when the user edits `CleanMerchantName`, `TransactionDate`, `Amount`, `CategoryId`, `Subcategory`, or `Notes`.
6. **Frontend XSS Prevention (`escapeHtml` Rendering Contract) & Verified Backups (`BackupService`):**
   - All externally sourced CSV fields and user-editable strings are escaped via `escapeHtml()` before DOM insertion.
   - `BackupService` verifies snapshot file existence and non-zero size after `VACUUM INTO` and returns accurate status to the caller.

---

## 2. System Architecture & Runtime Stack

| Layer | Technology / Specification |
| :--- | :--- |
| **Host OS & Web Server** | Windows 11 Pro/Home with IIS (`AspNetCoreModuleV2`, In-Process Hosting on Port `8080`) |
| **Backend Framework** | ASP.NET Core Minimal APIs targeting **.NET 10.0 (`net10.0`)** |
| **Database Engine** | **SQLite 3.53+** (`Microsoft.Data.Sqlite` `10.0.0` + `SQLitePCLRaw.lib.e_sqlite3` `3.53.3`) |
| **Concurrency & Durability** | `PRAGMA journal_mode=WAL;`, `PRAGMA synchronous=NORMAL;`, `PRAGMA foreign_keys=ON;`, `PRAGMA busy_timeout=5000;` |
| **Data Access Layer** | **Dapper** (`2.1.35`) with custom `SqliteDecimalHandler` and `SqliteBoolHandler` for strict `.NET 10` type safety |
| **Frontend Architecture** | Zero-build Vanilla ES6 Modules (`app.js`, view modules, custom responsive SVG `sankeyChart.js`), centralized list-sorting helpers & `escapeHtml()` sanitization, Mobile-First CSS |
| **Network Isolation** | Bound to `*:8080`; Windows Firewall rule restricts inbound TCP `8080` to `LocalSubnet` on `Private` profile |

---

## 3. Database Schema (DDL v2.2)

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
CREATE INDEX IF NOT EXISTS IX_Transactions_UpdatedAt ON Transactions(UpdatedAt);
```

### 3.2 Idempotent Schema Upgrade Strategy (`DatabaseInitializer`)
On startup, `DatabaseInitializer.InitializeAsync()` executes `schema.sql` and then runs `ApplyIdempotentSchemaMigrationsAsync()`:
1. Inspects `pragma_table_info('Categories')`, `pragma_table_info('CategorizationRules')`, and `pragma_table_info('Transactions')`.
2. Adds `UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))` (backfilled from `CreatedAt` where available) to `Categories`, `CategorizationRules`, and `Transactions` if missing on an older database.
3. Adds `CategorizationSource` and `UploadBatchId` to `Transactions` if missing, backfilling `CategorizationSource` (`'Manual'`, `'Rule'`, or `'Uncategorized'`).
4. Ensures `StagingSessions` exists and prunes expired staging sessions older than 24 hours.

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
  - **Add Category (`POST /api/categories`):** Validates non-empty `Name` and `MonthlyBudget >= 0`. Sets `CreatedAt = datetime('now')`, `UpdatedAt = datetime('now')`, and triggers a `"category_create"` backup snapshot.
  - **Edit Category (`PUT /api/categories/{id}`):** Updates `Name`, `IsBigBoxMerchantCategory`, `MonthlyBudget`, `SortOrder`, `ColorHex`, and sets `UpdatedAt = datetime('now')`. Triggers a `"category_update"` backup snapshot.
  - **Delete Category (`DELETE /api/categories/{id}?replacementCategoryId={optionalId}`):**
    - If `replacementCategoryId` is provided: atomically reassigns all `Transactions` and `CategorizationRules` from `id` to `replacementCategoryId` (updating `UpdatedAt = datetime('now')` on the target category and affected rows), then deletes category `id`.
    - If `replacementCategoryId` is omitted: sets `CategoryId = NULL`, `CategorizationSource = 'Uncategorized'`, `IsCategoryManuallyLocked = 0`, and `UpdatedAt = datetime('now')` on affected `Transactions`, deletes rules tied to `id`, and deletes category `id`.

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

### 4.2 List Management & Multi-Mode Sorting Specification (New in v2.2)

To make navigating categories, visualizations, budgets, rules, and transactions fast on both mobile (iPhone) and desktop, the system provides standardized list-sorting controls across all views. User sort preferences are persisted in `state` (and `localStorage` so they survive page reloads).

#### A. Category Dropdown Menus & Pickers (`sortCategories` Helper)
Applies to all `<select>` category menus across the application:
- Unified Categorize Expense Modal (`app.js`)
- Cashflow Uncategorized Triage row/group dropdowns (`cashflowView.js`)
- CSV Upload Staging row category dropdowns (`uploadView.js`)
- Transaction Inspector filter bar, table row dropdowns, and Split Transaction modal (`inspectorView.js`)
- Category Delete Reassignment & Category Merge modals (`app.js`, `topologyView.js`)

**Supported Category Dropdown Sort Modes (`state.categoryDropdownSort`):**
| Sort Mode Key | Label | Ordering Logic |
| :--- | :--- | :--- |
| `alpha_asc` *(Default)* | **Alphabetical (A $\rightarrow$ Z)** | `Name.localeCompare(b.Name)` (case-insensitive) so users can jump to any category by name in dropdown menus. |
| `recent` | **Recent Changes** | `UpdatedAt DESC, Name ASC` — places categories that were most recently created, edited, or assigned to a transaction at the top of the dropdown. |
| `custom_order` | **Custom Sort Order** | `SortOrder ASC, Name ASC` — respects the manual numeric order configured in the Topology table. |
| `budget_desc` | **Highest Budget ($)** | `MonthlyBudget DESC, Name ASC` — places major household budget categories at the top. |

- **UI Control:** A compact **"Dropdown Order"** pill/selector is available in the Categorize Expense Modal (and in the Categories Topology header) allowing the user to switch between `A–Z`, `Recent Changes`, `Custom Order`, and `Highest Budget` at any time; changing it updates all category dropdowns app-wide.

#### B. Cashflow Sankey Visualization & Tabular Breakdown Sorting (`cashflowView.js` & `sankeyChart.js`)
On the **Cashflow & Sankey Visualization** page, a **`Sort Categories:`** selector in the page toolbar controls the vertical ordering of **both** the **Sankey Diagram Category Nodes (Column 2) & Subcategory Nodes (Column 3)** and the **Tabular Cashflow Breakdown rows**:

| Sort Mode Key | Label | Ordering Logic in Sankey & Table |
| :--- | :--- | :--- |
| `spend_desc` *(Default)* | **Highest Spend ($)** | `NetSpend DESC, CategoryName ASC` — largest spending flows appear at the top of the Sankey diagram and table. |
| `spend_asc` | **Lowest Spend ($)** | `NetSpend ASC, CategoryName ASC` — smallest positive spending flows appear first. |
| `alpha_asc` | **Alphabetical (A $\rightarrow$ Z)** | `CategoryName ASC` (with `Uncategorized` pinned at the bottom of the category list so named categories stay strictly A $\rightarrow$ Z). Subcategory nodes in Column 3 also sort alphabetically within each category. |
| `recent` | **Recent Changes** | Ordered by `LastActivityAt DESC` (the latest `UpdatedAt` across the category and its transactions in the window), surfacing categories that were recently recategorized or modified at the top of the visualization and table. |
| `custom_order` | **Custom Order** | Ordered by `Categories.SortOrder ASC, CategoryName ASC`. |

#### C. Uncategorized Expenses Triage List Sorting (`cashflowView.js`)
Inside the **Uncategorized Expenses Triage Panel**, alongside the **Group by Merchant** vs. **Chronological List** toggle, a **`Sort Triage:`** dropdown/pill control orders the uncategorized groups or rows:
1. **`amount_desc` (Highest Amount — Default in Merchant-Grouped mode):** Largest dollar impact first (`ABS(totalAmount) DESC`).
2. **`date_desc` (Newest Date / Recent Activity — Default in List mode):** Most recent transaction date or `UpdatedAt` first.
3. **`alpha_asc` (Merchant A $\rightarrow$ Z):** Alphabetical by `CleanMerchantName ASC`.
4. **`count_desc` (Most Frequent Merchant):** Highest transaction count per merchant group first (`transactions.length DESC`).

#### D. Monthly Budgets View Card Sorting (`budgetsView.js`)
In the **Monthly Budgets** header, a **`Sort Categories:`** selector orders the budget progress cards:
1. **`custom_order` (Custom Order — Default):** `SortOrder ASC, Name ASC`.
2. **`alpha_asc` (Alphabetical A $\rightarrow$ Z):** `Name ASC`.
3. **`recent` (Recent Changes):** Ordered by most recent category/transaction modification (`UpdatedAt DESC`).
4. **`pct_used_desc` (% Budget Used — High $\rightarrow$ Low):** Surfaces over-budget (`Red`) and warning (`Amber`) categories at the top.
5. **`spend_desc` (Highest Spent $):** `NetSpent DESC`.
6. **`remaining_asc` (Least Remaining $):** `Remaining ASC` (most over-budget or tightest remaining budget first).

Within each budget card's expandable transaction drill-down list, transactions can also be toggled between **Newest Date (`date_desc`)**, **Recent Changes (`recent`)**, **Alphabetical (`alpha_asc`)**, and **Highest Amount (`amount_desc`)**.

#### E. Categories Topology & Auto-Categorization Rules Sorting (`topologyView.js`)
1. **Spending Category Topology Table:**
   - Sortable via toolbar pills: **Custom Order (`SortOrder ASC`)**, **Alphabetical (`Name ASC`)**, **Recent Changes (`UpdatedAt DESC`)**, and **Highest Budget (`MonthlyBudget DESC`)**.
2. **Auto-Categorization Rules Table:**
   - Sortable via toolbar pills: **Rule Priority (`Priority ASC`)**, **Alphabetical Pattern (`MatchPattern ASC`)**, **Target Category (`CategoryName ASC`)**, and **Recent Changes (`UpdatedAt DESC`)**.

#### F. Transaction Inspector Sorting (`inspectorView.js` & `GET /api/transactions`)
The `Sort By` selector in the Transaction Inspector supports:
- **`date_desc`:** Newest Transaction Date (`t.TransactionDate DESC, t.TransactionId DESC`)
- **`date_asc`:** Oldest Transaction Date (`t.TransactionDate ASC, t.TransactionId ASC`)
- **`recent_changes`:** **Recent Changes / Recently Edited (`t.UpdatedAt DESC, t.TransactionId DESC`)** — immediately surfaces transactions that were recently categorized, edited, or split.
- **`merchant_asc`:** **Alphabetical Merchant (A $\rightarrow$ Z) (`t.CleanMerchantName ASC, t.TransactionDate DESC`)**
- **`merchant_desc`:** **Alphabetical Merchant (Z $\rightarrow$ A) (`t.CleanMerchantName DESC, t.TransactionDate DESC`)**
- **`amount_desc`:** Highest Amount (`ABS(t.Amount) DESC, t.TransactionDate DESC`)
- **`amount_asc`:** Lowest Amount (`ABS(t.Amount) ASC, t.TransactionDate DESC`)

---

### 4.3 CSV Ingestion, Bank Classification & Persistent Staging

#### A. Bank-Specific CSV Classification Rules (`CsvParserService`)
1. **Chase Credit Card (7-Column Headered CSV):**
   - **Credit Card Payoff (`'CreditCardPayoff'`):** `Type` column equals `"Payment"` OR `Description` contains `"PAYMENT THANK YOU"`, `"AUTOMATIC PAYMENT"`, or `"ONLINE PAYMENT"`. Stored with positive `Math.Abs(csvAmt)`.
   - **Expense (`'Expense'`):** `csvAmt < 0` (and not a payment). Stored with positive `Math.Abs(csvAmt)`.
   - **Refund (`'Refund'`):** `csvAmt > 0` (and not a payment). Stored with negative `-Math.Abs(csvAmt)`.

2. **Wells Fargo Checking (5-Column Headerless CSV):**
   - **Credit Card Payoff (`'CreditCardPayoff'`):** `Description` matches known credit card payment patterns (`"CHASE CREDIT CRD"`, `"CREDIT CARD"`, or `"EPAY"` + `"CHASE"`). Stored with positive `Math.Abs(csvAmt)`.
   - **Verified Payroll / Income (`'IncomeDeposit'`):** `csvAmt > 0` AND `Description` matches the configured `GooglePayrollMatchPattern` (or explicit `"DIRECT DEP"` / `"PAYROLL"` keywords). Stored with positive `Math.Abs(csvAmt)`.
   - **Other Positive Checking Credits (`'Refund'` + Manual Review Flag):** `csvAmt > 0` that do **not** match payroll or credit card payoff patterns. Defaulted to `'Refund'` (`-Math.Abs(csvAmt)`) with `RequiresManualReview = true` in the staging table so the Administrator explicitly confirms the classification before committing.
   - **Expense (`'Expense'`):** `csvAmt < 0` (and not a credit card payoff). Stored with positive `Math.Abs(csvAmt)`.

#### B. Deduplication & Immutable Provenance (`DedupHash`)
- **Base Fingerprint:** `ComputeBaseHash(accountId, isoDate, rawDesc, normalizedAmt)` computes the first 24 hex characters of `SHA256("{accountId}|{isoDate}|{rawDesc.Trim().ToUpperInvariant()}|{Math.Abs(normalizedAmt):F2}")`.
- **Immutability Across Edits:** Subsequent edits to `TransactionDate`, `Amount`, `CleanMerchantName`, `TransactionType`, `CategoryId`, `Subcategory`, or `Notes` via the Transaction Inspector update `UpdatedAt = datetime('now')` but do **not** overwrite `DedupHash`.
- **Tombstone Protection on Splits & Deletes:** Deleting a transaction (or a split child leg) with `addToTombstone = true` records both the row's `DedupHash` and its base hash (stripping any `_SPLIT_n` suffix) in `DeletedTransactionHashes`.

---

## 5. Updated API Contract Summary (v2.2)

| Method & Route | Persona | Description | Triggers DB Backup? |
| :--- | :--- | :--- | :--- |
| `GET /api/accounts` | Both | List active bank and credit card accounts. | No |
| `POST /api/accounts` | Admin | Create a new account (`AccountName`, `Institution`, `AccountType`). | **Yes** (`account_create`) |
| `GET /api/categories` | Both | List active categories (including `SortOrder`, `MonthlyBudget`, `CreatedAt`, `UpdatedAt`) with optional `?sortBy=alpha_asc\|recent\|custom_order\|budget_desc`. | No |
| `POST /api/categories` | Both | Create a new category (`Name`, `IsBigBoxMerchantCategory`, `MonthlyBudget`, `SortOrder`, `ColorHex`). | **Yes** (`category_create`) |
| `PUT /api/categories/{id}` | Both | Edit category properties and update `UpdatedAt`. | **Yes** (`category_update`) |
| `PUT /api/categories/{id}/budget` | Both | Update a category's `MonthlyBudget` target and `UpdatedAt`. | **Yes** (`budget_edit`) |
| `DELETE /api/categories/{id}` | Both | Delete category (optional `?replacementCategoryId=` to reassign transactions/rules; otherwise sets transactions to `Uncategorized`). | **Yes** (`category_delete`) |
| `POST /api/categories/{id}/merge-into/{targetId}` | Both | Merge source category into target category and update target `UpdatedAt`. | **Yes** (`category_merge`) |
| `GET /api/topology` | Both | Return all active categories and `CategorizationRules` (including `UpdatedAt` timestamps for sorting). | No |
| `POST /api/rules` | Both | Create a categorization rule (validating regex syntax if `IsRegex = true`). | **Yes** (`rule_create`) |
| `PUT /api/rules/{id}` | Both | Update an existing categorization rule and `UpdatedAt`. | **Yes** (`rule_update`) |
| `DELETE /api/rules/{id}` | Both | Delete a categorization rule. | **Yes** (`rule_delete`) |
| `POST /api/rules/back-propagate` | Both | Dry-run or commit retroactive rule evaluation across historical transactions. | **Yes** (when `dryRun = false`, `backprop`) |
| `POST /api/uploads/stage` | Admin | Parse Chase/Wells Fargo CSV files, classify rows, compute `DedupHash`, and persist staging session in SQLite. | No |
| `POST /api/uploads/commit` | Admin | Commit approved staged rows from SQLite `StagingSessions` into `Transactions` / `DeletedTransactionHashes`. | **Yes** (`csv_upload`) |
| `POST /api/transactions/{id}/categorize` | Both | Categorize or recategorize an expense (optionally creating a new category inline) with non-mutually exclusive `applyMovingForward` and `applyToPast` flags; updates `UpdatedAt` on affected transactions and target category. | **Yes** (`recategorize`) |
| `GET /api/transactions/similar-count` | Both | Returns count of past transactions matching `pattern` to preview in the categorization modal. | No |
| `GET /api/transactions` | Both / Admin | Query transactions with filters (`startDate`, `endDate`, `accountId`, `categoryId`, `uncategorizedOnly`, `transactionType`, `minAmount`, `maxAmount`, `search`) and `sortBy` (`date_desc`, `date_asc`, `recent_changes`, `merchant_asc`, `merchant_desc`, `amount_desc`, `amount_asc`). | No |
| `PUT /api/transactions/{id}` | Admin | Edit transaction fields (`Amount`, `TransactionDate`, `TransactionType`, `CleanMerchantName`, `CategoryId`, `Subcategory`, `Notes`) and update `UpdatedAt`. | **Yes** (`data_edit`) |
| `POST /api/transactions/{id}/split` | Admin | Split a transaction into multiple categorized child legs. | **Yes** (`data_edit`) |
| `DELETE /api/transactions/{id}` | Admin | Delete an income or expense transaction (with `?addToTombstone=true/false`). | **Yes** (`transaction_delete`) |
| `POST /api/transactions/bulk-delete` | Admin | Bulk-delete selected income/expense transactions in one atomic transaction. | **Yes** (`transaction_delete`) |
| `GET /api/cashflow` | Both | Returns cashflow summary, Sankey nodes/links, `uncategorizedTransactions`, and category `sortOrder` / `lastActivityAt` metadata so the Visualization page can re-sort Sankey nodes and table rows dynamically. | No |
| `GET /api/budgets/{yearMonth?}` | Both | Returns monthly budget summary, category progress cards (with `updatedAt` / `lastActivityAt`), and uncategorized transactions for the month. | No |
| `GET /api/settings` | Both | Returns income configuration and monthly overrides. | No |
| `PUT /api/settings` | Admin | Updates persistent monthly income, payroll match pattern, and income mode. | **Yes** (`settings_update`) |
| `PUT /api/settings/income-overrides` | Admin | Upserts a monthly income override for a specific `YearMonth`. | **Yes** (`income_override`) |
| `DELETE /api/settings/income-overrides/{yearMonth}` | Admin | Deletes a monthly income override. | **Yes** (`income_override_delete`) |
| `GET /api/backups` | Admin | Lists available SQLite backup snapshots and backup directory path. | No |
| `POST /api/backups/trigger` | Admin | Triggers an immediate manual SQLite backup snapshot. | **Yes** (`manual`) |
| `GET /api/backups/download/{fileName}` | Admin | Downloads a specific `.db` backup file. | No |

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

### 6.2 Visualization Page (`Cashflow & Sankey`) with Visualization & Triage Sorting
```
+-----------------------------------------------------------------------+
| Cashflow & Sankey Visualization                                       |
| Window: [ Current MTD ] [ Prior Month ] [ Last 3M ] [ Custom ]        |
| Sort Visualization & Table: [ Highest Spend ▼ ]  (A-Z | Recent | ...) |
+-----------------------------------------------------------------------+
|   [ S A N K E Y    D I A G R A M   (Nodes ordered by selected sort) ] |
+-----------------------------------------------------------------------+
| ⚠️ Uncategorized Expenses Triage (4 items • $277.30)                  |
| View: [ Group by Merchant (3) ] [ List (4) ]   Sort: [ Highest $ ▼ ]  |
|-----------------------------------------------------------------------|
| CITY WATER UTILITY (1 charge • Mar 11)     | $137.51 | [ Categorize ] |
| COSTCO GAS (2 charges • Mar 11 – Mar 18)   | $124.80 | [ Categorize ] |
| APPLE.COM/BILL (1 charge • Mar 15)         |  $14.99 | [ Categorize ] |
+-----------------------------------------------------------------------+
```
