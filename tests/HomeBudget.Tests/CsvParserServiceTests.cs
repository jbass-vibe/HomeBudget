using System.Text;
using HomeBudget.Web.Models;
using HomeBudget.Web.Services;
using Xunit;

namespace HomeBudget.Tests;

public sealed class CsvParserServiceTests
{
    private readonly CsvParserService _parser = new();

    [Fact]
    public void ParseChaseCsv_ClassifiesExpensesRefundsAndPaymentsCorrectly()
    {
        string csvContent =
            """
            Transaction Date,Post Date,Description,Category,Type,Amount,Memo
            11/02/2026,11/03/2026,COSTCO WHSE #0423,Shopping,Sale,-245.80,
            11/10/2026,11/10/2026,PAYMENT THANK YOU - WEB,,Payment,1420.50,
            11/12/2026,11/13/2026,AMZN Mktp US REFUND,Shopping,Return,24.99,
            """;

        var account = new Account
        {
            AccountId = 2,
            AccountName = "Chase Sapphire",
            Institution = "Chase",
            AccountType = "CreditCard"
        };

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(csvContent));
        var rows = _parser.ParseCsvStream(ms, account, "GOOGLE");

        Assert.Equal(3, rows.Count);

        Assert.Equal("2026-11-02", rows[0].TransactionDate);
        Assert.Equal(TransactionTypes.Expense, rows[0].TransactionType);
        Assert.Equal(245.80m, rows[0].Amount);
        Assert.False(rows[0].RequiresManualReview);

        Assert.Equal("2026-11-10", rows[1].TransactionDate);
        Assert.Equal(TransactionTypes.CreditCardPayoff, rows[1].TransactionType);
        Assert.Equal(1420.50m, rows[1].Amount);

        Assert.Equal("2026-11-12", rows[2].TransactionDate);
        Assert.Equal(TransactionTypes.Refund, rows[2].TransactionType);
        Assert.Equal(-24.99m, rows[2].Amount);
    }

    [Fact]
    public void ParseWellsFargoCsv_DistinguishesPayrollFromNonPayrollPositiveCredits()
    {
        string csvContent =
            """
            "11/01/2026","4850.00","*","","GOOGLE INC DIRECT DEP PAYROLL"
            "11/05/2026","-185.40","*","","PG&E UTILITY BILL AUTOPAY"
            "11/10/2026","-1420.50","*","","CHASE CREDIT CRD EPAY AUTOPAY"
            "11/14/2026","42.15","*","","ZELLE FROM JANE DOE REIMBURSEMENT"
            """;

        var account = new Account
        {
            AccountId = 1,
            AccountName = "Wells Fargo Checking",
            Institution = "WellsFargo",
            AccountType = "Checking"
        };

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(csvContent));
        var rows = _parser.ParseCsvStream(ms, account, "GOOGLE");

        Assert.Equal(4, rows.Count);

        // 1. Verified Payroll -> IncomeDeposit
        Assert.Equal(TransactionTypes.IncomeDeposit, rows[0].TransactionType);
        Assert.Equal(4850.00m, rows[0].Amount);
        Assert.False(rows[0].RequiresManualReview);

        // 2. Utility outflow -> Expense
        Assert.Equal(TransactionTypes.Expense, rows[1].TransactionType);
        Assert.Equal(185.40m, rows[1].Amount);

        // 3. Credit card autopay -> CreditCardPayoff
        Assert.Equal(TransactionTypes.CreditCardPayoff, rows[2].TransactionType);
        Assert.Equal(1420.50m, rows[2].Amount);

        // 4. Non-payroll positive credit -> Refund + RequiresManualReview = true (does NOT silently inflate income)
        Assert.Equal(TransactionTypes.Refund, rows[3].TransactionType);
        Assert.Equal(-42.15m, rows[3].Amount);
        Assert.True(rows[3].RequiresManualReview);
    }

    [Fact]
    public void ExtractBaseHash_StripsSplitSuffixAndPreservesBaseFingerprint()
    {
        string baseHash = CsvParserService.ComputeBaseHash(1, "2026-11-02", "COSTCO WHSE #0423", 245.80m);
        Assert.Equal(24, baseHash.Length);

        string splitHash = $"{baseHash}_SPLIT_2";
        Assert.Equal(baseHash, CsvParserService.ExtractBaseHash(splitHash));
        Assert.Equal(baseHash, CsvParserService.ExtractBaseHash(baseHash));
    }
}
