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
            "SELECT * FROM CategorizationRules ORDER BY Priority ASC, LENGTH(MatchPattern) DESC, RuleId ASC;");

        return new { categories, rules };
    }

    public async Task<Category> CreateCategoryAsync(UpsertCategoryRequest req, CancellationToken ct = default)
    {
        ValidateCategoryRequest(req);

        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        int id = await conn.ExecuteScalarAsync<int>(
            """
            INSERT INTO Categories (Name, IsBigBoxMerchantCategory, MonthlyBudget, SortOrder, ColorHex, IsActive, CreatedAt, UpdatedAt)
            VALUES (@Name, @IsBigBoxMerchantCategory, @MonthlyBudget, @SortOrder, @ColorHex, 1, datetime('now'), datetime('now'));
            SELECT last_insert_rowid();
            """,
            new
            {
                Name = req.Name.Trim(),
                IsBigBoxMerchantCategory = req.IsBigBoxMerchantCategory ? 1 : 0,
                MonthlyBudget = Math.Max(0m, req.MonthlyBudget),
                req.SortOrder,
                ColorHex = string.IsNullOrWhiteSpace(req.ColorHex) ? "#3B82F6" : req.ColorHex.Trim()
            });

        var created = await conn.QuerySingleAsync<Category>(
            "SELECT * FROM Categories WHERE CategoryId = @Id;", new { Id = id });

        await backupService.CreateSnapshotImmediateAsync("category_create", ct);
        return created;
    }

    public async Task<Category?> UpdateCategoryAsync(int categoryId, UpsertCategoryRequest req, CancellationToken ct = default)
    {
        ValidateCategoryRequest(req);

        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        int rows = await conn.ExecuteAsync(
            """
            UPDATE Categories
            SET Name = @Name,
                IsBigBoxMerchantCategory = @IsBigBoxMerchantCategory,
                MonthlyBudget = @MonthlyBudget,
                SortOrder = @SortOrder,
                ColorHex = @ColorHex,
                UpdatedAt = datetime('now')
            WHERE CategoryId = @CategoryId;
            """,
            new
            {
                CategoryId = categoryId,
                Name = req.Name.Trim(),
                IsBigBoxMerchantCategory = req.IsBigBoxMerchantCategory ? 1 : 0,
                MonthlyBudget = Math.Max(0m, req.MonthlyBudget),
                req.SortOrder,
                ColorHex = string.IsNullOrWhiteSpace(req.ColorHex) ? "#3B82F6" : req.ColorHex.Trim()
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
            var targetExists = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Categories WHERE CategoryId = @TargetId;",
                new { TargetId = replacementCategoryId.Value },
                tx);
            if (targetExists == 0)
            {
                throw new InvalidOperationException($"Replacement category {replacementCategoryId.Value} does not exist.");
            }

            affectedTransactions = await conn.ExecuteAsync(
                "UPDATE Transactions SET CategoryId = @TargetId, UpdatedAt = datetime('now') WHERE CategoryId = @SourceId;",
                new { SourceId = categoryId, TargetId = replacementCategoryId.Value },
                tx);

            affectedRules = await conn.ExecuteAsync(
                "UPDATE CategorizationRules SET CategoryId = @TargetId, UpdatedAt = datetime('now') WHERE CategoryId = @SourceId;",
                new { SourceId = categoryId, TargetId = replacementCategoryId.Value },
                tx);

            await conn.ExecuteAsync(
                "UPDATE Categories SET UpdatedAt = datetime('now') WHERE CategoryId = @TargetId;",
                new { TargetId = replacementCategoryId.Value },
                tx);
        }
        else
        {
            affectedTransactions = await conn.ExecuteAsync(
                """
                UPDATE Transactions
                SET CategoryId = NULL,
                    CategorizationSource = 'Uncategorized',
                    IsCategoryManuallyLocked = 0,
                    UpdatedAt = datetime('now')
                WHERE CategoryId = @SourceId;
                """,
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

        string? backupFile = await backupService.CreateSnapshotImmediateAsync("category_delete", ct);

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
            "UPDATE Transactions SET CategoryId = @TargetId, UpdatedAt = datetime('now') WHERE CategoryId = @SourceId;",
            new { SourceId = sourceCategoryId, TargetId = targetCategoryId },
            tx);

        int reassignedRules = await conn.ExecuteAsync(
            "UPDATE CategorizationRules SET CategoryId = @TargetId, UpdatedAt = datetime('now') WHERE CategoryId = @SourceId;",
            new { SourceId = sourceCategoryId, TargetId = targetCategoryId },
            tx);

        await conn.ExecuteAsync(
            "UPDATE Categories SET UpdatedAt = datetime('now') WHERE CategoryId = @TargetId;",
            new { TargetId = targetCategoryId },
            tx);

        await conn.ExecuteAsync(
            "DELETE FROM Categories WHERE CategoryId = @SourceId;",
            new { SourceId = sourceCategoryId },
            tx);

        await tx.CommitAsync(ct);

        string? backupFile = await backupService.CreateSnapshotImmediateAsync("category_merge", ct);

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
                    INSERT INTO Categories (Name, IsBigBoxMerchantCategory, MonthlyBudget, SortOrder, ColorHex, IsActive, CreatedAt, UpdatedAt)
                    VALUES (@Name, @IsBigBox, @Budget, 100, @ColorHex, 1, datetime('now'), datetime('now'));
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        Name = newName,
                        IsBigBox = req.NewCategoryIsBigBox ? 1 : 0,
                        Budget = Math.Max(0m, req.NewCategoryMonthlyBudget ?? 200m),
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
        else if (targetCategoryId.HasValue)
        {
            int catExists = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Categories WHERE CategoryId = @CatId;",
                new { CatId = targetCategoryId.Value },
                dbTx);
            if (catExists == 0)
            {
                throw new InvalidOperationException($"Category {targetCategoryId.Value} does not exist.");
            }
        }

        if (targetCategoryId.HasValue)
        {
            await conn.ExecuteAsync(
                "UPDATE Categories SET UpdatedAt = datetime('now') WHERE CategoryId = @CatId;",
                new { CatId = targetCategoryId.Value },
                dbTx);
        }

        string? cleanSubcat = req.Subcategory is not null
            ? (string.IsNullOrWhiteSpace(req.Subcategory) ? null : req.Subcategory.Trim())
            : existing.Subcategory;

        string cleanMerchant = !string.IsNullOrWhiteSpace(req.CleanMerchantName)
            ? req.CleanMerchantName.Trim()
            : existing.CleanMerchantName;

        string targetSource = targetCategoryId.HasValue ? "Manual" : "Uncategorized";

        await conn.ExecuteAsync(
            """
            UPDATE Transactions
            SET CategoryId = @CategoryId,
                Subcategory = @Subcategory,
                CleanMerchantName = @CleanMerchantName,
                CategorizationSource = @CategorizationSource,
                IsCategoryManuallyLocked = @IsLocked,
                UpdatedAt = datetime('now')
            WHERE TransactionId = @TransactionId;
            """,
            new
            {
                TransactionId = transactionId,
                CategoryId = targetCategoryId,
                Subcategory = cleanSubcat,
                CleanMerchantName = cleanMerchant,
                CategorizationSource = targetSource,
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
                        DefaultSubcategory = COALESCE(@Subcategory, DefaultSubcategory),
                        UpdatedAt = datetime('now')
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
                    INSERT INTO CategorizationRules (MatchPattern, IsRegex, CategoryId, DefaultSubcategory, Priority, CreatedAt, UpdatedAt)
                    VALUES (@Pattern, 0, @CategoryId, @Subcategory, 25, datetime('now'), datetime('now'));
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
            string pastSource = targetCategoryId.HasValue ? "Rule" : "Uncategorized";
            pastUpdatedCount = await conn.ExecuteAsync(
                $"""
                UPDATE Transactions
                SET CategoryId = @CategoryId,
                    Subcategory = COALESCE(@Subcategory, Subcategory),
                    CategorizationSource = @PastSource,
                    UpdatedAt = datetime('now')
                WHERE TransactionId != @CurrentId
                  AND TransactionType IN ('Expense', 'Refund')
                  AND (CleanMerchantName LIKE @LikePattern OR RawDescription LIKE @LikePattern)
                  {lockFilter};
                """,
                new
                {
                    CategoryId = targetCategoryId,
                    Subcategory = cleanSubcat,
                    PastSource = pastSource,
                    CurrentId = transactionId,
                    LikePattern = $"%{matchPattern}%"
                },
                dbTx);
        }

        await dbTx.CommitAsync(ct);

        string? backupFile = await backupService.RequestBackupAsync("recategorize", isMajorOperation: false, ct);

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

    public async Task<CategorizationRule> CreateRuleAsync(
        UpsertRuleRequest req,
        CancellationToken ct = default,
        bool suppressBackup = false)
    {
        ValidateRuleRequest(req);

        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        await EnsureCategoryExistsAsync(conn, req.CategoryId);

        int id = await conn.ExecuteScalarAsync<int>(
            """
            INSERT INTO CategorizationRules (MatchPattern, IsRegex, CategoryId, DefaultSubcategory, Priority, CreatedAt, UpdatedAt)
            VALUES (@MatchPattern, @IsRegex, @CategoryId, @DefaultSubcategory, @Priority, datetime('now'), datetime('now'));
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

        if (!suppressBackup)
        {
            await backupService.RequestBackupAsync("rule_create", isMajorOperation: false, ct);
        }

        return await conn.QuerySingleAsync<CategorizationRule>("SELECT * FROM CategorizationRules WHERE RuleId = @Id;", new { Id = id });
    }

    public async Task<CategorizationRule?> UpdateRuleAsync(int ruleId, UpsertRuleRequest req, CancellationToken ct = default)
    {
        ValidateRuleRequest(req);

        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        await EnsureCategoryExistsAsync(conn, req.CategoryId);

        int rows = await conn.ExecuteAsync(
            """
            UPDATE CategorizationRules
            SET MatchPattern = @MatchPattern,
                IsRegex = @IsRegex,
                CategoryId = @CategoryId,
                DefaultSubcategory = @DefaultSubcategory,
                Priority = @Priority,
                UpdatedAt = datetime('now')
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
        await backupService.RequestBackupAsync("rule_update", isMajorOperation: false, ct);
        return await conn.QuerySingleAsync<CategorizationRule>("SELECT * FROM CategorizationRules WHERE RuleId = @Id;", new { Id = ruleId });
    }

    public async Task<bool> DeleteRuleAsync(int ruleId, CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        int rows = await conn.ExecuteAsync("DELETE FROM CategorizationRules WHERE RuleId = @RuleId;", new { RuleId = ruleId });
        if (rows > 0)
        {
            await backupService.RequestBackupAsync("rule_delete", isMajorOperation: false, ct);
            return true;
        }
        return false;
    }

    public async Task<BackPropagateResponse> BackPropagateRulesAsync(
        BackPropagateRequest request,
        CancellationToken ct = default,
        bool suppressBackup = false)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);

        var categories = (await conn.QueryAsync<Category>("SELECT * FROM Categories;"))
            .ToDictionary(c => c.CategoryId, c => c.Name);

        var rules = (await conn.QueryAsync<CategorizationRule>(
            "SELECT * FROM CategorizationRules ORDER BY Priority ASC, LENGTH(MatchPattern) DESC, RuleId ASC;")).ToList();

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
                    ? SafeRegexIsMatch(tx.RawDescription, rule.MatchPattern)
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
                        CategorizationSource = 'Rule',
                        IsCategoryManuallyLocked = 0,
                        UpdatedAt = datetime('now')
                    WHERE TransactionId = @TransactionId;
                    """,
                    change,
                    dbTx);
            }
            await dbTx.CommitAsync(ct);

            if (!suppressBackup)
            {
                backupFile = await backupService.RequestBackupAsync("backprop", isMajorOperation: false, ct);
            }
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
        if (newCatId.HasValue)
        {
            await EnsureCategoryExistsAsync(conn, newCatId.Value);
        }

        string? newSubcat = req.Subcategory is not null
            ? (string.IsNullOrWhiteSpace(req.Subcategory) ? null : req.Subcategory.Trim())
            : existing.Subcategory;
        string newCleanName = !string.IsNullOrWhiteSpace(req.CleanMerchantName)
            ? req.CleanMerchantName.Trim()
            : existing.CleanMerchantName;
        string newTxType = !string.IsNullOrWhiteSpace(req.TransactionType)
            ? TransactionTypes.Normalize(req.TransactionType)
            : existing.TransactionType;

        if (!TransactionTypes.IsValid(newTxType))
        {
            throw new InvalidOperationException($"Unsupported TransactionType '{req.TransactionType}'.");
        }

        string newTxDate = !string.IsNullOrWhiteSpace(req.TransactionDate)
            ? req.TransactionDate.Trim()
            : existing.TransactionDate;
        decimal newAmount = req.Amount ?? existing.Amount;
        bool newLocked = req.IsCategoryManuallyLocked ?? (newCatId.HasValue || existing.IsCategoryManuallyLocked);
        string newSource = newCatId.HasValue ? (newLocked ? "Manual" : "Rule") : "Uncategorized";
        string? newNotes = req.Notes is not null
            ? (string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim())
            : existing.Notes;

        // DedupHash is intentionally preserved as immutable import provenance
        await conn.ExecuteAsync(
            """
            UPDATE Transactions
            SET TransactionDate = @TransactionDate,
                Amount = @Amount,
                CategoryId = @CategoryId,
                Subcategory = @Subcategory,
                CleanMerchantName = @CleanMerchantName,
                TransactionType = @TransactionType,
                CategorizationSource = @CategorizationSource,
                IsCategoryManuallyLocked = @IsLocked,
                Notes = @Notes,
                UpdatedAt = datetime('now')
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
                CategorizationSource = newSource,
                IsLocked = newLocked ? 1 : 0,
                Notes = newNotes
            });

        if (newCatId.HasValue && newCatId != existing.CategoryId)
        {
            await conn.ExecuteAsync(
                "UPDATE Categories SET UpdatedAt = datetime('now') WHERE CategoryId = @CatId;",
                new { CatId = newCatId.Value });
        }

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
            }, ct, suppressBackup: true);

            if (req.BackPropagateRule)
            {
                var bpResult = await BackPropagateRulesAsync(new BackPropagateRequest
                {
                    IncludeManuallyLocked = false,
                    DryRun = false
                }, ct, suppressBackup: true);
                backPropagatedCount = bpResult.AffectedTransactionsCount;
            }
        }

        string? backupFile = await backupService.RequestBackupAsync("data_edit", isMajorOperation: false, ct);

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

        string baseHash = CsvParserService.ExtractBaseHash(parent.DedupHash);

        await using var tx = await conn.BeginTransactionAsync(ct);

        var createdIds = new List<int>();
        for (int i = 0; i < req.Splits.Count; i++)
        {
            var split = req.Splits[i];
            string childHash = i == 0 ? baseHash : $"{baseHash}_SPLIT_{i + 1}";
            string catSource = split.CategoryId.HasValue ? "Manual" : "Uncategorized";

            int childId = await conn.ExecuteScalarAsync<int>(
                """
                INSERT INTO Transactions (
                    AccountId, TransactionDate, PostDate, RawDescription, CleanMerchantName,
                    Amount, TransactionType, CategoryId, Subcategory, CategorizationSource,
                    IsCategoryManuallyLocked, ParentTransactionId, DedupHash, UploadBatchId, Notes,
                    CreatedAt, UpdatedAt
                ) VALUES (
                    @AccountId, @TransactionDate, @PostDate, @RawDescription, @CleanMerchantName,
                    @Amount, @TransactionType, @CategoryId, @Subcategory, @CategorizationSource,
                    1, @ParentTransactionId, @DedupHash, @UploadBatchId, @Notes,
                    datetime('now'), datetime('now')
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
                    CategorizationSource = catSource,
                    ParentTransactionId = parent.ParentTransactionId ?? parent.TransactionId,
                    DedupHash = childHash,
                    parent.UploadBatchId,
                    Notes = string.IsNullOrWhiteSpace(split.Notes) ? parent.Notes : split.Notes.Trim()
                },
                tx);

            if (split.CategoryId.HasValue)
            {
                await conn.ExecuteAsync(
                    "UPDATE Categories SET UpdatedAt = datetime('now') WHERE CategoryId = @CatId;",
                    new { CatId = split.CategoryId.Value },
                    tx);
            }

            createdIds.Add(childId);
        }

        await conn.ExecuteAsync("DELETE FROM Transactions WHERE TransactionId = @Id;", new { Id = transactionId }, tx);
        await tx.CommitAsync(ct);

        await backupService.RequestBackupAsync("data_edit", isMajorOperation: false, ct);

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

    private static void ValidateCategoryRequest(UpsertCategoryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            throw new InvalidOperationException("Category name is required.");
        }
        if (req.MonthlyBudget < 0m)
        {
            throw new InvalidOperationException("Monthly budget cannot be negative.");
        }
    }

    private static void ValidateRuleRequest(UpsertRuleRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.MatchPattern))
        {
            throw new InvalidOperationException("Rule match pattern is required.");
        }
        if (req.IsRegex)
        {
            try
            {
                _ = new Regex(req.MatchPattern.Trim(), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Invalid regular expression pattern: {ex.Message}");
            }
        }
    }

    private static async Task EnsureCategoryExistsAsync(System.Data.Common.DbConnection conn, int categoryId)
    {
        int count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Categories WHERE CategoryId = @CategoryId;",
            new { CategoryId = categoryId });
        if (count == 0)
        {
            throw new InvalidOperationException($"Category {categoryId} does not exist.");
        }
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
