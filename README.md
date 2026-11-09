# HomeBudget v2.1 — Local Windows 11 IIS Cashflow & Budgeting System (.NET 10)

A strictly local-network (LAN-only) household budgeting and cashflow visualization web application hosted on Windows 11 IIS + ASP.NET Core (`.NET 10`) + SQLite (WAL mode).

## Key Capabilities (v2.1)
1. **Full Category Lifecycle (Add, Edit, Delete + Automatic Verified DB Backup):**
   - Both User (iPhone/Mobile) and Administrator (Desktop) can add, edit, and delete categories from Monthly Budgets, the Cashflow Visualization page, or the Categories & Rules page.
   - Deleting a category lets you either reassign its existing transactions/rules to another category or mark them as `Uncategorized`.
   - Every category change triggers a verified atomic SQLite snapshot (`VACUUM INTO`) in `C:\ProgramData\HomeBudget\Backups`.
2. **Non-Mutually Exclusive Forward & Historical Categorization Prompts:**
   - Whenever an expense is categorized or recategorized, the user is prompted with two independent checkboxes:
     - **a) Moving Forward:** Automatically categorize similar expenses in future CSV uploads (creates/updates rule).
     - **b) In the Past:** Retroactively recategorize similar historical expenses already in the database.
   - Tracks `CategorizationSource` (`Manual`, `Rule`, `Uncategorized`) and `IsCategoryManuallyLocked` on each transaction.
3. **Transaction Inspector (Filtering, Notes, Splitting & Tombstone Deletion):**
   - Rich combinable filters (canonical `TransactionType` pills for `Expense`, `IncomeDeposit`, `Refund`, `CreditCardPayoff`, `Uncategorized`, Date presets, Account, Category, Min/Max Amount, Sort order, and instant text search across merchant, description, subcategory, and notes).
   - Full inline editing of `TransactionDate`, `CleanMerchantName`, `TransactionType`, `Amount`, `CategoryId`, `Subcategory`, and `Notes` while preserving immutable `DedupHash` provenance.
   - Multi-leg transaction splitting and single/bulk deletion with Tombstone hash protection (including base-hash protection for split legs).
4. **Visualization Page (`Cashflow & Sankey`) Uncategorized Triage (Merchant-Grouped & Chronological):**
   - Highlights `Uncategorized` expenses directly on the Visualization page with a toggle between **Group by Merchant** and **Chronological List** views.
5. **Hardened CSV Classification & SQLite-Persisted Upload Staging:**
   - Distinguishes verified payroll (`IncomeDeposit`) from non-payroll positive checking credits (`Refund` + manual review flag) and credit card payments (`CreditCardPayoff`).
   - Persists CSV staging sessions in SQLite (`StagingSessions`) so IIS recycles or restarts do not lose in-progress imports.

## Running Automated Tests
```powershell
# Run .NET 10 xUnit backend & SQLite integration tests
dotnet test tests/HomeBudget.Tests/HomeBudget.Tests.csproj

# Run frontend ES6 module syntax & security verification
node tests/frontend_verification.mjs
```

## Deployment to Windows 11 IIS
```powershell
iisreset /stop
cd C:\dev\HomeBudget
dotnet publish src/HomeBudget.Web/HomeBudget.Web.csproj -c Release -o C:\inetpub\HomeBudget
iisreset /start
```
