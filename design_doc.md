# Local Home Budgeting & Cashflow System — Design Document (v2.0)

**Document Version:** 2.0  
**Target Platform:** Windows 11 IIS + ASP.NET Core (.NET 10 Minimal APIs) + SQLite (WAL Mode)  
**Network Scope:** Strictly LAN-Only (`LocalSubnet` via Windows Defender Firewall, Port 8080)  
**Primary Personas:**
- **User (Spouse / Mobile-First on iPhone):** Reviews monthly budgets, explores the Cashflow Sankey visualization, manages categories (add/edit/delete), and rapidly categorizes or recategorizes expenses directly from the Visualization and Budgets views with automated forward/backward rule propagation prompts.
- **Administrator (Desktop on Windows 11 PC):** Performs multi-file CSV statement ingestion (Chase & Wells Fargo), manages full category topology and rules, and uses the filtered Transaction Inspector to edit or delete income and expense entries (with tombstone hash protection).

---

## 1. Summary of v2.0 Requirement Updates

1. **Full Category Lifecycle & Dual-Persona Access (Add, Edit, Delete + Automatic DB Backup):**
   - Both the **User (iPhone/Mobile)** and **Administrator (Desktop)** can **add, edit, and delete** categories from any category management surface or inline during expense categorization.
   - Deleting a category supports either reassigning its existing transactions/rules to a replacement category or setting those transactions back to `Uncategorized` (`CategoryId = NULL`).
   - Every category addition, edit, deletion, or merge immediately triggers an atomic event-driven SQLite snapshot (`VACUUM INTO`) in `C:\ProgramData\HomeBudget\Backups`.

2. **Expense Recategorization with Non-Mutually Exclusive Forward & Historical Propagation Prompts:**
   - Whenever a user or administrator categorizes an uncategorized expense or recategorizes an existing expense, the UI presents an interactive confirmation prompt with two independent (non-mutually exclusive) checkboxes:
     - **Option A (Forward / Moving Forward):** *"Categorize similar expenses based on this change moving forward?"* — Creates or updates a persistent `CategorizationRule` (matched on normalized merchant name / pattern) so all future CSV uploads automatically assign this category.
     - **Option B (Historical / Past):** *"Recategorize similar expenses in the past?"* — Immediately runs a retroactive back-propagation query across existing transactions matching the merchant/pattern and updates their `CategoryId` (with optional override for previously locked rows).
   - Users can check **neither** (one-off change for this transaction only), **Option A only**, **Option B only**, or **both Option A and Option B** simultaneously.
   - Executing this change triggers an immediate SQLite database backup.

3. **Administrator Income & Expense Deletion with Multi-Criteria Filtering:**
   - The Administrator can **edit or delete both Income (`IncomeDeposit`) and Expense (`Expense` / `Refund`) transactions** from the database via the **Transaction Inspector**.
   - To make locating specific income or expense records effortless, the Transaction Inspector provides real-time, combinable filters:
     - **Date Range:** Start Date & End Date pickers (plus quick presets: Current MTD, Last Month, Last 3 Months, YTD, All Time).
     - **Transaction Type:** `All`, `Expense`, `IncomeDeposit` (Income), `Refund`, `Transfer`, `Payment`.
     - **Category:** Specific category dropdown, plus an **`Uncategorized Only`** quick-toggle pill.
     - **Account:** Filter by specific bank/credit card account (e.g., Wells Fargo Checking, Chase Sapphire, Chase Freedom).
     - **Amount Range:** Optional `Min Amount` / `Max Amount` filter to quickly isolate specific dollar amounts.
     - **Search Query:** Instant substring filter across `RawDescription`, `CleanMerchantName`, `Subcategory`, and `Notes`.
   - When deleting an income or expense record, the Administrator can choose whether to record its `DedupHash` in `DeletedTransactionHashes` (Tombstone table, enabled by default) so future overlapping CSV uploads do not resurrect the deleted entry, or delete without tombstoning so it can be re-imported if desired. Every deletion triggers a database backup.

4. **Visualization Page (`Uncategorized` Triage & Inline Categorization Workflow):**
   - On the **Cashflow & Sankey Visualization** page, if any expenses in the active time window are marked **`Uncategorized`**, the `Uncategorized` node in the Sankey diagram and a dedicated **"Uncategorized Expenses Triage"** drawer/card below the diagram are highlighted.
   - Clicking or tapping the **`Uncategorized`** Sankey node (or the triage banner) opens an interactive list of all uncategorized expenses for that period right on the Visualization page.
   - From this triage list, the user can:
     1. Select an existing category **or click `+ New Category`** inline to create a new category on the spot (which saves the category and triggers a DB backup).
     2. Immediately see the two non-mutually exclusive checkboxes right in the categorization prompt:
        - `[x] Apply to similar expenses moving forward (create/update rule)`
        - `[x] Apply to similar expenses in the past (recategorize historical matches)`
     3. Confirm with a single tap/click—instantly updating the transaction(s), applying the selected forward/past options, triggering a database backup, and live-refreshing the Sankey diagram so the `Uncategorized` bar shrinks or disappears in real time.

