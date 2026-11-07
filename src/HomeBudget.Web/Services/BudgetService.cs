using System.Globalization;
using Dapper;
using HomeBudget.Web.Data;
using HomeBudget.Web.Models;

namespace HomeBudget.Web.Services;

public sealed class BudgetService(SqliteConnectionFactory connectionFactory)
{
    public async Task<MonthlyBudgetViewDto> GetMonthlyBudgetAsync(string? yearMonth, CancellationToken ct = default)
    {
        DateOnly targetMonth;
        if (!string.IsNullOrWhiteSpace(yearMonth)
            && DateOnly.TryParseExact($"{yearMonth.Trim()}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            targetMonth = parsed;
        }
        else
        {
            DateOnly now = DateOnly.FromDateTime(DateTime.Now);
            targetMonth = new DateOnly(now.Year, now.Month, 1);
        }

        string ym = targetMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        DateOnly endOfMonth = new(targetMonth.Year, targetMonth.Month, DateTime.DaysInMonth(targetMonth.Year, targetMonth.Month));

        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);

        var settingsDict = (await conn.QueryAsync<(string Key, string Value)>("SELECT Key, Value FROM Settings;"))
            .ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

        decimal persistentMonthlyIncome = settingsDict.TryGetValue("PersistentMonthlyIncome", out var pVal)
            && decimal.TryParse(pVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedIncome)
            ? parsedIncome
            : 10000.00m;

        string currentMonthMode = settingsDict.TryGetValue("CurrentMonthIncomeMode", out var mVal) ? mVal : "Auto";

        var overrides = (await conn.QueryAsync<IncomeOverrideDto>("SELECT YearMonth, IncomeAmount, Notes FROM IncomeOverrides;"))
            .ToDictionary(o => o.YearMonth, o => o.IncomeAmount, StringComparer.OrdinalIgnoreCase);

        var incomeBreakdown = await CashflowService.ResolveTieredIncomeBreakdownAsync(
            conn, targetMonth, endOfMonth, true, persistentMonthlyIncome, currentMonthMode, overrides);

        var monthIncomeInfo = incomeBreakdown.First();

        var categories = (await conn.QueryAsync<Category>(
            "SELECT * FROM Categories WHERE IsActive = 1 ORDER BY SortOrder ASC, Name ASC;")).ToList();

        var transactions = (await conn.QueryAsync<TransactionDto>(
            """
            SELECT t.*, a.AccountName, c.Name AS CategoryName
            FROM Transactions t
            JOIN Accounts a ON t.AccountId = a.AccountId
            LEFT JOIN Categories c ON t.CategoryId = c.CategoryId
            WHERE substr(t.TransactionDate, 1, 7) = @YearMonth
            ORDER BY t.TransactionDate DESC, t.TransactionId DESC;
            """,
            new { YearMonth = ym })).ToList();

        decimal creditCardPayoffsTotal = transactions
            .Where(t => t.TransactionType == "CreditCardPayoff")
            .Sum(t => t.Amount);

        var spendTxs = transactions
            .Where(t => t.TransactionType is "Expense" or "Refund")
            .ToList();

        var cards = new List<CategoryBudgetCardDto>();
        foreach (var cat in categories)
        {
            var catTxs = spendTxs.Where(t => t.CategoryId == cat.CategoryId).ToList();
            decimal netSpent = Math.Round(catTxs.Sum(t => t.Amount), 2);
            decimal budget = Math.Round(cat.MonthlyBudget, 2);
            decimal remaining = Math.Round(budget - netSpent, 2);
            decimal pctUsed = budget > 0 ? Math.Round((netSpent / budget) * 100m, 1) : (netSpent > 0 ? 100m : 0m);

            string statusLevel = budget <= 0
                ? "Unbudgeted"
                : pctUsed > 100m
                    ? "Red"
                    : pctUsed >= 80m
                        ? "Amber"
                        : "Green";

            cards.Add(new CategoryBudgetCardDto
            {
                CategoryId = cat.CategoryId,
                Name = cat.Name,
                IsBigBoxMerchantCategory = cat.IsBigBoxMerchantCategory,
                ColorHex = cat.ColorHex,
                MonthlyBudget = budget,
                NetSpent = netSpent,
                Remaining = remaining,
                PctUsed = pctUsed,
                StatusLevel = statusLevel,
                Transactions = catTxs
            });
        }

        decimal totalBudgeted = cards.Sum(c => c.MonthlyBudget);
        decimal totalNetSpent = Math.Round(spendTxs.Sum(t => t.Amount), 2);

        return new MonthlyBudgetViewDto
        {
            YearMonth = ym,
            ResolvedIncome = monthIncomeInfo.ResolvedFullMonthIncome,
            IncomeSourceType = monthIncomeInfo.SourceType,
            TotalBudgeted = totalBudgeted,
            TotalNetSpent = totalNetSpent,
            TotalRemaining = Math.Round(totalBudgeted - totalNetSpent, 2),
            CreditCardPayoffsTotal = Math.Round(creditCardPayoffsTotal, 2),
            Categories = cards
        };
    }

    public async Task<Category?> UpdateCategoryMonthlyBudgetAsync(int categoryId, decimal monthlyBudget, CancellationToken ct = default)
    {
        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        int updated = await conn.ExecuteAsync(
            "UPDATE Categories SET MonthlyBudget = @MonthlyBudget WHERE CategoryId = @CategoryId;",
            new { CategoryId = categoryId, MonthlyBudget = Math.Max(0m, monthlyBudget) });

        if (updated == 0) return null;
        return await conn.QuerySingleAsync<Category>(
            "SELECT * FROM Categories WHERE CategoryId = @CategoryId;",
            new { CategoryId = categoryId });
    }
}
