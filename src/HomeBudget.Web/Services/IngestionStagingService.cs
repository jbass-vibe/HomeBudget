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
    private readonly ConcurrentDictionary<string, StagingSessionDto> _sessions = new();

    public async Task<StagingSessionDto> StageFilesAsync(
        int accountId,
        IFormFileCollection files,
        CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);

        var account = await conn.QuerySingleOrDefaultAsync<Account>(
            "SELECT * FROM Accounts WHERE AccountId = @Id;", new { Id = accountId })
            ?? throw new InvalidOperationException($"Account {accountId} not found.");

        string googlePattern = await conn.ExecuteScalarAsync<string?>(
            "SELECT Value FROM Settings WHERE Key = 'GooglePayrollMatchPattern';") ?? "GOOGLE";

        var rules = (await conn.QueryAsync<CategorizationRule>(
            "SELECT * FROM CategorizationRules ORDER BY Priority ASC, RuleId ASC;")).ToList();

        var existingHashes = (await conn.QueryAsync<string>(
            "SELECT DedupHash FROM Transactions WHERE AccountId = @AccountId;",
            new { AccountId = accountId })).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deletedHashes = (await conn.QueryAsync<string>(
            "SELECT DedupHash FROM DeletedTransactionHashes;")).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var session = new StagingSessionDto
        {
            StagingSessionId = Guid.NewGuid().ToString("N"),
            AccountId = accountId,
            FileNames = files.Select(f => f.FileName).ToList()
        };

        var occurrenceCounter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            await using var stream = file.OpenReadStream();
            var parsedRows = csvParser.ParseCsvStream(stream, account, googlePattern);

            foreach (var row in parsedRows)
            {
                occurrenceCounter.TryGetValue(row.BaseDedupHash, out int countSoFar);
                countSoFar++;
                occurrenceCounter[row.BaseDedupHash] = countSoFar;

                string candidateHash = countSoFar == 1
                    ? row.BaseDedupHash
                    : $"{row.BaseDedupHash}_OCC_{countSoFar}";

                string dedupStatus;
                if (deletedHashes.Contains(candidateHash) || deletedHashes.Contains(row.BaseDedupHash))
                {
                    dedupStatus = "SkippedDeleted";
                }
                else if (existingHashes.Contains(candidateHash))
                {
                    dedupStatus = "SkippedDuplicate";
                }
                else if (countSoFar > 1)
                {
                    dedupStatus = "PotentialCollision";
                }
                else
                {
                    dedupStatus = "AutoApprove";
                }

                int? matchedCategoryId = null;
                string? matchedSubcat = null;
                bool requiresManualReview = false;

                if (row.TransactionType is "Expense" or "Refund")
                {
                    foreach (var rule in rules)
                    {
                        bool isMatch = rule.IsRegex
                            ? Regex.IsMatch(row.RawDescription, rule.MatchPattern, RegexOptions.IgnoreCase)
                            : row.RawDescription.Contains(rule.MatchPattern, StringComparison.OrdinalIgnoreCase)
                              || row.CleanMerchantName.Contains(rule.MatchPattern, StringComparison.OrdinalIgnoreCase);

                        if (isMatch)
                        {
                            matchedCategoryId = rule.CategoryId;
                            matchedSubcat = rule.DefaultSubcategory;
                            break;
                        }
                    }

                    if (!matchedCategoryId.HasValue)
                    {
                        requiresManualReview = true;
                    }
                }

                session.Candidates.Add(new StagingCandidateDto
                {
                    RowId = Guid.NewGuid().ToString("N")[..12],
                    TransactionDate = row.TransactionDate,
                    PostDate = row.PostDate,
                    RawDescription = row.RawDescription,
                    CleanMerchantName = row.CleanMerchantName,
                    Amount = row.Amount,
                    TransactionType = row.TransactionType,
                    CategoryId = matchedCategoryId,
                    Subcategory = matchedSubcat,
                    DedupHash = candidateHash,
                    DedupStatus = dedupStatus,
                    RequiresManualReview = requiresManualReview
                });
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

        var decisionMap = request.Decisions.ToDictionary(d => d.RowId, StringComparer.OrdinalIgnoreCase);
        string batchId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

        int importedCount = 0;
        int skippedCount = 0;
        int tombstonedCount = 0;

        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var candidate in session.Candidates)
        {
            if (!decisionMap.TryGetValue(candidate.RowId, out var decision))
            {
                skippedCount++;
                continue;
            }

            if (decision.Action.Equals("TombstoneDelete", StringComparison.OrdinalIgnoreCase))
            {
                await conn.ExecuteAsync(
                    """
                    INSERT OR IGNORE INTO DeletedTransactionHashes (DedupHash, RawDescription, TransactionDate, Amount)
                    VALUES (@DedupHash, @RawDescription, @TransactionDate, @Amount);
                    """,
                    candidate,
                    tx);
                tombstonedCount++;
                continue;
            }

            if (!decision.Action.Equals("Import", StringComparison.OrdinalIgnoreCase))
            {
                skippedCount++;
                continue;
            }

            string cleanName = !string.IsNullOrWhiteSpace(decision.CleanMerchantName)
                ? decision.CleanMerchantName.Trim()
                : candidate.CleanMerchantName;
            string txType = !string.IsNullOrWhiteSpace(decision.TransactionType)
                ? decision.TransactionType.Trim()
                : candidate.TransactionType;
            int? catId = txType is "Expense" or "Refund" ? decision.CategoryId : null;
            string? subcat = string.IsNullOrWhiteSpace(decision.Subcategory) ? null : decision.Subcategory.Trim();

            await conn.ExecuteAsync(
                """
                INSERT INTO Transactions (
                    AccountId, TransactionDate, PostDate, RawDescription, CleanMerchantName,
                    Amount, TransactionType, CategoryId, Subcategory, IsCategoryManuallyLocked,
                    ParentTransactionId, DedupHash, UploadBatchId
                ) VALUES (
                    @AccountId, @TransactionDate, @PostDate, @RawDescription, @CleanMerchantName,
                    @Amount, @TransactionType, @CategoryId, @Subcategory, @IsLocked,
                    NULL, @DedupHash, @UploadBatchId
                );
                """,
                new
                {
                    session.AccountId,
                    candidate.TransactionDate,
                    candidate.PostDate,
                    candidate.RawDescription,
                    CleanMerchantName = cleanName,
                    candidate.Amount,
                    TransactionType = txType,
                    CategoryId = catId,
                    Subcategory = subcat,
                    IsLocked = (catId != candidate.CategoryId) ? 1 : 0,
                    candidate.DedupHash,
                    UploadBatchId = batchId
                },
                tx);

            importedCount++;
        }

        await tx.CommitAsync(ct);

        string? backupFile = null;
        if (importedCount > 0 || tombstonedCount > 0)
        {
            backupFile = await backupService.CreateSnapshotImmediateAsync("csv_upload", ct);
        }

        return new
        {
            importedCount,
            skippedCount,
            tombstonedCount,
            backupFileCreated = backupFile
        };
    }
}