---

## 2. System Architecture & Runtime Stack

| Layer | Technology / Specification |
| :--- | :--- |
| **Host OS & Web Server** | Windows 11 Pro/Home with IIS (`AspNetCoreModuleV2`, In-Process Hosting on Port `8080`) |
| **Backend Framework** | ASP.NET Core Minimal APIs targeting **.NET 10.0 (`net10.0`)** |
| **Database Engine** | **SQLite 3.53+** (`Microsoft.Data.Sqlite` `10.0.0` + patched `SQLitePCLRaw.lib.e_sqlite3` `3.53.3`) |
| **Concurrency & Durability** | `PRAGMA journal_mode=WAL;`, `PRAGMA synchronous=NORMAL;`, `PRAGMA foreign_keys=ON;`, `PRAGMA busy_timeout=5000;` |
| **Data Access Layer** | **Dapper** (`2.1.35`) with custom `SqliteDecimalHandler` and `SqliteBoolHandler` for strict `.NET 10` type safety |
| **Frontend Architecture** | Zero-build Vanilla ES6 Modules (`app.js`, view modules, custom responsive SVG `sankeyChart.js`), Mobile-First CSS |
| **Network Isolation** | Bound to `*:8080` (blank hostname); Windows Firewall rule restricts inbound TCP `8080` to `LocalSubnet` on `Private` profile |

---

## 3. Database Schema (DDL v2.0)

