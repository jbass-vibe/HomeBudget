using System.Globalization;
using Dapper;
using HomeBudget.Web.Data;
using HomeBudget.Web.Models;

namespace HomeBudget.Web.Services;

public sealed class CashflowService(SqliteConnectionFactory connectionFactory)
{
    public async Task<CashflowResponse> GetCashflowAsync(
        string? windowType,
        string? startDateStr,
        string? endDateStr,
        int? monthsCount,
        bool includeSubcategories,
        string? categorySort = null,
        CancellationToken ct = default)
    {
        (DateOnly startDate, DateOnly endDate, string resolvedWindowType, bool isFullMonthWindow) =
            ResolveDateWindow(windowType, startDateStr, endDateStr, monthsCount);

        string isoStart = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string isoEnd = endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string normalizedSort = NormalizeCategorySort(categorySort);

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

        // 1. Resolve Tiered Income across constituent calendar months
        var monthBreakdowns = await ResolveTieredIncomeBreakdownAsync(
            conn, startDate, endDate, isFullMonthWindow, persistentMonthlyIncome, currentMonthMode, overrides);

        decimal totalIncome = Math.Round(monthBreakdowns.Sum(m => m.ProRatedWindowIncome), 2);
        string primarySourceLabel = DeterminePrimaryIncomeLabel(monthBreakdowns);

        // 2. Load Accounts & Categories
        var accounts = (await conn.QueryAsync<AccountHeaderDto>(
            "SELECT AccountId, AccountName, Institution, AccountType FROM Accounts WHERE IsActive = 1 ORDER BY AccountId ASC;")).ToList();

        var categories = (await conn.QueryAsync<Category>(
            "SELECT * FROM Categories WHERE IsActive = 1 ORDER BY SortOrder ASC, Name ASC;")).ToList();

        // 3. Load all Transactions in [isoStart, isoEnd]
        var transactions = (await conn.QueryAsync<TransactionDto>(
            """
            SELECT t.*, a.AccountName, c.Name AS CategoryName
            FROM Transactions t
            JOIN Accounts a ON t.AccountId = a.AccountId
            LEFT JOIN Categories c ON t.CategoryId = c.CategoryId
            WHERE t.TransactionDate >= @StartDate AND t.TransactionDate <= @EndDate
            ORDER BY t.TransactionDate DESC, t.TransactionId DESC;
            """,
            new { StartDate = isoStart, EndDate = isoEnd })).ToList();

        decimal creditCardPayoffsTotal = transactions
            .Where(t => t.TransactionType == TransactionTypes.CreditCardPayoff)
            .Sum(t => t.Amount);

        var spendTransactions = transactions
            .Where(t => t.TransactionType is TransactionTypes.Expense or TransactionTypes.Refund)
            .ToList();

        bool hasAnySubcategories = spendTransactions.Any(t => !string.IsNullOrWhiteSpace(t.Subcategory));

        // 4. Build Companion Tabular Breakdown by Category & Account Source
        var tableRows = new List<CashflowTableRowDto>();

        decimal totalPositiveCategorySpend = 0m;
        decimal totalNetSpend = Math.Round(spendTransactions.Sum(t => t.Amount), 2);

        foreach (var cat in categories)
        {
            var catTxs = spendTransactions.Where(t => t.CategoryId == cat.CategoryId).ToList();
            if (catTxs.Count == 0) continue;

            decimal netSpend = Math.Round(catTxs.Sum(t => t.Amount), 2);
            if (netSpend > 0) totalPositiveCategorySpend += netSpend;

            var spendByAcct = accounts.ToDictionary(
                a => a.AccountId,
                a => Math.Round(catTxs.Where(t => t.AccountId == a.AccountId).Sum(t => t.Amount), 2));

            string maxTxActivity = catTxs
                .Select(t => t.UpdatedAt ?? t.CreatedAt ?? t.TransactionDate)
                .DefaultIfEmpty("")
                .Max() ?? "";

            string catLastActivity = string.CompareOrdinal(cat.UpdatedAt ?? "", maxTxActivity) >= 0
                ? (cat.UpdatedAt ?? maxTxActivity)
                : maxTxActivity;

            var subcatGroups = catTxs
                .Where(t => !string.IsNullOrWhiteSpace(t.Subcategory))
                .GroupBy(t => t.Subcategory!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new SubcategoryBreakdownDto
                {
                    SubcategoryName = g.Key,
                    NetSpend = Math.Round(g.Sum(x => x.Amount), 2),
                    LastActivityAt = g.Select(x => x.UpdatedAt ?? x.CreatedAt ?? x.TransactionDate).DefaultIfEmpty("").Max() ?? ""
                })
                .Where(s => s.NetSpend > 0);

            var subcats = SortSubcategories(subcatGroups, normalizedSort);

            tableRows.Add(new CashflowTableRowDto
            {
                CategoryId = cat.CategoryId,
                CategoryName = cat.Name,
                ColorHex = cat.ColorHex,
                SortOrder = cat.SortOrder,
                LastActivityAt = catLastActivity,
                NetSpend = netSpend,
                SpendByAccount = spendByAcct,
                Subcategories = subcats,
                Transactions = catTxs
            });
        }

        // Include any uncategorized Expense/Refund rows
        var uncategorizedTxs = spendTransactions.Where(t => !t.CategoryId.HasValue).ToList();
        if (uncategorizedTxs.Count > 0)
        {
            decimal netUncat = Math.Round(uncategorizedTxs.Sum(t => t.Amount), 2);
            if (netUncat > 0) totalPositiveCategorySpend += netUncat;

            var spendByAcct = accounts.ToDictionary(
                a => a.AccountId,
                a => Math.Round(uncategorizedTxs.Where(t => t.AccountId == a.AccountId).Sum(t => t.Amount), 2));

            string uncatLastActivity = uncategorizedTxs
                .Select(t => t.UpdatedAt ?? t.CreatedAt ?? t.TransactionDate)
                .DefaultIfEmpty("")
                .Max() ?? "";

            tableRows.Add(new CashflowTableRowDto
            {
                CategoryId = null,
                CategoryName = "Uncategorized",
                ColorHex = "#F59E0B",
                SortOrder = int.MaxValue,
                LastActivityAt = uncatLastActivity,
                NetSpend = netUncat,
                SpendByAccount = spendByAcct,
                Subcategories = [],
                Transactions = uncategorizedTxs
            });
        }

        // Populate percentages & sort according to normalizedSort
        foreach (var row in tableRows)
        {
            row.PctOfTotalSpend = totalNetSpend > 0 ? Math.Round((row.NetSpend / totalNetSpend) * 100m, 1) : 0m;
            row.PctOfIncome = totalIncome > 0 ? Math.Round((row.NetSpend / totalIncome) * 100m, 1) : 0m;
        }

        tableRows = SortCategoryRows(tableRows, normalizedSort);

        decimal unspent = totalIncome > totalPositiveCategorySpend
            ? Math.Round(totalIncome - totalPositiveCategorySpend, 2)
            : 0m;
        decimal deficit = totalPositiveCategorySpend > totalIncome
            ? Math.Round(totalPositiveCategorySpend - totalIncome, 2)
            : 0m;

        // 5. Build D3 Sankey Graph using the sorted tableRows
        var sankey = BuildSankeyGraph(
            totalIncome,
            totalPositiveCategorySpend,
            unspent,
            deficit,
            tableRows,
            includeSubcategories && hasAnySubcategories);

        return new CashflowResponse
        {
            StartDate = isoStart,
            EndDate = isoEnd,
            WindowType = resolvedWindowType,
            CategorySort = normalizedSort,
            IncludeSubcategories = includeSubcategories,
            HasAnySubcategories = hasAnySubcategories,
            TotalIncome = totalIncome,
            PrimaryIncomeSourceLabel = primarySourceLabel,
            IncomeBreakdown = monthBreakdowns,
            TotalNetSpend = totalNetSpend,
            UnspentCashflow = unspent,
            OverIncomeDeficit = deficit,
            CreditCardPayoffsTotal = Math.Round(creditCardPayoffsTotal, 2),
            UncategorizedCount = uncategorizedTxs.Count,
            UncategorizedTotalAmount = Math.Round(uncategorizedTxs.Sum(t => t.Amount), 2),
            UncategorizedTransactions = uncategorizedTxs,
            Sankey = sankey,
            Accounts = accounts,
            TableRows = tableRows
        };
    }

