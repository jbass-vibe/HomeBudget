# HomeBudget v2.0 — Local Windows 11 IIS Cashflow & Budgeting System (.NET 10)

A strictly local-network (LAN-only) household budgeting and cashflow visualization web application hosted on Windows 11 IIS + ASP.NET Core (`.NET 10`) + SQLite (WAL mode).

## What's New in v2.0
1. **Full Category Lifecycle (Add, Edit, Delete + Automatic DB Backup):**
   - Both User (iPhone/Mobile) and Administrator (Desktop) can add, edit, and delete categories from Monthly Budgets, the Cashflow Visualization page, or the Categories & Rules page.
   - Deleting a category lets you either reassign its existing transactions/rules to another category or mark them as `Uncategorized`.
   - Every category change immediately triggers an atomic SQLite snapshot in `C:\ProgramData\HomeBudget\Backups`.
2. **Non-Mutually Exclusive Forward & Historical Categorization Prompts:**
   - Whenever an expense is categorized or recategorized, the user is prompted with two independent checkboxes:
     - **a) Moving Forward:** Automatically categorize similar expenses in future CSV uploads (creates/updates rule).
     - **b) In the Past:** Retroactively recategorize similar historical expenses already in the database.
3. **Filtered Income & Expense Deletion in Transaction Inspector:**
   - Rich combinable filters (Type pills for `IncomeDeposit`, `Expense`, `Uncategorized`, `Refund`, Date presets, Account, Category, Min/Max Amount, Sort order, and instant text search).
   - Single and bulk deletion of both Income (`IncomeDeposit`) and Expense (`Expense`) records with optional Tombstone hash protection and automatic database backup.
4. **Visualization Page (`Cashflow & Sankey`) Uncategorized Triage:**
   - Highlights `Uncategorized` expenses directly on the Visualization page so users can categorize expenses (or create new categories inline) and immediately watch the Sankey diagram update in real time.

## Deployment to Windows 11 IIS
```powershell
iisreset /stop
cd C:\dev\HomeBudget
dotnet publish src/HomeBudget.Web/HomeBudget.Web.csproj -c Release -o C:\inetpub\HomeBudget
iisreset /start
```