Database file location: `C:\ProgramData\HomeBudget\budget.db`  
Automated Backup directory: `C:\ProgramData\HomeBudget\Backups\`

### 3.1 Core Tables (`schema.sql`)

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
    CategoryId      INTEGER PRIMARY KEY AUTOINCREMENT,
    Name            TEXT    NOT NULL UNIQUE,
    MonthlyBudget   NUMERIC NOT NULL DEFAULT 0.00,
    ColorHex        TEXT    NOT NULL DEFAULT '#3B82F6',
    IconName        TEXT    NOT NULL DEFAULT 'tag',
    SortOrder       INTEGER NOT NULL DEFAULT 100,
    IsSystem        INTEGER NOT NULL DEFAULT 0,
    IsActive        INTEGER NOT NULL DEFAULT 1,
    CreatedAt       TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS CategorizationRules (
    RuleId          INTEGER PRIMARY KEY AUTOINCREMENT,
    MatchType       TEXT    NOT NULL DEFAULT 'Contains', -- 'Exact', 'Contains', 'StartsWith', 'Regex'
    MatchPattern    TEXT    NOT NULL,
    CategoryId      INTEGER NOT NULL,
    DefaultSubcat   TEXT    NULL,
    Priority        INTEGER NOT NULL DEFAULT 100, -- Lower number = higher priority
    IsActive        INTEGER NOT NULL DEFAULT 1,
    CreatedAt       TEXT    NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS Transactions (
    TransactionId            INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountId                INTEGER NOT NULL,
    TransactionDate          TEXT    NOT NULL, -- ISO-8601 'YYYY-MM-DD'
    PostDate                 TEXT    NULL,
    RawDescription           TEXT    NOT NULL,
    CleanMerchantName        TEXT    NOT NULL,
    Amount                   NUMERIC NOT NULL, -- Positive = Expense/Outflow; Negative = Refund; Income stored positively with TransactionType='IncomeDeposit'
    TransactionType          TEXT    NOT NULL, -- 'Expense', 'Refund', 'IncomeDeposit', 'Transfer', 'Payment'
    CategoryId               INTEGER NULL,     -- NULL = Uncategorized (for Expense/Refund)
    Subcategory              TEXT    NULL,
    CategorizationSource     TEXT    NOT NULL DEFAULT 'Uncategorized', -- 'Manual', 'Rule', 'Heuristic', 'Uncategorized'
    IsCategoryManuallyLocked INTEGER NOT NULL DEFAULT 0,
    ParentTransactionId      INTEGER NULL,
    DedupHash                TEXT    NOT NULL,
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

> **Note on Foreign Key Cascade Behavior for Category Deletion:**
> - `CategorizationRules.CategoryId` uses `ON DELETE CASCADE` (or re-points to a replacement `CategoryId` if the user chooses to reassign on delete).
> - `Transactions.CategoryId` uses `ON DELETE SET NULL` so deleting a category without selecting a replacement cleanly marks its former expenses as `Uncategorized` (`CategoryId = NULL`, `CategorizationSource = 'Uncategorized'`, `IsCategoryManuallyLocked = 0`) rather than deleting transaction history.

---

## 4. Detailed Functional Specifications for New v2.0 Requirements

### 4.1 Requirement 1: Category Lifecycle & Unified Recategorization Propagation

#### A. Category CRUD (Both User & Administrator)
- **Where Accessible:**
  1. **Categories & Rules View** (accessible from both desktop sidebar and mobile bottom navigation bar).
  2. **Monthly Budgets View** (`+ Add Category` button at the top of the budget list, plus an `Edit / Delete` action icon on each category card/modal).
  3. **Inline Category Picker** inside the **Visualization Page Uncategorized Triage** and **Transaction Inspector** (`+ New Category...` option directly inside the category dropdown/modal).
- **Operations:**
  - **Add Category (`POST /api/categories`):** Creates a new category with `Name`, `MonthlyBudget`, `ColorHex`, and `SortOrder`. Immediately invokes `BackupService.CreateSnapshotImmediateAsync("category_create")`.
  - **Edit Category (`PUT /api/categories/{id}`):** Updates `Name`, `MonthlyBudget`, `ColorHex`, or `SortOrder`. Immediately invokes `BackupService.CreateSnapshotImmediateAsync("category_update")`.
  - **Delete Category (`DELETE /api/categories/{id}?replacementCategoryId={optionalId}`):**
    - If `replacementCategoryId` is provided: atomically reassigns all `Transactions` and `CategorizationRules` from `id` to `replacementCategoryId`, then deletes category `id`.
    - If `replacementCategoryId` is omitted: sets `CategoryId = NULL`, `CategorizationSource = 'Uncategorized'`, and `IsCategoryManuallyLocked = 0` on affected `Transactions`, deletes rules tied solely to `id`, and deletes category `id`.
    - Immediately invokes `BackupService.CreateSnapshotImmediateAsync("category_delete")`.

#### B. Expense Categorization / Recategorization with Forward & Historical Options
Whenever a user or administrator assigns or changes the category of an expense (from the **Visualization Page**, **Monthly Budgets Drill-Down**, or **Transaction Inspector**):

1. **Similarity Matching Pattern Resolution:**
   - The system pre-fills the similarity match pattern using the transaction's `CleanMerchantName` (e.g., `"COSTCO WHSE"`, `"TARGET"`, `"PG&E"`, or a user-editable merchant substring).
   - The modal/popover displays a live preview count: *"Found X past transactions matching `[Pattern]`"*.

2. **Non-Mutually Exclusive Propagation Options:**
   The UI presents two independent checkboxes (both checked by default when categorizing an uncategorized merchant, and both independently toggleable):
   - **[Checkbox A] Apply to similar expenses moving forward (`applyMovingForward: bool`)**
     - When `true`: Upserts an active rule in `CategorizationRules` (`MatchType = 'Contains'`, `MatchPattern = normalizedMerchantPattern`, `CategoryId = newCategoryId`). Any future CSV statement uploads containing this merchant will automatically be assigned to `newCategoryId`.
   - **[Checkbox B] Recategorize similar expenses in the past (`applyToPast: bool`)**
     - When `true`: Executes an immediate historical update across all existing rows in `Transactions` where `TransactionDate <= current` (or all existing rows in DB) matching `CleanMerchantName LIKE %pattern% OR RawDescription LIKE %pattern%` and `TransactionId != currentTransactionId`, setting `CategoryId = newCategoryId` and `CategorizationSource = 'Rule'`.
     - Includes an optional sub-toggle *"Also update past expenses that were manually locked"* (defaults to `true` when explicitly requested by the user during recategorization so all past instances of that merchant align).

3. **Unified Backend Endpoint (`POST /api/transactions/{id}/categorize`):**
   - **Request Payload (`CategorizeTransactionRequest`):**
     ```json
     {
       "categoryId": 4,
       "newCategoryName": null,
       "newCategoryBudget": null,
       "newCategoryColorHex": null,
       "subcategory": "Household",
       "matchPattern": "COSTCO",
       "applyMovingForward": true,
       "applyToPast": true,
       "includeManuallyLockedInPast": true
     }
     ```
   - **Execution Flow (Single Atomic SQLite Transaction + Backup):**
     1. If `categoryId` is `null` and `newCategoryName` is provided, creates the new category first and obtains its `CategoryId`.
     2. Updates the target transaction (`TransactionId = id`) with `CategoryId`, `Subcategory`, `CategorizationSource = 'Manual'`, `IsCategoryManuallyLocked = 1`.
     3. If `applyMovingForward == true`, creates or updates the matching `CategorizationRule` for `matchPattern` $ightarrow$ `CategoryId`.
     4. If `applyToPast == true`, updates all existing transactions matching `matchPattern` to `CategoryId` and returns `pastTransactionsUpdatedCount`.
     5. Commits the SQLite transaction and invokes `BackupService.CreateSnapshotImmediateAsync("recategorize")` so the database is backed up immediately.

---

### 4.2 Requirement 2: Administrator Income & Expense Editing and Deletion with Rich Filters

#### A. Comprehensive Filtering in Transaction Inspector (`GET /api/transactions`)
The **Transaction Inspector** view provides a sticky filter bar so the Administrator can isolate any income or expense record in seconds:
- **Search Box (`search`):** Matches partial text in `RawDescription`, `CleanMerchantName`, `Subcategory`, or `Notes`.
- **Transaction Type Filter (`transactionType`):**
  - `All Types`
  - `Expense` (Standard purchases)
  - `IncomeDeposit` (Paychecks, direct deposits, other income)
  - `Refund` (Credits/returns)
  - `Transfer` / `Payment` (Credit card payments & inter-account transfers)
- **Category Filter (`categoryId` / `uncategorizedOnly`):**
  - `All Categories`
  - `⚠️ Uncategorized Only`
  - Individual Category list (`Housing & Utilities`, `Groceries`, `Costco`, etc.)
- **Account Filter (`accountId`):** Filter by specific checking or credit card account.
- **Date Range (`startDate`, `endDate`) & Quick Date Pills:** `This Month`, `Last Month`, `Last 3M`, `YTD`, `All`.
- **Amount Range (`minAmount`, `maxAmount`):** Filter by exact or bounded dollar amount.

#### B. Editing & Deleting Income and Expenses
- **Editing Income or Expense Rows:**
  - The Administrator can edit the **Category** (triggering the Forward/Past propagation prompt), **Transaction Type** (e.g., reclassifying a deposit to/from `IncomeDeposit`, `Expense`, `Refund`, or `Transfer`), **Clean Merchant Name**, **Subcategory**, **Amount**, **Transaction Date**, or **Notes**.
  - Every edit triggers `BackupService.CreateSnapshotImmediateAsync("data_edit")`.
- **Deleting Income or Expense Rows (`DELETE /api/transactions/{id}?addToTombstone=true`):**
  - Available for **all** `TransactionType` values, including **`IncomeDeposit`** and **`Expense`**.
  - Clicking **Delete** opens a confirmation modal displaying the date, merchant/description, type (`Income` or `Expense`), and amount, with a checkbox:
    - `[x] Prevent this transaction from being re-imported on future CSV uploads (Tombstone hash)`
  - Confirming deletion removes the row from `Transactions`, optionally inserts its `DedupHash` into `DeletedTransactionHashes`, and immediately invokes `BackupService.CreateSnapshotImmediateAsync("transaction_delete")`.
  - **Bulk Delete Support (`POST /api/transactions/bulk-delete`):** Allows the Administrator to select multiple filtered rows via checkboxes in the Transaction Inspector and delete them in a single atomic operation with a single post-operation backup.

---

### 4.3 Requirement 3: Visualization Page (`Cashflow & Sankey`) Uncategorized Expense Triage

On the **Cashflow & Sankey Visualization** page (`cashflowView.js`):

1. **Interactive `Uncategorized` Node in Sankey Diagram:**
   - When `Uncategorized` expenses exist in the selected time window (`Current MTD`, `Rolling N Months`, or `Custom Range`), the Sankey diagram renders an **`Uncategorized`** node highlighted in amber (`#F59E0B`) with a pulsing action badge showing the count and dollar total.
   - Tapping or clicking the **`Uncategorized`** node in the SVG diagram smoothly scrolls to (or opens) the **Uncategorized Expenses Triage Panel** directly below the chart.

