# HomeBudget — Local Windows 11 IIS Cashflow & Budgeting System

A strictly local-network (LAN-only) household budgeting and cashflow visualization web application hosted on Windows 11 IIS + ASP.NET Core (.NET 8) + SQLite (WAL mode).

## Key Features
- **LAN-Only Privacy:** Scoped at the Windows Defender Firewall level strictly to `LocalSubnet` over Private Wi-Fi/LAN.
- **Dual-Persona Responsive UI:**
  - **Wife's iPhone View (Mobile-First):** Portrait monthly budget progress cards with tap-to-edit fixed monthly targets, transaction drill-downs, and Landscape full-screen interactive Cashflow Sankey diagram.
  - **Windows 11 PC View (Desktop Admin):** Multi-file Chase & Wells Fargo CSV drag-and-drop staging wizard, same-day duplicate collision reviewer, inline transaction inspector, transaction splitter, category topology merge, and retroactive rule back-propagation engine.
- **Tiered Monthly Income Resolution:** Automatically prioritizes bi-weekly **Google Direct Deposits** from Wells Fargo Checking CSVs (`IncomeDeposit`), falling back to persistent monthly income when direct deposit data is absent for a given period.
- **Event-Driven Local SQLite Backups:** Automatically executes `VACUUM INTO` snapshots in `C:\ProgramData\HomeBudget\Backups` whenever input CSV data is committed or transactions/rules are recategorized.

## Quick Start (Local Development)
```powershell
dotnet run --project src/HomeBudget.Web/HomeBudget.Web.csproj --urls "http://localhost:5000"
```
Then open `http://localhost:5000` and test uploading the sample files in `sample_data/WellsFargo_Sample.csv` and `sample_data/Chase_Sample.csv`.

## Production Deployment to Windows 11 IIS
1. Install the official Microsoft **ASP.NET Core 8.0 Windows Hosting Bundle**.
2. Publish the app:
   ```powershell
   dotnet publish src/HomeBudget.Web/HomeBudget.Web.csproj -c Release -o C:\inetpub\HomeBudget
   ```
3. Run the provisioning script as Administrator:
   ```powershell
   .\deploy\Deploy-HomeBudget.ps1
   ```
