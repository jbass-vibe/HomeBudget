namespace HomeBudget.Web.Models;

public sealed class CashflowResponse
{
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    public string WindowType { get; set; } = "mtd";
    public bool IncludeSubcategories { get; set; }
    public bool HasAnySubcategories { get; set; }
    public decimal TotalIncome { get; set; }
    public string PrimaryIncomeSourceLabel { get; set; } = "";
    public List<MonthlyIncomeBreakdownDto> IncomeBreakdown { get; set; } = [];
    public decimal TotalNetSpend { get; set; }
    public decimal UnspentCashflow { get; set; }
    public decimal OverIncomeDeficit { get; set; }
    public decimal CreditCardPayoffsTotal { get; set; }
    public SankeyGraphDto Sankey { get; set; } = new();
    public List<AccountHeaderDto> Accounts { get; set; } = [];
    public List<CashflowTableRowDto> TableRows { get; set; } = [];
}

public sealed class AccountHeaderDto
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public string Institution { get; set; } = "";
    public string AccountType { get; set; } = "";
}

public sealed class MonthlyIncomeBreakdownDto
{
    public string YearMonth { get; set; } = "";
    public decimal ResolvedFullMonthIncome { get; set; }
    public decimal ProRatedWindowIncome { get; set; }
    public int ActiveDays { get; set; }
    public int TotalDaysInMonth { get; set; }
    public string SourceType { get; set; } = ""; // 'ActualGoogleDeposits' | 'MonthlyOverride' | 'PersistentFallback'
    public int DepositCount { get; set; }
}

public sealed class SankeyGraphDto
{
    public List<SankeyNodeDto> Nodes { get; set; } = [];
    public List<SankeyLinkDto> Links { get; set; } = [];
}

public sealed class SankeyNodeDto
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ColorHex { get; set; } = "#4F46E5";
    public decimal Amount { get; set; }
    public string NodeType { get; set; } = ""; // 'Income' | 'Deficit' | 'Summary' | 'Category' | 'Subcategory'
}

public sealed class SankeyLinkDto
{
    public int Source { get; set; }
    public int Target { get; set; }
    public decimal Value { get; set; }
    public string ColorHex { get; set; } = "#94A3B8";
}

public sealed class CashflowTableRowDto
{
    public int? CategoryId { get; set; }
    public string CategoryName { get; set; } = "";
    public string ColorHex { get; set; } = "#64748B";
    public decimal NetSpend { get; set; }
    public decimal PctOfTotalSpend { get; set; }
    public decimal PctOfIncome { get; set; }
    public Dictionary<int, decimal> SpendByAccount { get; set; } = [];
    public List<SubcategoryBreakdownDto> Subcategories { get; set; } = [];
    public List<TransactionDto> Transactions { get; set; } = [];
}

public sealed class SubcategoryBreakdownDto
{
    public string SubcategoryName { get; set; } = "";
    public decimal NetSpend { get; set; }
}

public sealed class MonthlyBudgetViewDto
{
    public string YearMonth { get; set; } = "";
    public decimal ResolvedIncome { get; set; }
    public string IncomeSourceType { get; set; } = "";
    public decimal TotalBudgeted { get; set; }
    public decimal TotalNetSpent { get; set; }
    public decimal TotalRemaining { get; set; }
    public decimal CreditCardPayoffsTotal { get; set; }
    public List<CategoryBudgetCardDto> Categories { get; set; } = [];
}

public sealed class CategoryBudgetCardDto
{
    public int CategoryId { get; set; }
    public string Name { get; set; } = "";
    public bool IsBigBoxMerchantCategory { get; set; }
    public string ColorHex { get; set; } = "#4F46E5";
    public decimal MonthlyBudget { get; set; }
    public decimal NetSpent { get; set; }
    public decimal Remaining { get; set; }
    public decimal PctUsed { get; set; }
    public string StatusLevel { get; set; } = "Green"; // 'Green' (<80%), 'Amber' (80-100%), 'Red' (>100%), 'Unbudgeted'
    public List<TransactionDto> Transactions { get; set; } = [];
}

public sealed class TransactionDto
{
    public int TransactionId { get; set; }
    public int AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public string TransactionDate { get; set; } = "";
    public string? PostDate { get; set; }
    public string RawDescription { get; set; } = "";
    public string CleanMerchantName { get; set; } = "";
    public decimal Amount { get; set; }
    public string TransactionType { get; set; } = "Expense";
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? Subcategory { get; set; }
    public bool IsCategoryManuallyLocked { get; set; }
    public int? ParentTransactionId { get; set; }
    public string? Notes { get; set; }
}

public sealed class UpdateTransactionRequest
{
    public int? CategoryId { get; set; }
    public string? Subcategory { get; set; }
    public string? CleanMerchantName { get; set; }
    public string? TransactionType { get; set; }
    public bool? IsCategoryManuallyLocked { get; set; }
    public string? Notes { get; set; }
    public string? CreateRulePattern { get; set; }
    public int? CreateRulePriority { get; set; }
    public bool BackPropagateRule { get; set; }
}

public sealed class SplitTransactionRequest
{
    public List<SplitItemDto> Splits { get; set; } = [];
}

public sealed class SplitItemDto
{
    public decimal Amount { get; set; }
    public int? CategoryId { get; set; }
    public string? Subcategory { get; set; }
    public string? Notes { get; set; }
}

public sealed class UpdateBudgetRequest
{
    public decimal MonthlyBudget { get; set; }
}

public sealed class UpsertCategoryRequest
{
    public string Name { get; set; } = "";
    public bool IsBigBoxMerchantCategory { get; set; }
    public decimal MonthlyBudget { get; set; }
    public int SortOrder { get; set; } = 100;
    public string ColorHex { get; set; } = "#4F46E5";
}

public sealed class UpsertRuleRequest
{
    public string MatchPattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public int CategoryId { get; set; }
    public string? DefaultSubcategory { get; set; }
    public int Priority { get; set; } = 100;
}

public sealed class BackPropagateRequest
{
    public bool IncludeManuallyLocked { get; set; }
    public bool DryRun { get; set; } = true;
}

public sealed class BackPropagatePreviewItem
{
    public int TransactionId { get; set; }
    public string TransactionDate { get; set; } = "";
    public string RawDescription { get; set; } = "";
    public decimal Amount { get; set; }
    public int? OldCategoryId { get; set; }
    public string OldCategoryName { get; set; } = "Uncategorized";
    public int NewCategoryId { get; set; }
    public string NewCategoryName { get; set; } = "";
    public string? NewSubcategory { get; set; }
    public string MatchedRulePattern { get; set; } = "";
}

public sealed class BackPropagateResponse
{
    public bool DryRun { get; set; }
    public int EvaluatedCount { get; set; }
    public int AffectedTransactionsCount { get; set; }
    public string? BackupFileCreated { get; set; }
    public List<BackPropagatePreviewItem> Changes { get; set; } = [];
}

public sealed class SettingsDto
{
    public decimal PersistentMonthlyIncome { get; set; } = 10000.00m;
    public string GooglePayrollMatchPattern { get; set; } = "GOOGLE";
    public string CurrentMonthIncomeMode { get; set; } = "Auto";
    public string GeminiApiKey { get; set; } = "";
}

public sealed class IncomeOverrideDto
{
    public string YearMonth { get; set; } = "";
    public decimal IncomeAmount { get; set; }
    public string? Notes { get; set; }
}