2. **Dedicated "Uncategorized Expenses Triage" Panel on the Visualization Page:**
   - Automatically appears on the Visualization page whenever there are 1 or more uncategorized expenses in the selected date window (and displays a clean `"All expenses in this period are categorized! 🎉"` badge when zero remain).
   - Groups uncategorized expenses by normalized merchant (`CleanMerchantName`) or lists them chronologically with a merchant badge so the user can see at a glance if 5 uncategorized charges are all from the same merchant.
   - Each row/group features a **Quick Categorize** button (or inline selector) that opens the **Unified Categorization Modal**:
     - **Select Existing Category** (tappable color-coded pills on mobile or dropdown) **OR** tap **`+ Create New Category`** inline (enter Name, optional Monthly Budget, Color).
     - **Propagation Options (Non-Mutually Exclusive Checkboxes):**
       - `[x] Categorize similar expenses moving forward (Merchant matches: "...")`
       - `[x] Recategorize similar expenses in the past (N matching historical expenses found)`
     - **Save & Apply Button:**
       - Saves the new category (if created), categorizes the expense, applies the forward rule and/or historical recategorization, triggers the SQLite database backup, and **immediately re-renders the Cashflow Sankey diagram** so the user sees the expense flow move from `Uncategorized` into its proper category node in real time.

