# HomeBudget Deployment & Rollback Runbook (v2.9 Authentication)

## 1. NTFS Permissions (Run Once Before Starting Service)
Protect the Data Protection key ring, SQLite database, and bootstrap/recovery token directory so only `SYSTEM`, `Administrators`, and the HomeBudget service account can read/write `C:\ProgramData\HomeBudget\`:

```powershell
$dataDir = "C:\ProgramData\HomeBudget"
$keysDir = "C:\ProgramData\HomeBudget\keys"
New-Item -ItemType Directory -Path $dataDir, $keysDir -Force | Out-Null

# Replace <ServiceAccount> with your IIS AppPool identity or Windows Service user
# e.g., "IIS APPPOOL\HomeBudget" or "NT AUTHORITY\LOCAL SERVICE"
$svcAccount = "IIS APPPOOL\HomeBudget"

icacls $dataDir /inheritance:r
icacls $dataDir /grant "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "${svcAccount}:(OI)(CI)M"

icacls $keysDir /inheritance:r
icacls $keysDir /grant "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "${svcAccount}:(OI)(CI)M"
```

## 2. 12-Step Rollout & Smoke Verification
1. **Create a pre-upgrade SQLite backup** (copy `C:\ProgramData\HomeBudget\budget.db` to `Backups\pre_v2_9_manual.db`).
2. **Verify HTTPS is active** per [https_setup_runbook.md](./https_setup_runbook.md).
3. **Stop the HomeBudget service / IIS AppPool**.
4. **Deploy the v2.9 application binaries** (`dotnet publish -c Release`).
5. **Apply NTFS ACLs** on `C:\ProgramData\HomeBudget` and `C:\ProgramData\HomeBudget\keys`.
6. **Start the HomeBudget service / IIS AppPool**.
7. **Inspect logs / disk** to confirm `C:\ProgramData\HomeBudget\setup-token.txt` was generated.
8. **Smoke Check 1 (Unauthenticated Gate):** Visit `https://<host>:8443` — confirm the Initial Setup screen appears and `GET /api/budgets` returns `401`.
9. **Smoke Check 2 (Bootstrap Setup):** Paste the token from `setup-token.txt`, set strong passphrases (12–128 chars) for `admin` and `user`, and submit. Confirm `setup-token.txt` is automatically deleted.
10. **Smoke Check 3 (Admin & User Roles):**
    - Sign in as `admin`: verify `Upload CSVs`, `Categories, Rules & Backups`, `User Management`, and `Security Audit Log` are visible.
    - Sign in as `user` (e.g., on iPhone Safari): verify `Monthly Budgets`, `Cashflow & Sankey`, `Transaction Inspector` (read-only + Recategorize), and `Categories & Rules` work, while `Upload CSVs` and Admin panels are hidden.
11. **Smoke Check 4 (Step-Up & Logout):** Verify `Sign Out` and `Sign Out All` invalidate sessions cleanly.
12. **Run full test suite** on the Windows host before/after deploy:
    ```powershell
    dotnet test
    node tests/frontend_verification.mjs
    ```

## 3. Break-Glass Admin Recovery Procedure
If the `admin` passphrase is forgotten or `admin` is locked out:
1. RDP or log in locally to the Windows 11 host as an Administrator.
2. Create `C:\ProgramData\HomeBudget\admin-recovery.token` containing a new strong passphrase (12–128 characters) on the first line.
3. Restart the HomeBudget service / IIS AppPool.
4. On startup, `AuthService.ProcessBreakGlassRecoveryAsync` will:
   - Validate the passphrase against `PasswordPolicyValidator`.
   - Update `admin`'s `PasswordHash`, rotate `SecurityStamp` (revoking all old `admin` cookies), clear `FailedLoginAttempts` / `LockoutEndUtc`, and ensure `IsActive = 1`.
   - Log an `EMERGENCY_RECOVERY` event to `SecurityAuditLogs`.
   - **Immediately delete** `admin-recovery.token`.
5. Sign in as `admin` using the new passphrase.

## 4. Decoupled Rollback Procedures
- **Database Rollback (Keep v2.9 Code):**
  - Use **Restore Database** in the UI (requires Admin passphrase step-up) or copy a backup `.db` over `budget.db` while the service is stopped.
  - Note: Restoring an older snapshot via the UI automatically preserves live `Users`, `SecurityAuditLogs`, and `AuthInitialized` so you are never locked out.
- **Code Rollback (Revert to Pre-v2.9 Binaries):**
  1. Stop the HomeBudget service / IIS AppPool.
  2. Restore the previous application binaries.
  3. Restore the `pre_v2_9_manual.db` snapshot (or leave `budget.db` — pre-v2.9 code ignores `Users` and `SecurityAuditLogs` tables).
  4. Start the service / AppPool.
