using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dapper;
using HomeBudget.Web.Data;
using HomeBudget.Web.Models;

namespace HomeBudget.Web.Services;

public sealed class IngestionStagingService(
    SqliteConnectionFactory connectionFactory,
    CsvParserService csvParser,
    BackupService backupService)
{
    private readonly ConcurrentDictionary<string, StagedUploadSession> _sessions = new();

    private static readonly Dictionary<string, string> ChaseNativeCategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Groceries"] = "Groceries",
        ["Food & Drink"] = "Dining & Bars",
        ["Gas"] = "Auto & Gas",
        ["Automotive"] = "Auto & Gas",
        ["Bills & Utilities"] = "Housing & Utilities",
        ["Home"] = "Home & Garden",
        ["Entertainment"] = "Entertainment",
        ["Health & Wellness"] = "Health & Medical",
        ["Travel"] = "Travel"
    };

    public async Task<StagedUploadSession> StageFilesAsync(
        int accountId,
        IFormFileCollection files,
        CancellationToken ct = default)
    {
        PruneExpiredSessions();

        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);

        var account = await conn.QuerySingleOrDefaultAsync<Account>(
            "SELECT * FROM Accounts WHERE AccountId = @AccountId;",
            new { AccountId = accountId })
            ?? throw new InvalidOperationException($"Account {accountId} not found.");

        string googlePattern = await conn.ExecuteScalarAsync<string?>(
            "SELECT Value FROM Settings WHERE Key = 'GooglePayrollMatchPattern';") ?? "GOOGLE";

        var categories = (await conn.QueryAsync<Category>("SELECT * FROM Categories WHERE IsActive = 1;")).ToList();
        var categoryByName = categories.ToDictionary(c => c.Name, c => c.CategoryId, StringComparer.OrdinalIgnoreCase);

        var rules = (await conn.QueryAsync<CategorizationRule>(
            "SELECT * FROM CategorizationRules ORDER BY Priority ASC, RuleId ASC;")).ToList();

        var existingHashes = (await conn.QueryAsync<string>("SELECT DedupHash FROM Transactions;"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tombstoneHashes = (await conn.QueryAsync<string>("SELECT DedupHash FROM DeletedTransactionHashes;"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var session = new StagedUploadSession { AccountId = accountId };
        var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            session.FileNames.Add(file.FileName);
            await using var stream = file.OpenReadStream();
            var parsedRows = csvParser.ParseCsv(stream, file.FileName, account, googlePattern);

            foreach (var row in parsedRows)
            {
                if (row.TransactionType is "Expense" or "Refund")
                {
                    ApplyAutoCategorization(row, rules, categoryByName);
                }

                if (tombstoneHashes.Contains(row.DedupHash))
                {
                    row.DedupStatus = "SkippedDeleted";
                }
                else if (existingHashes.Contains(row.DedupHash))
                {
                    row.DedupStatus = "SkippedDuplicate";
                }
                else if (!seenInBatch.Add(row.DedupHash))
                {
                    row.DedupStatus = "PotentialCollision";
                }
                else
                {
                    row.DedupStatus = "AutoApprove";
                }

                session.Candidates.Add(row);
            }
        }

        _sessions[session.StagingSessionId] = session;
        return session;
    }

    public async Task<object> CommitSessionAsync(CommitStagingRequest request, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(request.StagingSessionId, out var session))
        {
            throw new InvalidOperationException("Staging session not found or already committed.");
        }

        var decisionsById = request.Decisions.ToDictionary(d => d.RowId, StringComparer.OrdinalIgnoreCase);

        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int importedCount = 0;
        int skippedCount = 0;
        int tombstonedCount = 0;

        int batchId = await conn.ExecuteScalarAsync<int>(
            "INSERT INTO UploadBatches (AccountId, FileName, ImportedRowCount, SkippedDuplicateCount) VALUES (@AccountId, @FileName, 0, 0); SELECT last_insert_rowid();",
            new
            {
                session.AccountId,
                FileName = string.Join(", ", session.FileNames)
            },
            tx);

        foreach (var row in session.Candidates)
        {
            string action = row.DedupStatus == "AutoApprove" ? "Import" : "Skip";
            if (decisionsById.TryGetValue(row.RowId, out var decision))
            {
                action = decision.Action;
                if (decision.CategoryId.HasValue) row.CategoryId = decision.CategoryId;
                if (decision.Subcategory is not null) row.Subcategory = decision.Subcategory;
                if (!string.IsNullOrWhiteSpace(decision.CleanMerchantName)) row.CleanMerchantName = decision.CleanMerchantName;
                if (!string.IsNullOrWhiteSpace(decision.TransactionType)) row.TransactionType = decision.TransactionType;
            }

            if (action.Equals("TombstoneDelete", StringComparison.OrdinalIgnoreCase))
            {
                await conn.ExecuteAsync(
                    "INSERT OR IGNORE INTO DeletedTransactionHashes (DedupHash, RawDescription, TransactionDate, Amount) VALUES (@DedupHash, @RawDescription, @TransactionDate, @Amount);",
                    row,
                    tx);
                tombstonedCount++;
            }
            else if (action.Equals("Import", StringComparison.OrdinalIgnoreCase))
            {
                await conn.ExecuteAsync(
                    "INSERT INTO Transactions (AccountId, TransactionDate, PostDate, RawDescription, CleanMerchantName, Amount, TransactionType, CategoryId, Subcategory, IsCategoryManuallyLocked, DedupHash, UploadBatchId) VALUES (@AccountId, @TransactionDate, @PostDate, @RawDescription, @CleanMerchantName, @Amount, @TransactionType, @CategoryId, @Subcategory, 0, @DedupHash, @BatchId);",
                    new
                    {
                        row.AccountId,
                        row.TransactionDate,
                        row.PostDate,
                        row.RawDescription,
                        row.CleanMerchantName,
                        row.Amount,
                        row.TransactionType,
                        row.CategoryId,
                        row.Subcategory,
                        row.DedupHash,
                        BatchId = batchId
                    },
                    tx);
                importedCount++;
            }
            else
            {
                skippedCount++;
            }
        }

        await conn.ExecuteAsync(
            "UPDATE UploadBatches SET ImportedRowCount = @ImportedCount, SkippedDuplicateCount = @SkippedCount WHERE UploadBatchId = @BatchId;",
            new { ImportedCount = importedCount, SkippedCount = skippedCount, BatchId = batchId },
            tx);

        await tx.CommitAsync(ct);

        string? backupFile = null;
        if (importedCount > 0 || tombstonedCount > 0)
        {
            backupFile = await backupService.CreateSnapshotImmediateAsync("upload", ct);
        }

        return new
        {
            uploadBatchId = batchId,
            importedCount,
            skippedCount,
            tombstonedCount,
            backupFileCreated = backupFile
        };
    }

    private static void ApplyAutoCategorization(
        StagedCandidateRow row,
        List<CategorizationRule> rules,
        Dictionary<string, int> categoryByName)
    {
        foreach (var rule in rules)
        {
            bool matched = rule.IsRegex
                ? Regex.IsMatch(row.RawDescription, rule.MatchPattern, RegexOptions.IgnoreCase)
                : row.RawDescription.Contains(rule.MatchPattern, StringComparison.OrdinalIgnoreCase);

            if (matched)
            {
                row.CategoryId = rule.CategoryId;
                row.Subcategory = rule.DefaultSubcategory;
                row.RequiresManualReview = false;
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(row.NativeCsvCategory)
            && ChaseNativeCategoryMap.TryGetValue(row.NativeCsvCategory, out string? mappedCategoryName)
            && categoryByName.TryGetValue(mappedCategoryName, out int mappedCatId))
        {
            row.CategoryId = mappedCatId;
            return;
        }

        if (!row.CategoryId.HasValue)
        {
            row.RequiresManualReview = true;
        }
    }

    private void PruneExpiredSessions()
    {
        DateTime cutoff = DateTime.UtcNow.AddMinutes(-30);
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.CreatedAtUtc < cutoff)
            {
                _sessions.TryRemove(kvp.Key, out _);
            }
        }
    }
}
