using System.Text.RegularExpressions;
using Dapper;
using HomeBudget.Web.Data;
using HomeBudget.Web.Models;

namespace HomeBudget.Web.Services;

public sealed class CategorizationService(
    SqliteConnectionFactory connectionFactory,
    BackupService backupService)
{
    public async Task<object> GetTopologyAsync(CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        var categories = await conn.QueryAsync<Category>(
            "SELECT * FROM Categories WHERE IsActive = 1 ORDER BY SortOrder ASC, Name ASC;");
        var rules = await conn.QueryAsync<CategorizationRule>(
            "SELECT * FROM CategorizationRules ORDER BY Priority ASC, RuleId ASC;");

        return new { categories, rules };
    }

    public async Task<Category> CreateCategoryAsync(UpsertCategoryRequest req, CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        int id = await conn.ExecuteScalarAsync<int>(
            """
            INSERT INTO Categories (Name, IsBigBoxMerchantCategory, MonthlyBudget, SortOrder, ColorHex, IsActive)
            VALUES (@Name, @IsBigBoxMerchantCategory, @MonthlyBudget, @SortOrder, @ColorHex, 1);
            SELECT last_insert_rowid();
            """,
            new
            {
                Name = req.Name.Trim(),
                IsBigBoxMerchantCategory = req.IsBigBoxMerchantCategory ? 1 : 0,
                req.MonthlyBudget,
                req.SortOrder,
                req.ColorHex
            });

        return await conn.QuerySingleAsync<Category>("SELECT * FROM Categories WHERE CategoryId = @Id;", new { Id = id });
    }

    public async Task<Category?> UpdateCategoryAsync(int categoryId, UpsertCategoryRequest req, CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        int rows = await conn.ExecuteAsync(
            """
            UPDATE Categories
            SET Name = @Name,
                IsBigBoxMerchantCategory = @IsBigBoxMerchantCategory,
                MonthlyBudget = @MonthlyBudget,
                SortOrder = @SortOrder,
                ColorHex = @ColorHex
            WHERE CategoryId = @CategoryId;
            """,
            new
            {
                CategoryId = categoryId,
                Name = req.Name.Trim(),
                IsBigBoxMerchantCategory = req.IsBigBoxMerchantCategory ? 1 : 0,
                req.MonthlyBudget,
                req.SortOrder,
                req.ColorHex
            });

        if (rows == 0) return null;
        return await conn.QuerySingleAsync<Category>("SELECT * FROM Categories WHERE CategoryId = @Id;", new { Id = categoryId });
    }

    public async Task<object> MergeCategoriesAsync(int sourceCategoryId, int targetCategoryId, CancellationToken ct = default)
    {
        if (sourceCategoryId == targetCategoryId)
        {
            throw new InvalidOperationException("Source and target categories must be different.");
        }

        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int reassignedTransactions = await conn.ExecuteAsync(
            "UPDATE Transactions SET CategoryId = @TargetId WHERE CategoryId = @SourceId;",
            new { SourceId = sourceCategoryId, TargetId = targetCategoryId },
            tx);

        int reassignedRules = await conn.ExecuteAsync(
            "UPDATE CategorizationRules SET CategoryId = @TargetId WHERE CategoryId = @SourceId;",
            new { SourceId = sourceCategoryId, TargetId = targetCategoryId },
            tx);

        await conn.ExecuteAsync(
            "DELETE FROM Categories WHERE CategoryId = @SourceId;",
            new { SourceId = sourceCategoryId },
            tx);

        await tx.CommitAsync(ct);

        string backupFile = await backupService.CreateSnapshotImmediateAsync("topology", ct);

        return new
        {
            sourceCategoryId,
            targetCategoryId,
            reassignedTransactionsCount = reassignedTransactions,
            reassignedRulesCount = reassignedRules,
            backupFileCreated = backupFile
        };
    }

    public async Task<CategorizationRule> CreateRuleAsync(UpsertRuleRequest req, CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        int id = await conn.ExecuteScalarAsync<int>(
            """
            INSERT INTO CategorizationRules (MatchPattern, IsRegex, CategoryId, DefaultSubcategory, Priority)
            VALUES (@MatchPattern, @IsRegex, @CategoryId, @DefaultSubcategory, @Priority);
            SELECT last_insert_rowid();
            """,
            new
            {
                MatchPattern = req.MatchPattern.Trim(),
                IsRegex = req.IsRegex ? 1 : 0,
                req.CategoryId,
                DefaultSubcategory = string.IsNullOrWhiteSpace(req.DefaultSubcategory) ? null : req.DefaultSubcategory.Trim(),
                req.Priority
            });

        return await conn.QuerySingleAsync<CategorizationRule>("SELECT * FROM CategorizationRules WHERE RuleId = @Id;", new { Id = id });
    }

    public async Task<CategorizationRule?> UpdateRuleAsync(int ruleId, UpsertRuleRequest req, CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        int rows = await conn.ExecuteAsync(
            """
            UPDATE CategorizationRules
            SET MatchPattern = @MatchPattern,
                IsRegex = @IsRegex,
                CategoryId = @CategoryId,
                DefaultSubcategory = @DefaultSubcategory,
                Priority = @Priority
            WHERE RuleId = @RuleId;
            """,
            new
            {
                RuleId = ruleId,
                MatchPattern = req.MatchPattern.Trim(),
                IsRegex = req.IsRegex ? 1 : 0,
                req.CategoryId,
                DefaultSubcategory = string.IsNullOrWhiteSpace(req.DefaultSubcategory) ? null : req.DefaultSubcategory.Trim(),
                req.Priority
            });

        if (rows == 0) return null;
        return await conn.QuerySingleAsync<CategorizationRule>("SELECT * FROM CategorizationRules WHERE RuleId = @Id;", new { Id = ruleId });
    }

    public async Task<bool> DeleteRuleAsync(int ruleId, CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        int rows = await conn.ExecuteAsync("DELETE FROM CategorizationRules WHERE RuleId = @RuleId;", new { RuleId = ruleId });
        return rows > 0;
    }

    public async Task<BackPropagateResponse> BackPropagateRulesAsync(BackPropagateRequest request, CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);

        var categories = (await conn.QueryAsync<Category>("SELECT * FROM Categories;"))
            .ToDictionary(c => c.CategoryId, c => c.Name);

        var rules = (await conn.QueryAsync<CategorizationRule>(
            "SELECT * FROM CategorizationRules ORDER BY Priority ASC, RuleId ASC;")).ToList();

        string sql = request.IncludeManuallyLocked
            ? "SELECT * FROM Transactions WHERE TransactionType IN ('Expense', 'Refund') ORDER BY TransactionDate DESC;"
            : "SELECT * FROM Transactions WHERE TransactionType IN ('Expense', 'Refund') AND IsCategoryManuallyLocked = 0 ORDER BY TransactionDate DESC;";

        var transactions = (await conn.QueryAsync<TransactionEntity>(sql)).ToList();
        var changes = new List<BackPropagatePreviewItem>();

        foreach (var tx in transactions)
        {
            foreach (var rule in rules)
            {
                bool matched = rule.IsRegex
                    ? Regex.IsMatch(tx.RawDescription, rule.MatchPattern, RegexOptions.IgnoreCase)
                    : tx.RawDescription.Contains(rule.MatchPattern, StringComparison.OrdinalIgnoreCase);

                if (matched)
                {
                    bool categoryChanged = tx.CategoryId != rule.CategoryId;
                    bool subcatChanged = !string.IsNullOrWhiteSpace(rule.DefaultSubcategory)
                        && !string.Equals(tx.Subcategory, rule.DefaultSubcategory, StringComparison.Ordinal);

                    if (categoryChanged || subcatChanged)
                    {
                        string oldName = tx.CategoryId.HasValue && categories.TryGetValue(tx.CategoryId.Value, out var oName)
                            ? oName
                            : "Uncategorized";
                        string newName = categories.TryGetValue(rule.CategoryId, out var nName)
                            ? nName
                            : "Unknown";

                        changes.Add(new BackPropagatePreviewItem
                        {
                            TransactionId = tx.TransactionId,
                            TransactionDate = tx.TransactionDate,
                            RawDescription = tx.RawDescription,
                            Amount = tx.Amount,
                            OldCategoryId = tx.CategoryId,
                            OldCategoryName = oldName,
                            NewCategoryId = rule.CategoryId,
                            NewCategoryName = newName,
                            NewSubcategory = rule.DefaultSubcategory ?? tx.Subcategory,
                            MatchedRulePattern = rule.MatchPattern
                        });
                    }
                    break;
                }
            }
        }

        string? backupFile = null;
        if (!request.DryRun && changes.Count > 0)
        {
            await using var dbTx = await conn.BeginTransactionAsync(ct);
            foreach (var change in changes)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE Transactions
                    SET CategoryId = @NewCategoryId,
                        Subcategory = @NewSubcategory,
                        IsCategoryManuallyLocked = 0
                    WHERE TransactionId = @TransactionId;
                    """,
                    change,
                    dbTx);
            }
            await dbTx.CommitAsync(ct);

            backupFile = await backupService.CreateSnapshotImmediateAsync("backprop", ct);
        }

        return new BackPropagateResponse
        {
            DryRun = request.DryRun,
            EvaluatedCount = transactions.Count,
            AffectedTransactionsCount = changes.Count,
            BackupFileCreated = backupFile,
            Changes = changes
        };
    }

    public async Task<object?> UpdateTransactionAsync(int transactionId, UpdateTransactionRequest req, CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        var existing = await conn.QuerySingleOrDefaultAsync<TransactionEntity>(
            "SELECT * FROM Transactions WHERE TransactionId = @Id;", new { Id = transactionId });

        if (existing is null) return null;

        int? newCatId = req.CategoryId ?? existing.CategoryId;
        string? newSubcat = req.Subcategory is not null
            ? (string.IsNullOrWhiteSpace(req.Subcategory) ? null : req.Subcategory.Trim())
            : existing.Subcategory;
        string newCleanName = !string.IsNullOrWhiteSpace(req.CleanMerchantName)
            ? req.CleanMerchantName.Trim()
            : existing.CleanMerchantName;
        string newTxType = !string.IsNullOrWhiteSpace(req.TransactionType)
            ? req.TransactionType.Trim()
            : existing.TransactionType;
        bool newLocked = req.IsCategoryManuallyLocked ?? (req.CategoryId.HasValue || existing.IsCategoryManuallyLocked);
        string? newNotes = req.Notes ?? existing.Notes;

        bool recatOccurred = existing.CategoryId != newCatId
            || !string.Equals(existing.Subcategory, newSubcat, StringComparison.Ordinal)
            || !string.Equals(existing.TransactionType, newTxType, StringComparison.Ordinal);

        await conn.ExecuteAsync(
            """
            UPDATE Transactions
            SET CategoryId = @CategoryId,
                Subcategory = @Subcategory,
                CleanMerchantName = @CleanMerchantName,
                TransactionType = @TransactionType,
                IsCategoryManuallyLocked = @IsLocked,
                Notes = @Notes
            WHERE TransactionId = @TransactionId;
            """,
            new
            {
                TransactionId = transactionId,
                CategoryId = newCatId,
                Subcategory = newSubcat,
                CleanMerchantName = newCleanName,
                TransactionType = newTxType,
                IsLocked = newLocked ? 1 : 0,
                Notes = newNotes
            });

        int backPropagatedCount = 0;
        if (!string.IsNullOrWhiteSpace(req.CreateRulePattern) && newCatId.HasValue)
        {
            await CreateRuleAsync(new UpsertRuleRequest
            {
                MatchPattern = req.CreateRulePattern.Trim(),
                IsRegex = false,
                CategoryId = newCatId.Value,
                DefaultSubcategory = newSubcat,
                Priority = req.CreateRulePriority ?? 50
            }, ct);

            if (req.BackPropagateRule)
            {
                var bpResult = await BackPropagateRulesAsync(new BackPropagateRequest
                {
                    IncludeManuallyLocked = false,
                    DryRun = false
                }, ct);
                backPropagatedCount = bpResult.AffectedTransactionsCount;
            }
        }

        if (recatOccurred && backPropagatedCount == 0)
        {
            backupService.ScheduleDebouncedRecategorizationSnapshot();
        }

        var updated = await conn.QuerySingleAsync<TransactionDto>(
            """
            SELECT t.*, a.AccountName, c.Name AS CategoryName
            FROM Transactions t
            JOIN Accounts a ON t.AccountId = a.AccountId
            LEFT JOIN Categories c ON t.CategoryId = c.CategoryId
            WHERE t.TransactionId = @Id;
            """,
            new { Id = transactionId });

        return new { updatedTransaction = updated, backPropagatedCount };
    }

    public async Task<List<TransactionDto>> SplitTransactionAsync(
        int transactionId,
        SplitTransactionRequest req,
        CancellationToken ct = default)
    {
        if (req.Splits.Count < 2)
        {
            throw new InvalidOperationException("At least 2 split items are required.");
        }

        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        var parent = await conn.QuerySingleOrDefaultAsync<TransactionEntity>(
            "SELECT * FROM Transactions WHERE TransactionId = @Id;", new { Id = transactionId })
            ?? throw new InvalidOperationException($"Transaction {transactionId} not found.");

        decimal splitSum = req.Splits.Sum(s => s.Amount);
        if (Math.Abs(splitSum - parent.Amount) > 0.01m)
        {
            throw new InvalidOperationException(
                $"Split amounts ({splitSum:F2}) must equal the original transaction amount ({parent.Amount:F2}).");
        }

        await using var tx = await conn.BeginTransactionAsync(ct);

        var createdIds = new List<int>();
        for (int i = 0; i < req.Splits.Count; i++)
        {
            var split = req.Splits[i];
            // First child preserves the exact parent DedupHash so future overlapping CSV uploads still detect it as already imported
            string childHash = i == 0 ? parent.DedupHash : $"{parent.DedupHash}_SPLIT_{i + 1}";

            int childId = await conn.ExecuteScalarAsync<int>(
                """
                INSERT INTO Transactions (
                    AccountId, TransactionDate, PostDate, RawDescription, CleanMerchantName,
                    Amount, TransactionType, CategoryId, Subcategory, IsCategoryManuallyLocked,
                    ParentTransactionId, DedupHash, UploadBatchId, Notes
                ) VALUES (
                    @AccountId, @TransactionDate, @PostDate, @RawDescription, @CleanMerchantName,
                    @Amount, @TransactionType, @CategoryId, @Subcategory, 1,
                    NULL, @DedupHash, @UploadBatchId, @Notes
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    parent.AccountId,
                    parent.TransactionDate,
                    parent.PostDate,
                    parent.RawDescription,
                    parent.CleanMerchantName,
                    split.Amount,
                    parent.TransactionType,
                    split.CategoryId,
                    Subcategory = string.IsNullOrWhiteSpace(split.Subcategory) ? null : split.Subcategory.Trim(),
                    DedupHash = childHash,
                    parent.UploadBatchId,
                    Notes = split.Notes
                },
                tx);

            createdIds.Add(childId);
        }

        await conn.ExecuteAsync("DELETE FROM Transactions WHERE TransactionId = @Id;", new { Id = transactionId }, tx);
        await tx.CommitAsync(ct);

        await backupService.CreateSnapshotImmediateAsync("data_edit", ct);

        var rows = await conn.QueryAsync<TransactionDto>(
            """
            SELECT t.*, a.AccountName, c.Name AS CategoryName
            FROM Transactions t
            JOIN Accounts a ON t.AccountId = a.AccountId
            LEFT JOIN Categories c ON t.CategoryId = c.CategoryId
            WHERE t.TransactionId IN @Ids
            ORDER BY t.TransactionId ASC;
            """,
            new { Ids = createdIds });

        return rows.ToList();
    }
}
