using System.Text.Json;
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
            "SELECT * FROM CategorizationRules ORDER BY Priority ASC, LENGTH(MatchPattern) DESC, RuleId ASC;")).ToList();

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
                bool requiresManualReview = row.RequiresManualReview;

                if (row.TransactionType is TransactionTypes.Expense or TransactionTypes.Refund)
                {
                    foreach (var rule in rules)
                    {
                        bool isMatch = rule.IsRegex
                            ? SafeRegexIsMatch(row.RawDescription, rule.MatchPattern)
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

        // Persist staging session in SQLite so app pool recycles or restarts do not lose staged state
        await conn.ExecuteAsync(
            """
            DELETE FROM StagingSessions WHERE CreatedAt < datetime('now', '-1 day');
            INSERT INTO StagingSessions (StagingSessionId, AccountId, FileNamesJson, CandidatesJson, CreatedAt)
            VALUES (@StagingSessionId, @AccountId, @FileNamesJson, @CandidatesJson, datetime('now'));
            """,
            new
            {
                session.StagingSessionId,
                session.AccountId,
                FileNamesJson = JsonSerializer.Serialize(session.FileNames),
                CandidatesJson = JsonSerializer.Serialize(session.Candidates)
            });

        return session;
    }

    public async Task<object> CommitSessionAsync(CommitStagingRequest request, CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);

        var sessionRow = await conn.QuerySingleOrDefaultAsync<StagingSessionEntity>(
            "SELECT * FROM StagingSessions WHERE StagingSessionId = @Id;",
            new { Id = request.StagingSessionId })
            ?? throw new InvalidOperationException("Staging session not found or already committed.");

        var candidates = JsonSerializer.Deserialize<List<StagingCandidateDto>>(sessionRow.CandidatesJson) ?? [];
        var decisionMap = request.Decisions.ToDictionary(d => d.RowId, StringComparer.OrdinalIgnoreCase);
        string batchId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

        int importedCount = 0;
        int skippedCount = 0;
        int tombstonedCount = 0;

        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var candidate in candidates)
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
            string txType = TransactionTypes.Normalize(
                !string.IsNullOrWhiteSpace(decision.TransactionType)
                    ? decision.TransactionType
                    : candidate.TransactionType);

            decimal normalizedAmount = txType == TransactionTypes.Refund
                ? -Math.Abs(candidate.Amount)
                : Math.Abs(candidate.Amount);

            int? catId = txType is TransactionTypes.Expense or TransactionTypes.Refund ? decision.CategoryId : null;
            string? subcat = string.IsNullOrWhiteSpace(decision.Subcategory) ? null : decision.Subcategory.Trim();
            bool isManuallyLocked = catId.HasValue && catId != candidate.CategoryId;
            string catSource = !catId.HasValue
                ? "Uncategorized"
                : (isManuallyLocked ? "Manual" : "Rule");

            await conn.ExecuteAsync(
                """
                INSERT INTO Transactions (
                    AccountId, TransactionDate, PostDate, RawDescription, CleanMerchantName,
                    Amount, TransactionType, CategoryId, Subcategory, CategorizationSource,
                    IsCategoryManuallyLocked, ParentTransactionId, DedupHash, UploadBatchId,
                    CreatedAt, UpdatedAt
                ) VALUES (
                    @AccountId, @TransactionDate, @PostDate, @RawDescription, @CleanMerchantName,
                    @Amount, @TransactionType, @CategoryId, @Subcategory, @CategorizationSource,
                    @IsLocked, NULL, @DedupHash, @UploadBatchId,
                    datetime('now'), datetime('now')
                );
                """,
                new
                {
                    sessionRow.AccountId,
                    candidate.TransactionDate,
                    candidate.PostDate,
                    candidate.RawDescription,
                    CleanMerchantName = cleanName,
                    Amount = normalizedAmount,
                    TransactionType = txType,
                    CategoryId = catId,
                    Subcategory = subcat,
                    CategorizationSource = catSource,
                    IsLocked = isManuallyLocked ? 1 : 0,
                    candidate.DedupHash,
                    UploadBatchId = batchId
                },
                tx);

            if (isManuallyLocked && catId.HasValue)
            {
                await conn.ExecuteAsync(
                    "UPDATE Categories SET UpdatedAt = datetime('now') WHERE CategoryId = @CatId;",
                    new { CatId = catId.Value },
                    tx);
            }

            importedCount++;
        }

        await conn.ExecuteAsync(
            "DELETE FROM StagingSessions WHERE StagingSessionId = @Id;",
            new { Id = request.StagingSessionId },
            tx);

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

    private static bool SafeRegexIsMatch(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            return false;
        }
    }
}
