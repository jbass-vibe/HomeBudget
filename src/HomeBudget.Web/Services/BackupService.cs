using Dapper;
using HomeBudget.Web.Data;
using HomeBudget.Web.Models;
using Microsoft.Data.Sqlite;

namespace HomeBudget.Web.Services;

public sealed class BackupService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILogger<BackupService> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _scheduleLock = new();

    private readonly string _defaultAutoMode;
    private readonly int _defaultDebounceSeconds;
    private readonly int _defaultMinIntervalMinutes;
    private readonly int _defaultMaxRetainedBackups;

    private BackupPolicyConfigDto? _cachedPolicy;
    private CancellationTokenSource? _debounceCts;
    private int _pendingChangesCount;
    private readonly HashSet<string> _pendingReasons = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _nextScheduledBackupUtc;
    private DateTime? _lastAutoBackupUtc;

    public string BackupDirectoryPath { get; }

    public BackupService(
        SqliteConnectionFactory connectionFactory,
        IConfiguration configuration,
        ILogger<BackupService> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;

        string? configuredBackupDir = configuration["HomeBudget:BackupDirectoryPath"];
        if (string.IsNullOrWhiteSpace(configuredBackupDir)
            || (!OperatingSystem.IsWindows() && configuredBackupDir.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase)))
        {
            string dbDir = Path.GetDirectoryName(connectionFactory.DatabasePath) ?? AppContext.BaseDirectory;
            BackupDirectoryPath = OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(configuredBackupDir)
                ? @"C:\ProgramData\HomeBudget\Backups"
                : Path.Combine(dbDir, "Backups");
        }
        else
        {
            BackupDirectoryPath = configuredBackupDir;
        }

        _defaultAutoMode = NormalizeAutoMode(configuration["HomeBudget:BackupAutoMode"]);
        _defaultDebounceSeconds = int.TryParse(configuration["HomeBudget:BackupDebounceSeconds"], out int deb) && deb >= 5 ? Math.Clamp(deb, 5, 600) : 30;
        _defaultMinIntervalMinutes = int.TryParse(configuration["HomeBudget:BackupMinIntervalMinutes"], out int minInt) && minInt >= 0 ? Math.Clamp(minInt, 0, 1440) : 10;
        _defaultMaxRetainedBackups = int.TryParse(configuration["HomeBudget:MaxRetainedBackups"], out int max) && max > 0 ? Math.Clamp(max, 1, 200) : 15;

        Directory.CreateDirectory(BackupDirectoryPath);
    }

    public static string NormalizeAutoMode(string? mode)
    {
        return (mode ?? "Coalesced").Trim().ToLowerInvariant() switch
        {
            "majoronly" or "major_only" => "MajorOnly",
            "manualonly" or "manual_only" => "ManualOnly",
            "everychange" or "every_change" => "EveryChange",
            _ => "Coalesced"
        };
    }

    public async Task<BackupPolicyConfigDto> GetPolicyConfigAsync(CancellationToken ct = default)
    {
        if (_cachedPolicy is not null)
        {
            return _cachedPolicy;
        }

        try
        {
            await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
            var rows = await conn.QueryAsync<(string Key, string Value)>(
                "SELECT Key, Value FROM Settings WHERE Key IN ('BackupAutoMode', 'BackupDebounceSeconds', 'BackupMinIntervalMinutes', 'MaxRetainedBackups');");
            var dict = rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);

            var config = new BackupPolicyConfigDto
            {
                BackupAutoMode = dict.TryGetValue("BackupAutoMode", out var m) ? NormalizeAutoMode(m) : _defaultAutoMode,
                BackupDebounceSeconds = dict.TryGetValue("BackupDebounceSeconds", out var d) && int.TryParse(d, out int dv) ? Math.Clamp(dv, 5, 600) : _defaultDebounceSeconds,
                BackupMinIntervalMinutes = dict.TryGetValue("BackupMinIntervalMinutes", out var mi) && int.TryParse(mi, out int miv) ? Math.Clamp(miv, 0, 1440) : _defaultMinIntervalMinutes,
                MaxRetainedBackups = dict.TryGetValue("MaxRetainedBackups", out var mr) && int.TryParse(mr, out int mrv) ? Math.Clamp(mrv, 1, 200) : _defaultMaxRetainedBackups
            };

            _cachedPolicy = config;
            return config;
        }
        catch
        {
            return new BackupPolicyConfigDto
            {
                BackupAutoMode = _defaultAutoMode,
                BackupDebounceSeconds = _defaultDebounceSeconds,
                BackupMinIntervalMinutes = _defaultMinIntervalMinutes,
                MaxRetainedBackups = _defaultMaxRetainedBackups
            };
        }
    }

    public async Task<BackupPolicyConfigDto> UpdatePolicyConfigAsync(BackupPolicyConfigDto req, CancellationToken ct = default)
    {
        var normalized = new BackupPolicyConfigDto
        {
            BackupAutoMode = NormalizeAutoMode(req.BackupAutoMode),
            BackupDebounceSeconds = Math.Clamp(req.BackupDebounceSeconds, 5, 600),
            BackupMinIntervalMinutes = Math.Clamp(req.BackupMinIntervalMinutes, 0, 1440),
            MaxRetainedBackups = Math.Clamp(req.MaxRetainedBackups, 1, 200)
        };

        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        async Task UpsertSetting(string key, string val)
        {
            await conn.ExecuteAsync(
                "INSERT INTO Settings (Key, Value, UpdatedAt) VALUES (@Key, @Value, datetime('now')) ON CONFLICT(Key) DO UPDATE SET Value = @Value, UpdatedAt = datetime('now');",
                new { Key = key, Value = val },
                tx);
        }

        await UpsertSetting("BackupAutoMode", normalized.BackupAutoMode);
        await UpsertSetting("BackupDebounceSeconds", normalized.BackupDebounceSeconds.ToString());
        await UpsertSetting("BackupMinIntervalMinutes", normalized.BackupMinIntervalMinutes.ToString());
        await UpsertSetting("MaxRetainedBackups", normalized.MaxRetainedBackups.ToString());

        await tx.CommitAsync(ct);

        _cachedPolicy = normalized;
        if (normalized.BackupAutoMode == "ManualOnly")
        {
            CancelPendingSchedule();
        }

        PruneOldBackups(normalized.MaxRetainedBackups);
        return normalized;
    }

    /// <summary>
    /// Policy-aware backup entrypoint used by application services.
    /// Prevents backup proliferation by coalescing rapid edits and respecting cooldowns.
    /// </summary>
    public async Task<string?> RequestBackupAsync(string reason, bool isMajorOperation = false, CancellationToken ct = default)
    {
        var policy = await GetPolicyConfigAsync(ct);

        if (policy.BackupAutoMode == "ManualOnly")
        {
            return "skipped_policy";
        }

        if (policy.BackupAutoMode == "EveryChange")
        {
            return await CreateSnapshotImmediateAsync(reason, ct);
        }

        if (policy.BackupAutoMode == "MajorOnly" && !isMajorOperation)
        {
            return "skipped_policy";
        }

        // Mode is either "Coalesced" or ("MajorOnly" with isMajorOperation == true)
        TimeSpan remainingCooldown = GetRemainingCooldown(policy.BackupMinIntervalMinutes);

        if (isMajorOperation && remainingCooldown <= TimeSpan.Zero)
        {
            CancelPendingSchedule();
            return await CreateSnapshotImmediateAsync(reason, ct);
        }

        ScheduleCoalescedSnapshot(reason, TimeSpan.FromSeconds(policy.BackupDebounceSeconds), remainingCooldown);
        return "scheduled";
    }

    public async Task<string?> FlushPendingSnapshotAsync(string fallbackReason = "manual", CancellationToken ct = default)
    {
        int pendingCount;
        lock (_scheduleLock)
        {
            pendingCount = _pendingChangesCount;
            _debounceCts?.Cancel();
            _debounceCts = null;
            _pendingChangesCount = 0;
            _pendingReasons.Clear();
            _nextScheduledBackupUtc = null;
        }

        string effectiveReason = pendingCount > 0 && fallbackReason == "flush"
            ? $"coalesced_{pendingCount}changes"
            : fallbackReason;

        return await CreateSnapshotImmediateAsync(effectiveReason, ct);
    }

    public async Task<string?> CreateSnapshotImmediateAsync(string reason, CancellationToken ct = default)
    {
        var policy = await GetPolicyConfigAsync(ct);

        await _mutex.WaitAsync(ct);
        try
        {
            lock (_scheduleLock)
            {
                _debounceCts?.Cancel();
                _debounceCts = null;
                _pendingChangesCount = 0;
                _pendingReasons.Clear();
                _nextScheduledBackupUtc = null;
            }

            Directory.CreateDirectory(BackupDirectoryPath);
            string safeReason = SanitizeReasonToken(reason);
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            string fileName = $"budget_{timestamp}_{safeReason}.db";
            string fullPath = Path.Combine(BackupDirectoryPath, fileName);

            await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
            string escapedPath = fullPath.Replace("'", "''");
            await conn.ExecuteAsync($"VACUUM INTO '{escapedPath}';");

            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                _logger.LogError("SQLite backup snapshot {FileName} was not created or is empty at {FullPath}", fileName, fullPath);
                return null;
            }

            _lastAutoBackupUtc = DateTime.UtcNow;
            PruneOldBackups(policy.MaxRetainedBackups);
            _logger.LogInformation("Created verified SQLite backup snapshot {FileName} ({SizeBytes} bytes)", fileName, fileInfo.Length);
            return fileName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create SQLite backup snapshot for reason {Reason}", reason);
            return null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public void ScheduleDebouncedRecategorizationSnapshot()
    {
        ScheduleCoalescedSnapshot("recat_debounced", TimeSpan.FromSeconds(_defaultDebounceSeconds), TimeSpan.Zero);
    }

    private void ScheduleCoalescedSnapshot(string reason, TimeSpan debounceWindow, TimeSpan remainingCooldown)
    {
        TimeSpan delay = debounceWindow > remainingCooldown ? debounceWindow : remainingCooldown;
        if (delay < TimeSpan.FromSeconds(2))
        {
            delay = TimeSpan.FromSeconds(2);
        }

        CancellationToken token;
        lock (_scheduleLock)
        {
            _pendingChangesCount++;
            if (!string.IsNullOrWhiteSpace(reason))
            {
                _pendingReasons.Add(reason.Trim());
            }

            _debounceCts?.Cancel();
            var cts = new CancellationTokenSource();
            _debounceCts = cts;
            token = cts.Token;
            _nextScheduledBackupUtc = DateTime.UtcNow.Add(delay);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token);
                int countSnapshot;
                lock (_scheduleLock)
                {
                    countSnapshot = Math.Max(1, _pendingChangesCount);
                }
                await CreateSnapshotImmediateAsync($"coalesced_{countSnapshot}changes", token);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CancelPendingSchedule()
    {
        lock (_scheduleLock)
        {
            _debounceCts?.Cancel();
            _debounceCts = null;
            _pendingChangesCount = 0;
            _pendingReasons.Clear();
            _nextScheduledBackupUtc = null;
        }
    }

    private TimeSpan GetRemainingCooldown(int minIntervalMinutes)
    {
        if (minIntervalMinutes <= 0 || !_lastAutoBackupUtc.HasValue)
        {
            return TimeSpan.Zero;
        }

        DateTime nextAllowed = _lastAutoBackupUtc.Value.AddMinutes(minIntervalMinutes);
        TimeSpan remaining = nextAllowed - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public List<BackupFileInfoDto> ListBackups()
    {
        if (!Directory.Exists(BackupDirectoryPath)) return [];

        return new DirectoryInfo(BackupDirectoryPath)
            .GetFiles("*.db")
            .OrderByDescending(f => f.CreationTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new BackupFileInfoDto
            {
                FileName = f.Name,
                CreatedAtUtc = f.CreationTimeUtc,
                SizeBytes = f.Length,
                ReasonLabel = ParseReasonLabel(f.Name)
            })
            .ToList();
    }

    public BackupStatusDto GetStatus(List<BackupFileInfoDto>? backups = null)
    {
        var list = backups ?? ListBackups();
        lock (_scheduleLock)
        {
            return new BackupStatusDto
            {
                TotalCount = list.Count,
                TotalSizeBytes = list.Sum(b => b.SizeBytes),
                LatestBackupUtc = list.FirstOrDefault()?.CreatedAtUtc,
                PendingChangesCount = _pendingChangesCount,
                PendingReasons = _pendingReasons.ToList(),
                NextScheduledBackupUtc = _nextScheduledBackupUtc
            };
        }
    }

    public bool DeleteBackup(string fileName)
    {
        string? fullPath = ResolveSafeBackupFilePath(fileName);
        if (fullPath is null || !File.Exists(fullPath))
        {
            return false;
        }

        File.Delete(fullPath);
        _logger.LogInformation("Deleted SQLite backup snapshot {FileName}", Path.GetFileName(fullPath));
        return true;
    }

    public int BulkDeleteBackups(IEnumerable<string> fileNames)
    {
        int deleted = 0;
        foreach (var name in fileNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (DeleteBackup(name))
            {
                deleted++;
            }
        }
        return deleted;
    }

    public int PruneBackupsToCount(int keepCount)
    {
        int safeKeep = Math.Max(0, keepCount);
        if (!Directory.Exists(BackupDirectoryPath)) return 0;

        var filesToDelete = new DirectoryInfo(BackupDirectoryPath)
            .GetFiles("*.db")
            .OrderByDescending(f => f.CreationTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(safeKeep)
            .ToList();

        int deleted = 0;
        foreach (var file in filesToDelete)
        {
            try
            {
                file.Delete();
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete backup snapshot {FileName} during prune", file.Name);
            }
        }
        return deleted;
    }

    public async Task<RestoreBackupResultDto> RestoreSnapshotAsync(string fileName, bool createPreRestoreBackup = true, CancellationToken ct = default)
    {
        string? fullPath = ResolveSafeBackupFilePath(fileName);
        if (fullPath is null || !File.Exists(fullPath))
        {
            throw new InvalidOperationException($"Backup file '{fileName}' was not found.");
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException($"Backup file '{fileInfo.Name}' is empty and cannot be restored.");
        }

        // 1. Verify SQLite integrity of the backup snapshot before touching the live DB
        var checkConnStr = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        try
        {
            await using var checkConn = new SqliteConnection(checkConnStr);
            await checkConn.OpenAsync(ct);
            string? integrity = await checkConn.ExecuteScalarAsync<string>("PRAGMA integrity_check;");
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Backup file '{fileInfo.Name}' failed SQLite integrity check ({integrity}).");
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException($"File '{fileInfo.Name}' is not a valid SQLite database ({ex.Message}).");
        }

        // 2. Optionally create a safety backup of the current live database first
        string? preRestoreFile = null;
        if (createPreRestoreBackup && File.Exists(_connectionFactory.DatabasePath))
        {
            preRestoreFile = await CreateSnapshotImmediateAsync("pre_restore", ct);
            if (string.IsNullOrWhiteSpace(preRestoreFile))
            {
                throw new InvalidOperationException("Pre-restore safety backup creation failed. Aborting database restore to protect your live data.");
            }
        }

        // 3. Perform online SQLite page backup from snapshot into the live database
        await _mutex.WaitAsync(ct);
        try
        {
            CancelPendingSchedule();

            await using (var sourceConn = new SqliteConnection(checkConnStr))
            await using (var destConn = await _connectionFactory.CreateOpenConnectionAsync(ct))
            {
                await sourceConn.OpenAsync(ct);
                sourceConn.BackupDatabase(destConn);
            }

            // 4. Re-apply idempotent schema migrations so even older snapshots upgrade cleanly
            await using (var migratedConn = await _connectionFactory.CreateOpenConnectionAsync(ct))
            {
                await DatabaseInitializer.ApplyIdempotentSchemaMigrationsAsync(migratedConn);
            }

            _cachedPolicy = null;
            _logger.LogInformation("Restored live SQLite database from snapshot {FileName}", fileInfo.Name);

            return new RestoreBackupResultDto
            {
                RestoredFrom = fileInfo.Name,
                PreRestoreBackupFile = preRestoreFile,
                IntegrityVerified = true
            };
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<T> ExecuteExclusiveLifecycleActionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            CancelPendingSchedule();
            _cachedPolicy = null;
            return await action(ct);
        }
        finally
        {
            _cachedPolicy = null;
            _mutex.Release();
        }
    }

    public async Task<RestoreBackupResultDto> RestoreUploadedSnapshotAsync(
        Stream uploadedStream,
        string? originalFileName,
        bool createPreRestoreBackup = true,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(BackupDirectoryPath);
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        string savedFileName = $"budget_{timestamp}_uploaded_restore.db";
        string savedFullPath = Path.Combine(BackupDirectoryPath, savedFileName);

        await using (var fs = new FileStream(savedFullPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await uploadedStream.CopyToAsync(fs, ct);
        }

        try
        {
            return await RestoreSnapshotAsync(savedFileName, createPreRestoreBackup, ct);
        }
        catch
        {
            try
            {
                if (File.Exists(savedFullPath))
                {
                    File.Delete(savedFullPath);
                }
            }
            catch
            {
            }
            throw;
        }
    }

    public static string ParseReasonLabel(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "Snapshot";
        string stem = Path.GetFileNameWithoutExtension(fileName);
        // Expected pattern: budget_yyyyMMdd_HHmmss_fff_<reason>
        var parts = stem.Split('_', 5, StringSplitOptions.RemoveEmptyEntries);
        string rawReason = parts.Length >= 5 ? parts[4] : (parts.Length >= 4 ? parts[^1] : "snapshot");

        if (rawReason.StartsWith("coalesced", StringComparison.OrdinalIgnoreCase))
        {
            return "Coalesced Edits";
        }

        return rawReason.ToLowerInvariant() switch
        {
            "manual" => "Manual Snapshot",
            "csv_upload" => "CSV Import",
            "pre_restore" => "Pre-Restore Safety",
            "pre_clear_history" => "Pre-Clear History Safety",
            "pre_reset_all" => "Pre-Reset Safety",
            "uploaded_restore" => "Uploaded Snapshot",
            "category_delete" => "Category Delete",
            "category_merge" => "Category Merge",
            "category_create" => "Category Create",
            "category_update" => "Category Update",
            "bulk_delete" => "Bulk Delete",
            "transaction_delete" => "Transaction Delete",
            "transaction_split" => "Transaction Split",
            "recategorize" or "recat_debounced" => "Expense Categorization",
            "backprop" => "Rule Back-Propagation",
            "rule_create" or "rule_update" or "rule_delete" => "Rule Change",
            "budget_edit" => "Budget Target Edit",
            "data_edit" => "Transaction Edit / Split",
            "settings_update" or "income_override" or "income_override_delete" => "Income / Settings",
            "account_create" => "Account Created",
            _ => rawReason.Replace('_', ' ')
        };
    }

    private string? ResolveSafeBackupFilePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        string safeName = Path.GetFileName(fileName.Trim());
        if (!safeName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(BackupDirectoryPath, safeName);
    }

    private static string SanitizeReasonToken(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "snapshot";
        var chars = reason.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        string cleaned = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(cleaned) ? "snapshot" : cleaned;
    }

    private void PruneOldBackups(int maxRetained)
    {
        try
        {
            PruneBackupsToCount(maxRetained);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error pruning old SQLite backups");
        }
    }

    public void Dispose()
    {
        CancelPendingSchedule();
        _mutex.Dispose();
    }
}

