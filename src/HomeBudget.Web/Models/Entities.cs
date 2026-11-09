namespace HomeBudget.Web.Models;

public sealed class Account
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public string Institution { get; set; } = "Chase";
    public string AccountType { get; set; } = "CreditCard";
    public bool IsActive { get; set; } = true;
    public string CreatedAt { get; set; } = "";
}

public sealed class Category
{
    public int CategoryId { get; set; }
    public string Name { get; set; } = "";
    public bool IsBigBoxMerchantCategory { get; set; }
    public decimal MonthlyBudget { get; set; }
    public int SortOrder { get; set; } = 100;
    public string ColorHex { get; set; } = "#3B82F6";
    public bool IsActive { get; set; } = true;
}

public sealed class CategorizationRule
{
    public int RuleId { get; set; }
    public string MatchPattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public int CategoryId { get; set; }
    public string? DefaultSubcategory { get; set; }
    public int Priority { get; set; } = 100;
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
    public string? UploadBatchId { get; set; }
    public string? Notes { get; set; }
    public string? CreatedAt { get; set; }
}
