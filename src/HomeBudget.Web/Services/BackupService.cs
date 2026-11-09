using Dapper;
using HomeBudget.Web.Data;
using HomeBudget.Web.Models;

namespace HomeBudget.Web.Services;

public sealed class BackupService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILogger<BackupService> _logger;
    private readonly int _maxRetainedBackups;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private CancellationTokenSource? _debounceCts;

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

        _maxRetainedBackups = int.TryParse(configuration["HomeBudget:MaxRetainedBackups"], out int max) && max > 0 ? max : 30;

        Directory.CreateDirectory(BackupDirectoryPath);
    }

    public async Task<string?> CreateSnapshotImmediateAsync(string reason, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(BackupDirectoryPath);
            string safeReason = string.IsNullOrWhiteSpace(reason) ? "snapshot" : reason.Trim().ToLowerInvariant();
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

            PruneOldBackups();
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
        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                await CreateSnapshotImmediateAsync("recat_debounced", cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public List<BackupFileInfoDto> ListBackups()
    {
        if (!Directory.Exists(BackupDirectoryPath)) return [];

        return new DirectoryInfo(BackupDirectoryPath)
            .GetFiles("*.db")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new BackupFileInfoDto
            {
                FileName = f.Name,
                CreatedAtUtc = f.CreationTimeUtc,
                SizeBytes = f.Length
            })
            .ToList();
    }

    private void PruneOldBackups()
    {
        try
        {
            var files = new DirectoryInfo(BackupDirectoryPath)
                .GetFiles("*.db")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(_maxRetainedBackups)
                .ToList();

            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error pruning old SQLite backups");
        }
    }
}
