using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using HomeBudget.Web.Models;

namespace HomeBudget.Web.Services;

public sealed class ParsedCsvRow
{
    public string TransactionDate { get; set; } = "";
    public string? PostDate { get; set; }
    public string RawDescription { get; set; } = "";
    public string CleanMerchantName { get; set; } = "";
    public decimal Amount { get; set; }
    public string TransactionType { get; set; } = "Expense";
    public string BaseDedupHash { get; set; } = "";
}

public sealed class CsvParserService
{
    public List<ParsedCsvRow> ParseCsvStream(
        Stream stream,
        Account account,
        string googlePayrollPattern)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string content = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(content)) return [];

        bool isWellsFargo = account.Institution.Equals("WellsFargo", StringComparison.OrdinalIgnoreCase)
            || (!content.StartsWith("Transaction Date", StringComparison.OrdinalIgnoreCase)
                && content.TrimStart().StartsWith('"'));

        return isWellsFargo
            ? ParseWellsFargoCsv(content, account.AccountId, googlePayrollPattern)
            : ParseChaseCsv(content, account.AccountId, googlePayrollPattern);
    }

    private static List<ParsedCsvRow> ParseChaseCsv(string content, int accountId, string googlePayrollPattern)
    {
        var results = new List<ParsedCsvRow>();
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim
        };

        using var sr = new StringReader(content);
        using var csv = new CsvReader(sr, config);

        if (!csv.Read() || !csv.ReadHeader()) return results;

        while (csv.Read())
        {
            string? txDateRaw = csv.GetField("Transaction Date") ?? csv.GetField(0);
            string? postDateRaw = csv.GetField("Post Date") ?? csv.GetField(1);
            string? descRaw = csv.GetField("Description") ?? csv.GetField(2);
            string? typeRaw = csv.GetField("Type") ?? csv.GetField(4);
            string? amtRaw = csv.GetField("Amount") ?? csv.GetField(5);

            if (string.IsNullOrWhiteSpace(txDateRaw) || string.IsNullOrWhiteSpace(amtRaw) || string.IsNullOrWhiteSpace(descRaw))
                continue;

            if (!TryParseDate(txDateRaw, out string isoTxDate)) continue;
            TryParseDate(postDateRaw, out string isoPostDate);

            if (!decimal.TryParse(amtRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal csvAmt))
                continue;

            string rawDesc = descRaw.Trim();
            string cleanMerchant = CleanMerchant(rawDesc);

            // In Chase Credit Card CSV: negative = Sale/Expense, positive = Payment or Return/Refund
            string txType;
            decimal normalizedAmt;

            bool isPayment = (typeRaw?.Equals("Payment", StringComparison.OrdinalIgnoreCase) == true)
                || rawDesc.Contains("PAYMENT THANK YOU", StringComparison.OrdinalIgnoreCase)
                || rawDesc.Contains("AUTOMATIC PAYMENT", StringComparison.OrdinalIgnoreCase);

            if (isPayment)
            {
                txType = "CreditCardPayoff";
                normalizedAmt = Math.Abs(csvAmt);
            }
            else if (csvAmt < 0m)
            {
                txType = "Expense";
                normalizedAmt = Math.Abs(csvAmt);
            }
            else
            {
                txType = "Refund";
                normalizedAmt = -Math.Abs(csvAmt);
            }

            results.Add(new ParsedCsvRow
            {
                TransactionDate = isoTxDate,
                PostDate = string.IsNullOrEmpty(isoPostDate) ? null : isoPostDate,
                RawDescription = rawDesc,
                CleanMerchantName = cleanMerchant,
                Amount = Math.Round(normalizedAmt, 2),
                TransactionType = txType,
                BaseDedupHash = ComputeBaseHash(accountId, isoTxDate, rawDesc, normalizedAmt)
            });
        }

        return results;
    }

    private static List<ParsedCsvRow> ParseWellsFargoCsv(string content, int accountId, string googlePayrollPattern)
    {
        var results = new List<ParsedCsvRow>();
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim
        };

        using var sr = new StringReader(content);
        using var csv = new CsvReader(sr, config);

        while (csv.Read())
        {
            string? dateRaw = csv.GetField(0);
            string? amtRaw = csv.GetField(1);
            string? descRaw = csv.GetField(4) ?? csv.GetField(2);

            if (string.IsNullOrWhiteSpace(dateRaw) || string.IsNullOrWhiteSpace(amtRaw) || string.IsNullOrWhiteSpace(descRaw))
                continue;

            if (!TryParseDate(dateRaw, out string isoDate)) continue;
            if (!decimal.TryParse(amtRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal csvAmt))
                continue;

            string rawDesc = descRaw.Trim();
            string cleanMerchant = CleanMerchant(rawDesc);

            string txType;
            decimal normalizedAmt;

            bool isCcPayoff = rawDesc.Contains("CHASE CREDIT CRD", StringComparison.OrdinalIgnoreCase)
                || rawDesc.Contains("CREDIT CARD", StringComparison.OrdinalIgnoreCase)
                || rawDesc.Contains("EPAY", StringComparison.OrdinalIgnoreCase) && rawDesc.Contains("CHASE", StringComparison.OrdinalIgnoreCase);

            bool isGooglePayroll = csvAmt > 0m && !string.IsNullOrWhiteSpace(googlePayrollPattern)
                && rawDesc.Contains(googlePayrollPattern.Trim(), StringComparison.OrdinalIgnoreCase);

            if (isCcPayoff)
            {
                txType = "CreditCardPayoff";
                normalizedAmt = Math.Abs(csvAmt);
            }
            else if (isGooglePayroll || csvAmt > 0m)
            {
                txType = "IncomeDeposit";
                normalizedAmt = Math.Abs(csvAmt);
            }
            else
            {
                txType = "Expense";
                normalizedAmt = Math.Abs(csvAmt);
            }

            results.Add(new ParsedCsvRow
            {
                TransactionDate = isoDate,
                PostDate = isoDate,
                RawDescription = rawDesc,
                CleanMerchantName = cleanMerchant,
                Amount = Math.Round(normalizedAmt, 2),
                TransactionType = txType,
                BaseDedupHash = ComputeBaseHash(accountId, isoDate, rawDesc, normalizedAmt)
            });
        }

        return results;
    }

    private static bool TryParseDate(string? input, out string isoDate)
    {
        isoDate = "";
        if (string.IsNullOrWhiteSpace(input)) return false;

        string[] formats = ["MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd", "MM-dd-yyyy"];
        if (DateOnly.TryParseExact(input.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            || DateOnly.TryParse(input.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
        {
            isoDate = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }

    public static string CleanMerchant(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown Merchant";
        string cleaned = Regex.Replace(raw.ToUpperInvariant(), @"[#*]\S+", "");
        cleaned = Regex.Replace(cleaned, @"\d{4,}", "");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? raw.Trim() : cleaned;
    }

    public static string ComputeBaseHash(int accountId, string isoDate, string rawDesc, decimal amount)
    {
        string canonical = $"{accountId}|{isoDate}|{rawDesc.Trim().ToUpperInvariant()}|{amount.ToString("F2", CultureInfo.InvariantCulture)}";
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes)[..24];
    }
}
