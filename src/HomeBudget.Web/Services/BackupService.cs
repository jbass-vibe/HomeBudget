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
        BackupDirectoryPath = configuration["HomeBudget:BackupDirectoryPath"]
            ?? @"C:\ProgramData\HomeBudget\Backups";
        _maxRetainedBackups = int.TryParse(configuration["HomeBudget:MaxRetainedBackups"], out int max) ? max : 30;

        Directory.CreateDirectory(BackupDirectoryPath);
    }

    public async Task<string> CreateSnapshotImmediateAsync(string reason, CancellationToken ct = default)
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

            PruneOldBackups();
            _logger.LogInformation("Created SQLite backup snapshot {FileName}", fileName);
            return fileName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create SQLite backup snapshot for reason {Reason}", reason);
            return "";
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
