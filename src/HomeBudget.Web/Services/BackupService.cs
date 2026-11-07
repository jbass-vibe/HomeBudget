using HomeBudget.Web.Data;

namespace HomeBudget.Web.Services;

public sealed class BackupService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILogger<BackupService> _logger;
    private readonly SemaphoreSlim _backupLock = new(1, 1);
    private CancellationTokenSource? _debounceCts;
    private const int MaxRetainedBackups = 30;

    public string BackupDirectoryPath { get; }

    public BackupService(SqliteConnectionFactory connectionFactory, IConfiguration config, ILogger<BackupService> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        BackupDirectoryPath = config["BackupDirectoryPath"] ?? @"C:\ProgramData\HomeBudget\Backups";
        Directory.CreateDirectory(BackupDirectoryPath);
    }

    public async Task<string> CreateSnapshotImmediateAsync(string reasonTag, CancellationToken ct = default)
    {
        _debounceCts?.Cancel();

        await _backupLock.WaitAsync(ct);
        try
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string fileName = $"budget_{reasonTag}_{timestamp}.db";
            string fullPath = Path.Combine(BackupDirectoryPath, fileName);

            await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{fullPath.Replace("'", "''")}';";
            await cmd.ExecuteNonQueryAsync(ct);

            _logger.LogInformation("Created SQLite snapshot: {FileName}", fileName);
            PruneOldBackups();
            return fileName;
        }
        finally
        {
            _backupLock.Release();
        }
    }

    public void ScheduleDebouncedRecategorizationSnapshot()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), token);
                await CreateSnapshotImmediateAsync("recat", token);
            }
            catch (OperationCanceledException)
            {
                // Superseded
            }
        }, token);
    }

    public IEnumerable<object> ListBackups()
    {
        return new DirectoryInfo(BackupDirectoryPath)
            .GetFiles("budget_*.db")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new
            {
                fileName = f.Name,
                createdAtUtc = f.CreationTimeUtc,
                sizeBytes = f.Length
            });
    }

    private void PruneOldBackups()
    {
        var oldFiles = new DirectoryInfo(BackupDirectoryPath)
            .GetFiles("budget_*.db")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Skip(MaxRetainedBackups)
            .ToList();

        foreach (var file in oldFiles)
        {
            try { file.Delete(); } catch { }
        }
    }
}
