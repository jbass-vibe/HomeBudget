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

        var created = await conn.QuerySingleAsync<Category>(
            "SELECT * FROM Categories WHERE CategoryId = @Id;", new { Id = id });

        await backupService.CreateSnapshotImmediateAsync("category_create", ct);
        return created;
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

        var updated = await conn.QuerySingleAsync<Category>(
            "SELECT * FROM Categories WHERE CategoryId = @Id;", new { Id = categoryId });

        await backupService.CreateSnapshotImmediateAsync("category_update", ct);
        return updated;
    }

    public async Task<object?> DeleteCategoryAsync(int categoryId, int? replacementCategoryId, CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        var existing = await conn.QuerySingleOrDefaultAsync<Category>(
            "SELECT * FROM Categories WHERE CategoryId = @Id;", new { Id = categoryId });
        if (existing is null) return null;

        await using var tx = await conn.BeginTransactionAsync(ct);
        int affectedTransactions;
        int affectedRules;

        if (replacementCategoryId.HasValue && replacementCategoryId.Value != categoryId)
        {
            affectedTransactions = await conn.ExecuteAsync(
                "UPDATE Transactions SET CategoryId = @TargetId WHERE CategoryId = @SourceId;",
                new { SourceId = categoryId, TargetId = replacementCategoryId.Value },
                tx);

            affectedRules = await conn.ExecuteAsync(
                "UPDATE CategorizationRules SET CategoryId = @TargetId WHERE CategoryId = @SourceId;",
                new { SourceId = categoryId, TargetId = replacementCategoryId.Value },
                tx);
        }
        else
        {
            affectedTransactions = await conn.ExecuteAsync(
                "UPDATE Transactions SET CategoryId = NULL, IsCategoryManuallyLocked = 0 WHERE CategoryId = @SourceId;",
                new { SourceId = categoryId },
                tx);

            affectedRules = await conn.ExecuteAsync(
                "DELETE FROM CategorizationRules WHERE CategoryId = @SourceId;",
                new { SourceId = categoryId },
                tx);
        }

        await conn.ExecuteAsync(
            "DELETE FROM Categories WHERE CategoryId = @SourceId;",
            new { SourceId = categoryId },
            tx);

        await tx.CommitAsync(ct);

        string backupFile = await backupService.CreateSnapshotImmediateAsync("category_delete", ct);

        return new
        {
            deletedCategoryId = categoryId,
            replacementCategoryId,
            affectedTransactionsCount = affectedTransactions,
            affectedRulesCount = affectedRules,
            backupFileCreated = backupFile
        };
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

        string backupFile = await backupService.CreateSnapshotImmediateAsync("category_merge", ct);

        return new
        {
            sourceCategoryId,
            targetCategoryId,
            reassignedTransactionsCount = reassignedTransactions,
            reassignedRulesCount = reassignedRules,
            backupFileCreated = backupFile
        };
    }

    public async Task<object> GetSimilarTransactionsCountAsync(
        string? pattern,
        int? excludeTransactionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new { pattern = "", matchingPastCount = 0 };
        }

        string trimmed = pattern.Trim();
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        int count = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM Transactions
            WHERE TransactionType IN ('Expense', 'Refund')
              AND (@ExcludeId IS NULL OR TransactionId != @ExcludeId)
              AND (CleanMerchantName LIKE @LikePattern OR RawDescription LIKE @LikePattern);
            """,
            new
            {
                ExcludeId = excludeTransactionId,
                LikePattern = $"%{trimmed}%"
            });

        return new { pattern = trimmed, matchingPastCount = count };
    }

    public async Task<object?> CategorizeTransactionWithOptionsAsync(
        int transactionId,
        CategorizeTransactionRequest req,
        CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        var existing = await conn.QuerySingleOrDefaultAsync<TransactionEntity>(
            "SELECT * FROM Transactions WHERE TransactionId = @Id;", new { Id = transactionId });

        if (existing is null) return null;

        await using var dbTx = await conn.BeginTransactionAsync(ct);

        Category? createdCategory = null;
        int? targetCategoryId = req.CategoryId;

        if (!targetCategoryId.HasValue && !string.IsNullOrWhiteSpace(req.NewCategoryName))
        {
            string newName = req.NewCategoryName.Trim();
            var existingCat = await conn.QuerySingleOrDefaultAsync<Category>(
                "SELECT * FROM Categories WHERE lower(Name) = lower(@Name);",
                new { Name = newName },
                dbTx);

            if (existingCat is not null)
            {
                targetCategoryId = existingCat.CategoryId;
            }
            else
            {
                int newCatId = await conn.ExecuteScalarAsync<int>(
                    """
                    INSERT INTO Categories (Name, IsBigBoxMerchantCategory, MonthlyBudget, SortOrder, ColorHex, IsActive)
                    VALUES (@Name, @IsBigBox, @Budget, 100, @ColorHex, 1);
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Name = newName,
                        IsBigBox = req.NewCategoryIsBigBox ? 1 : 0,
                        Budget = req.NewCategoryMonthlyBudget ?? 200m,
                        ColorHex = string.IsNullOrWhiteSpace(req.NewCategoryColorHex) ? "#3B82F6" : req.NewCategoryColorHex.Trim()
                    },
                    dbTx);

                createdCategory = await conn.QuerySingleAsync<Category>(
                    "SELECT * FROM Categories WHERE CategoryId = @Id;",
                    new { Id = newCatId },
                    dbTx);

                targetCategoryId = newCatId;
            }
        }

        string? cleanSubcat = req.Subcategory is not null
            ? (string.IsNullOrWhiteSpace(req.Subcategory) ? null : req.Subcategory.Trim())
            : existing.Subcategory;

        string cleanMerchant = !string.IsNullOrWhiteSpace(req.CleanMerchantName)
            ? req.CleanMerchantName.Trim()
            : existing.CleanMerchantName;

        await conn.ExecuteAsync(
            """
            UPDATE Transactions
            SET CategoryId = @CategoryId,
                Subcategory = @Subcategory,
                CleanMerchantName = @CleanMerchantName,
                IsCategoryManuallyLocked = @IsLocked
            WHERE TransactionId = @TransactionId;
            """,
            new
            {
                TransactionId = transactionId,
                CategoryId = targetCategoryId,
                Subcategory = cleanSubcat,
                CleanMerchantName = cleanMerchant,
                IsLocked = targetCategoryId.HasValue ? 1 : 0
            },
            dbTx);

        string matchPattern = !string.IsNullOrWhiteSpace(req.MatchPattern)
            ? req.MatchPattern.Trim()
            : cleanMerchant;

        bool ruleSaved = false;
        if (req.ApplyMovingForward && targetCategoryId.HasValue && !string.IsNullOrWhiteSpace(matchPattern))
        {
            var existingRule = await conn.QueryFirstOrDefaultAsync<CategorizationRule>(
                "SELECT * FROM CategorizationRules WHERE lower(MatchPattern) = lower(@Pattern) AND IsRegex = 0;",
                new { Pattern = matchPattern },
                dbTx);

            if (existingRule is not null)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE CategorizationRules
                    SET CategoryId = @CategoryId,
                        DefaultSubcategory = COALESCE(@Subcategory, DefaultSubcategory)
                    WHERE RuleId = @RuleId;
                    """,
                    new
                    {
                        existingRule.RuleId,
                        CategoryId = targetCategoryId.Value,
                        Subcategory = cleanSubcat
                    },
                    dbTx);
            }
            else
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO CategorizationRules (MatchPattern, IsRegex, CategoryId, DefaultSubcategory, Priority)
                    VALUES (@Pattern, 0, @CategoryId, @Subcategory, 25);
                    """,
                    new
                    {
                        Pattern = matchPattern,
                        CategoryId = targetCategoryId.Value,
                        Subcategory = cleanSubcat
                    },
                    dbTx);
            }
            ruleSaved = true;
        }

        int pastUpdatedCount = 0;
        if (req.ApplyToPast && !string.IsNullOrWhiteSpace(matchPattern))
        {
            string lockFilter = req.IncludeManuallyLockedInPast ? "" : "AND IsCategoryManuallyLocked = 0";
            pastUpdatedCount = await conn.ExecuteAsync(
                $"""
                UPDATE Transactions
                SET CategoryId = @CategoryId,
                    Subcategory = COALESCE(@Subcategory, Subcategory)
                WHERE TransactionId != @CurrentId
                  AND TransactionType IN ('Expense', 'Refund')
                  AND (CleanMerchantName LIKE @LikePattern OR RawDescription LIKE @LikePattern)
                  {lockFilter};
                """,
                new
                {
                    CategoryId = targetCategoryId,
                    Subcategory = cleanSubcat,
                    CurrentId = transactionId,
                    LikePattern = $"%{matchPattern}%"
                },
                dbTx);
        }

        await dbTx.CommitAsync(ct);

        string backupFile = await backupService.CreateSnapshotImmediateAsync("recategorize", ct);

        var updatedTx = await conn.QuerySingleAsync<TransactionDto>(
            """
            SELECT t.*, a.AccountName, c.Name AS CategoryName
            FROM Transactions t
            JOIN Accounts a ON t.AccountId = a.AccountId
            LEFT JOIN Categories c ON t.CategoryId = c.CategoryId
            WHERE t.TransactionId = @Id;
            """,
            new { Id = transactionId });

        return new
        {
            updatedTransaction = updatedTx,
            createdCategory,
            ruleSaved,
            pastUpdatedCount,
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

        await backupService.CreateSnapshotImmediateAsync("rule_create", ct);
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
        await backupService.CreateSnapshotImmediateAsync("rule_update", ct);
        return await conn.QuerySingleAsync<CategorizationRule>("SELECT * FROM CategorizationRules WHERE RuleId = @Id;", new { Id = ruleId });
    }

    public async Task<bool> DeleteRuleAsync(int ruleId, CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        int rows = await conn.ExecuteAsync("DELETE FROM CategorizationRules WHERE RuleId = @RuleId;", new { RuleId = ruleId });
        if (rows > 0)
        {
            await backupService.CreateSnapshotImmediateAsync("rule_delete", ct);
            return true;
        }
        return false;
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
                    : tx.RawDescription.Contains(rule.MatchPattern, StringComparison.OrdinalIgnoreCase)
                      || tx.CleanMerchantName.Contains(rule.MatchPattern, StringComparison.OrdinalIgnoreCase);

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

        int? newCatId = req.ClearCategory ? null : (req.CategoryId ?? existing.CategoryId);
        string? newSubcat = req.Subcategory is not null
            ? (string.IsNullOrWhiteSpace(req.Subcategory) ? null : req.Subcategory.Trim())
            : existing.Subcategory;
        string newCleanName = !string.IsNullOrWhiteSpace(req.CleanMerchantName)
            ? req.CleanMerchantName.Trim()
            : existing.CleanMerchantName;
        string newTxType = !string.IsNullOrWhiteSpace(req.TransactionType)
            ? req.TransactionType.Trim()
            : existing.TransactionType;
        string newTxDate = !string.IsNullOrWhiteSpace(req.TransactionDate)
            ? req.TransactionDate.Trim()
            : existing.TransactionDate;
        decimal newAmount = req.Amount ?? existing.Amount;
        bool newLocked = req.IsCategoryManuallyLocked ?? (newCatId.HasValue || existing.IsCategoryManuallyLocked);
        string? newNotes = req.Notes ?? existing.Notes;

        await conn.ExecuteAsync(
            """
            UPDATE Transactions
            SET TransactionDate = @TransactionDate,
                Amount = @Amount,
                CategoryId = @CategoryId,
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
                TransactionDate = newTxDate,
                Amount = newAmount,
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

        string backupFile = await backupService.CreateSnapshotImmediateAsync("data_edit", ct);

        var updated = await conn.QuerySingleAsync<TransactionDto>(
            """
            SELECT t.*, a.AccountName, c.Name AS CategoryName
            FROM Transactions t
            JOIN Accounts a ON t.AccountId = a.AccountId
            LEFT JOIN Categories c ON t.CategoryId = c.CategoryId
            WHERE t.TransactionId = @Id;
            """,
            new { Id = transactionId });

        return new { updatedTransaction = updated, backPropagatedCount, backupFileCreated = backupFile };
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
