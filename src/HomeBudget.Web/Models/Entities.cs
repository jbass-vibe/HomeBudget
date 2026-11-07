namespace HomeBudget.Web.Models;

public sealed class Account
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public string Institution { get; set; } = "";
    public string AccountType { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class Category
{
    public int CategoryId { get; set; }
    public string Name { get; set; } = "";
    public bool IsBigBoxMerchantCategory { get; set; }
    public decimal MonthlyBudget { get; set; }
    public int SortOrder { get; set; }
    public string ColorHex { get; set; } = "#4F46E5";
    public bool IsActive { get; set; } = true;
}

public sealed class CategorizationRule
{
    public int RuleId { get; set; }
    public string MatchPattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public int CategoryId { get; set; }
    public string? DefaultSubcategory { get; set; }
    public int Priority { get; set; }
}

public sealed class TransactionEntity
{
    public int TransactionId { get; set; }
    public int AccountId { get; set; }
    public string TransactionDate { get; set; } = "";
    public string? PostDate { get; set; }
    public string RawDescription { get; set; } = "";
    public string CleanMerchantName { get; set; } = "";
    public decimal Amount { get; set; }
    public string TransactionType { get; set; } = "Expense";
    public int? CategoryId { get; set; }
    public string? Subcategory { get; set; }
    public bool IsCategoryManuallyLocked { get; set; }
    public int? ParentTransactionId { get; set; }
    public string DedupHash { get; set; } = "";
    public int? UploadBatchId { get; set; }
    public string? Notes { get; set; }
}

public sealed class StagedCandidateRow
{
    public string RowId { get; set; } = Guid.NewGuid().ToString("N");
    public int AccountId { get; set; }
    public string SourceFileName { get; set; } = "";
    public string TransactionDate { get; set; } = "";
    public string? PostDate { get; set; }
    public string RawDescription { get; set; } = "";
    public string CleanMerchantName { get; set; } = "";
    public string? NativeCsvCategory { get; set; }
    public decimal Amount { get; set; }
    public string TransactionType { get; set; } = "Expense";
    public int? CategoryId { get; set; }
    public string? Subcategory { get; set; }
    public string DedupHash { get; set; } = "";
    public bool RequiresManualReview { get; set; }
    public string DedupStatus { get; set; } = "AutoApprove";
}

public sealed class StagedUploadSession
{
    public string StagingSessionId { get; set; } = Guid.NewGuid().ToString("N");
    public int AccountId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<string> FileNames { get; set; } = [];
    public List<StagedCandidateRow> Candidates { get; set; } = [];
}

public sealed class CommitStagingRequest
{
    public string StagingSessionId { get; set; } = "";
    public List<CommitRowDecision> Decisions { get; set; } = [];
}

public sealed class CommitRowDecision
{
    public string RowId { get; set; } = "";
    public string Action { get; set; } = "Import";
    public int? CategoryId { get; set; }
    public string? Subcategory { get; set; }
    public string? CleanMerchantName { get; set; }
    public string? TransactionType { get; set; }
}
