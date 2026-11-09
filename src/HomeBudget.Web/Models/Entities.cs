namespace HomeBudget.Web.Models;

public static class TransactionTypes
{
    public const string Expense = "Expense";
    public const string Refund = "Refund";
    public const string IncomeDeposit = "IncomeDeposit";
    public const string CreditCardPayoff = "CreditCardPayoff";

    public static readonly HashSet<string> CanonicalSet = new(StringComparer.OrdinalIgnoreCase)
    {
        Expense,
        Refund,
        IncomeDeposit,
        CreditCardPayoff
    };

    public static bool IsValid(string? type)
        => !string.IsNullOrWhiteSpace(type) && CanonicalSet.Contains(type.Trim());

    public static string Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return Expense;
        string t = type.Trim();
        if (t.Equals(Expense, StringComparison.OrdinalIgnoreCase)) return Expense;
        if (t.Equals(Refund, StringComparison.OrdinalIgnoreCase)) return Refund;
        if (t.Equals(IncomeDeposit, StringComparison.OrdinalIgnoreCase)) return IncomeDeposit;
        if (t.Equals(CreditCardPayoff, StringComparison.OrdinalIgnoreCase)
            || t.Equals("Payment", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Transfer", StringComparison.OrdinalIgnoreCase))
        {
            return CreditCardPayoff;
        }
        return t;
    }
}

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
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

public sealed class CategorizationRule
{
    public int RuleId { get; set; }
    public string MatchPattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public int CategoryId { get; set; }
    public string? DefaultSubcategory { get; set; }
    public int Priority { get; set; } = 100;
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
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
    public string TransactionType { get; set; } = TransactionTypes.Expense;
    public int? CategoryId { get; set; }
    public string? Subcategory { get; set; }
    public string CategorizationSource { get; set; } = "Uncategorized";
    public bool IsCategoryManuallyLocked { get; set; }
    public int? ParentTransactionId { get; set; }
    public string DedupHash { get; set; } = "";
    public string? UploadBatchId { get; set; }
    public string? Notes { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

public sealed class StagingSessionEntity
{
    public string StagingSessionId { get; set; } = "";
    public int AccountId { get; set; }
    public string FileNamesJson { get; set; } = "[]";
    public string CandidatesJson { get; set; } = "[]";
    public string CreatedAt { get; set; } = "";
}