---

## 5. Updated API Contract Summary (v2.0)

| Method & Route | Persona | Description | Triggers DB Backup? |
| :--- | :--- | :--- | :--- |
| `GET /api/categories` | Both | List all active categories ordered by `SortOrder`. | No |
| `POST /api/categories` | Both | Create a new category (`Name`, `MonthlyBudget`, `ColorHex`). | **Yes** (`category_create`) |
| `PUT /api/categories/{id}` | Both | Edit category name, budget, color, or sort order. | **Yes** (`category_update`) |
| `DELETE /api/categories/{id}` | Both | Delete category (optional `?replacementCategoryId=` to reassign transactions/rules; otherwise sets transactions to `Uncategorized`). | **Yes** (`category_delete`) |
| `POST /api/categories/{id}/merge-into/{targetId}` | Both | Merge source category into target category. | **Yes** (`category_merge`) |
| `POST /api/transactions/{id}/categorize` | Both | Categorize or recategorize an expense (optionally creating a new category inline) with non-mutually exclusive `applyMovingForward` and `applyToPast` flags. | **Yes** (`recategorize`) |
| `GET /api/transactions/similar-count` | Both | Returns count of past transactions matching `pattern` to preview in the categorization modal before applying. | No |
| `GET /api/transactions` | Both / Admin | Query transactions with filters: `startDate`, `endDate`, `accountId`, `categoryId`, `uncategorizedOnly`, `transactionType`, `minAmount`, `maxAmount`, `search`, `page`, `pageSize`. | No |
| `PUT /api/transactions/{id}` | Admin | Edit transaction fields (`Amount`, `TransactionDate`, `TransactionType`, `CleanMerchantName`, `CategoryId`, `Subcategory`, `Notes`). | **Yes** (`data_edit`) |
| `DELETE /api/transactions/{id}` | Admin | Delete an income or expense transaction (with `?addToTombstone=true/false`). | **Yes** (`transaction_delete`) |
| `POST /api/transactions/bulk-delete` | Admin | Bulk-delete selected income/expense transactions in one atomic transaction. | **Yes** (`transaction_delete`) |
| `GET /api/cashflow` | Both | Returns cashflow summary, Sankey nodes/links, and the list of `uncategorizedTransactions` for the active window so the Visualization page can triage them immediately. | No |

---

## 6. UI/UX Wireframe Flows for New Requirements

### 6.1 Unified Categorization / Recategorization Modal (Used in Visualization Page, Budgets, and Inspector)
```
+-----------------------------------------------------------------------+
| Categorize Expense                                                [X] |
+-----------------------------------------------------------------------+
| Transaction:  2026-03-14 • TRADER JOE'S #124                $84.52    |
|                                                                       |
| 1. Choose Category:                                                   |
|    [ Groceries       ▼ ]   or   [ + Create New Category ]             |
|                                                                       |
|    (If "+ Create New Category" clicked, expands inline:)              |
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
| [ Current MTD ] [ Last 3 Months ] [ Custom Range ]                    |
+-----------------------------------------------------------------------+
|   [ S A N K E Y    D I A G R A M   (Tap "Uncategorized" node) ]       |
+-----------------------------------------------------------------------+
| ⚠️ Uncategorized Expenses in This Period (3 items • $214.90)          |
| Categorize these items below to update your Cashflow Sankey diagram:  |
|-----------------------------------------------------------------------|
| 2026-03-18 | COSTCO GAS #0422        | $62.40  | [ Categorize... ]    |
| 2026-03-15 | APPLE.COM/BILL          | $14.99  | [ Categorize... ]    |
| 2026-03-11 | CITY WATER UTILITY      | $137.51 | [ Categorize... ]    |
+-----------------------------------------------------------------------+
```