    public static string NormalizeCategorySort(string? categorySort)
    {
        return (categorySort ?? "spend_desc").Trim().ToLowerInvariant() switch
        {
            "spend_asc" => "spend_asc",
            "alpha_asc" => "alpha_asc",
            "alpha_desc" => "alpha_desc",
            "recent" => "recent",
            "custom_order" => "custom_order",
            _ => "spend_desc"
        };
    }

    public static List<CashflowTableRowDto> SortCategoryRows(IEnumerable<CashflowTableRowDto> rows, string normalizedSort)
    {
        return normalizedSort switch
        {
            "spend_asc" => rows
                .OrderBy(r => r.NetSpend)
                .ThenBy(r => r.CategoryName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "alpha_asc" => rows
                .OrderBy(r => r.CategoryId is null ? 1 : 0)
                .ThenBy(r => r.CategoryName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "alpha_desc" => rows
                .OrderBy(r => r.CategoryId is null ? 1 : 0)
                .ThenByDescending(r => r.CategoryName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "recent" => rows
                .OrderByDescending(r => r.LastActivityAt, StringComparer.Ordinal)
                .ThenByDescending(r => r.NetSpend)
                .ToList(),
            "custom_order" => rows
                .OrderBy(r => r.CategoryId is null ? 1 : 0)
                .ThenBy(r => r.SortOrder)
                .ThenBy(r => r.CategoryName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => rows
                .OrderByDescending(r => r.NetSpend)
                .ThenBy(r => r.CategoryName, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static List<SubcategoryBreakdownDto> SortSubcategories(IEnumerable<SubcategoryBreakdownDto> subcats, string normalizedSort)
    {
        return normalizedSort switch
        {
            "spend_asc" => subcats
                .OrderBy(s => s.NetSpend)
                .ThenBy(s => s.SubcategoryName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "alpha_asc" => subcats
                .OrderBy(s => s.SubcategoryName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "alpha_desc" => subcats
                .OrderByDescending(s => s.SubcategoryName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "recent" => subcats
                .OrderByDescending(s => s.LastActivityAt, StringComparer.Ordinal)
                .ThenByDescending(s => s.NetSpend)
                .ToList(),
            _ => subcats
                .OrderByDescending(s => s.NetSpend)
                .ThenBy(s => s.SubcategoryName, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public static async Task<List<MonthlyIncomeBreakdownDto>> ResolveTieredIncomeBreakdownAsync(
        System.Data.Common.DbConnection conn,
        DateOnly startDate,
        DateOnly endDate,
        bool isFullMonthWindow,
        decimal persistentMonthlyIncome,
        string currentMonthMode,
        Dictionary<string, decimal> overrides)
    {
        var results = new List<MonthlyIncomeBreakdownDto>();
        string currentYearMonth = DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        DateOnly cursor = new(startDate.Year, startDate.Month, 1);
        DateOnly lastMonth = new(endDate.Year, endDate.Month, 1);

        while (cursor <= lastMonth)
        {
            string ym = cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            int daysInMonth = DateTime.DaysInMonth(cursor.Year, cursor.Month);

            DateOnly monthStart = cursor;
            DateOnly monthEnd = new(cursor.Year, cursor.Month, daysInMonth);

            DateOnly effectiveStart = startDate > monthStart ? startDate : monthStart;
            DateOnly effectiveEnd = endDate < monthEnd ? endDate : monthEnd;
            int activeDays = effectiveEnd.DayNumber - effectiveStart.DayNumber + 1;

            var depositStats = await conn.QuerySingleAsync<(decimal SumAmount, int DepositCount)>(
                """
                SELECT COALESCE(SUM(Amount), 0) AS SumAmount, COUNT(*) AS DepositCount
                FROM Transactions
                WHERE TransactionType = 'IncomeDeposit'
                  AND substr(TransactionDate, 1, 7) = @YearMonth;
                """,
                new { YearMonth = ym });

            decimal fullMonthIncome;
            string sourceType;

            bool useActualDeposits = depositStats.DepositCount > 0
                && !(ym == currentYearMonth && currentMonthMode.Equals("ForceFullExpected", StringComparison.OrdinalIgnoreCase));

            if (useActualDeposits)
            {
                fullMonthIncome = depositStats.SumAmount;
                sourceType = "ActualGoogleDeposits";
            }
            else if (overrides.TryGetValue(ym, out decimal overrideIncome))
            {
                fullMonthIncome = overrideIncome;
                sourceType = "MonthlyOverride";
            }
            else
            {
                fullMonthIncome = persistentMonthlyIncome;
                sourceType = "PersistentFallback";
            }

            decimal proRatedIncome = (isFullMonthWindow || activeDays == daysInMonth)
                ? fullMonthIncome
                : Math.Round(fullMonthIncome * ((decimal)activeDays / daysInMonth), 2);

            results.Add(new MonthlyIncomeBreakdownDto
            {
                YearMonth = ym,
                ResolvedFullMonthIncome = Math.Round(fullMonthIncome, 2),
                ProRatedWindowIncome = proRatedIncome,
                ActiveDays = activeDays,
                TotalDaysInMonth = daysInMonth,
                SourceType = sourceType,
                DepositCount = depositStats.DepositCount
            });

            cursor = cursor.AddMonths(1);
        }

        return results;
    }

    private static SankeyGraphDto BuildSankeyGraph(
        decimal totalIncome,
        decimal totalPositiveSpend,
        decimal unspent,
        decimal deficit,
        List<CashflowTableRowDto> categoryRows,
        bool expandSubcategories)
    {
        var nodes = new List<SankeyNodeDto>();
        var links = new List<SankeyLinkDto>();

        int AddNode(string id, string name, string color, decimal amount, string nodeType)
        {
            int idx = nodes.Count;
            nodes.Add(new SankeyNodeDto
            {
                Index = idx,
                Id = id,
                Name = name,
                ColorHex = color,
                Amount = Math.Round(amount, 2),
                NodeType = nodeType
            });
            return idx;
        }

        int incomeNodeIdx = AddNode("income", "Resolved Income", "#10B981", totalIncome, "Income");
        int spendingNodeIdx = AddNode("total_spend", "Total Spending", "#3B82F6", totalPositiveSpend, "Summary");

        if (totalPositiveSpend > 0 && totalIncome > 0)
        {
            decimal incomeToSpend = Math.Min(totalIncome, totalPositiveSpend);
            links.Add(new SankeyLinkDto
            {
                Source = incomeNodeIdx,
                Target = spendingNodeIdx,
                Value = Math.Round(incomeToSpend, 2),
                ColorHex = "#3B82F6"
            });
        }

        if (unspent > 0)
        {
            int unspentNodeIdx = AddNode("unspent", "Unspent / Remaining", "#059669", unspent, "Summary");
            links.Add(new SankeyLinkDto
            {
                Source = incomeNodeIdx,
                Target = unspentNodeIdx,
                Value = unspent,
                ColorHex = "#10B981"
            });
        }

        if (deficit > 0)
        {
            int deficitNodeIdx = AddNode("deficit", "Over-Income / Deficit", "#DC2626", deficit, "Deficit");
            links.Add(new SankeyLinkDto
            {
                Source = deficitNodeIdx,
                Target = spendingNodeIdx,
                Value = deficit,
                ColorHex = "#DC2626"
            });
        }

        foreach (var catRow in categoryRows.Where(r => r.NetSpend > 0))
        {
            string catIdStr = $"cat_{catRow.CategoryId?.ToString() ?? "uncat"}";
            int catNodeIdx = AddNode(catIdStr, catRow.CategoryName, catRow.ColorHex, catRow.NetSpend, "Category");

            links.Add(new SankeyLinkDto
            {
                Source = spendingNodeIdx,
                Target = catNodeIdx,
                Value = catRow.NetSpend,
                ColorHex = catRow.ColorHex
            });

            // Only expand subcategories when enabled AND this category actually has populated subcategories
            if (expandSubcategories && catRow.Subcategories.Count > 0)
            {
                decimal subcatSum = 0m;
                foreach (var sub in catRow.Subcategories)
                {
                    decimal val = Math.Min(sub.NetSpend, catRow.NetSpend - subcatSum);
                    if (val <= 0) continue;

                    subcatSum += val;
                    int subIdx = AddNode(
                        $"{catIdStr}_sub_{sub.SubcategoryName}",
                        sub.SubcategoryName,
                        catRow.ColorHex,
                        val,
                        "Subcategory");

                    links.Add(new SankeyLinkDto
                    {
                        Source = catNodeIdx,
                        Target = subIdx,
                        Value = val,
                        ColorHex = catRow.ColorHex
                    });
                }

                decimal generalRemainder = Math.Round(catRow.NetSpend - subcatSum, 2);
                if (generalRemainder > 0)
                {
                    int genIdx = AddNode(
                        $"{catIdStr}_sub_general",
                        $"{catRow.CategoryName} (General)",
                        "#94A3B8",
                        generalRemainder,
                        "Subcategory");

                    links.Add(new SankeyLinkDto
                    {
                        Source = catNodeIdx,
                        Target = genIdx,
                        Value = generalRemainder,
                        ColorHex = "#94A3B8"
                    });
                }
            }
        }

        return new SankeyGraphDto { Nodes = nodes, Links = links };
    }

    private static (DateOnly Start, DateOnly End, string WindowType, bool IsFullMonthWindow) ResolveDateWindow(
        string? windowType,
        string? startDateStr,
        string? endDateStr,
        int? monthsCount)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        string wt = (windowType ?? "mtd").Trim().ToLowerInvariant();

        switch (wt)
        {
            case "mtd":
            {
                DateOnly start = new(today.Year, today.Month, 1);
                DateOnly end = new(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
                return (start, end, "mtd", true);
            }
            case "prior_month":
            {
                DateOnly prev = today.AddMonths(-1);
                DateOnly start = new(prev.Year, prev.Month, 1);
                DateOnly end = new(prev.Year, prev.Month, DateTime.DaysInMonth(prev.Year, prev.Month));
                return (start, end, "prior_month", true);
            }
            case "last_n_months":
            {
                int n = Math.Clamp(monthsCount ?? 3, 1, 60);
                DateOnly firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-(n - 1));
                DateOnly end = new(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
                return (firstMonth, end, $"last_{n}_months", true);
            }
            case "ytd":
            {
                DateOnly start = new(today.Year, 1, 1);
                DateOnly end = new(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
                return (start, end, "ytd", true);
            }
            case "prior_year":
            {
                int yr = today.Year - 1;
                return (new DateOnly(yr, 1, 1), new DateOnly(yr, 12, 31), "prior_year", true);
            }
            case "custom":
            default:
            {
                if (DateOnly.TryParse(startDateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var s)
                    && DateOnly.TryParse(endDateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var e)
                    && s <= e)
                {
                    bool fullMonths = s.Day == 1 && e.Day == DateTime.DaysInMonth(e.Year, e.Month);
                    return (s, e, "custom", fullMonths);
                }

                DateOnly fallbackStart = new(today.Year, today.Month, 1);
                DateOnly fallbackEnd = new(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
                return (fallbackStart, fallbackEnd, "mtd", true);
            }
        }
    }

    private static string DeterminePrimaryIncomeLabel(List<MonthlyIncomeBreakdownDto> breakdowns)
    {
        if (breakdowns.All(b => b.SourceType == "ActualGoogleDeposits"))
            return "Actual Google Direct Deposits";
        if (breakdowns.Any(b => b.SourceType == "ActualGoogleDeposits"))
            return "Blended (Google Direct Deposits + Persistent Fallback)";
        return "Persistent Monthly Income Fallback";
    }
}
