using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using HomeBudget.Web.Models;

namespace HomeBudget.Web.Services;

public sealed partial class CsvParserService
{
    public List<StagedCandidateRow> ParseCsv(
        Stream csvStream,
        string fileName,
        Account account,
        string googlePayrollPattern)
    {
        using var reader = new StreamReader(csvStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string content = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        return account.Institution.Equals("WellsFargo", StringComparison.OrdinalIgnoreCase)
            ? ParseWellsFargoCsv(content, fileName, account.AccountId, googlePayrollPattern)
            : ParseChaseCsv(content, fileName, account.AccountId);
    }

    private static List<StagedCandidateRow> ParseChaseCsv(string csvContent, string fileName, int accountId)
    {
        var results = new List<StagedCandidateRow>();
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim
        };

        using var stringReader = new StringReader(csvContent);
        using var csv = new CsvReader(stringReader, config);

        if (!csv.Read() || !csv.ReadHeader())
        {
            return results;
        }

        while (csv.Read())
        {
            string rawTxDate = csv.GetField("Transaction Date") ?? "";
            if (string.IsNullOrWhiteSpace(rawTxDate)) continue;

            string rawPostDate = csv.GetField("Post Date") ?? "";
            string rawDesc = csv.GetField("Description") ?? "";
            string nativeCat = csv.GetField("Category") ?? "";
            string rawType = csv.GetField("Type") ?? "";
            string rawAmountStr = csv.GetField("Amount") ?? "0";

            if (!decimal.TryParse(rawAmountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal rawAmount))
            {
                continue;
            }

            string isoTxDate = NormalizeDate(rawTxDate);
            string? isoPostDate = string.IsNullOrWhiteSpace(rawPostDate) ? null : NormalizeDate(rawPostDate);

            string txType;
            decimal normalizedAmount;

            bool isCardPayoff = rawType.Equals("Payment", StringComparison.OrdinalIgnoreCase)
                || rawDesc.Contains("PAYMENT THANK YOU", StringComparison.OrdinalIgnoreCase)
                || rawDesc.Contains("AUTOMATIC PAYMENT", StringComparison.OrdinalIgnoreCase);

            if (isCardPayoff)
            {
                txType = "CreditCardPayoff";
                normalizedAmount = Math.Abs(rawAmount);
            }
            else if (rawAmount < 0)
            {
                txType = "Expense";
                normalizedAmount = Math.Abs(rawAmount);
            }
            else
            {
                txType = "Refund";
                normalizedAmount = -Math.Abs(rawAmount);
            }

            bool isP2P = ContainsP2PKeyword(rawDesc);

            results.Add(new StagedCandidateRow
            {
                AccountId = accountId,
                SourceFileName = fileName,
                TransactionDate = isoTxDate,
                PostDate = isoPostDate,
                RawDescription = rawDesc,
                CleanMerchantName = CleanMerchantDescription(rawDesc),
                NativeCsvCategory = string.IsNullOrWhiteSpace(nativeCat) ? null : nativeCat,
                Amount = normalizedAmount,
                TransactionType = txType,
                RequiresManualReview = isP2P,
                DedupHash = ComputeDedupHash(accountId, isoTxDate, normalizedAmount, rawDesc)
            });
        }

        return results;
    }

    private static List<StagedCandidateRow> ParseWellsFargoCsv(
        string csvContent,
        string fileName,
        int accountId,
        string googlePayrollPattern)
    {
        var results = new List<StagedCandidateRow>();
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim
        };

        using var stringReader = new StringReader(csvContent);
        using var csv = new CsvReader(stringReader, config);

        while (csv.Read())
        {
            string rawDate = csv.GetField(0) ?? "";
            string rawAmountStr = csv.GetField(1) ?? "";
            string rawDesc = csv.TryGetField<string>(4, out var col4) && !string.IsNullOrWhiteSpace(col4)
                ? col4
                : (csv.GetField(2) ?? "");

            if (!DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                continue;
            }

            if (!decimal.TryParse(rawAmountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal rawAmount))
            {
                continue;
            }

            string isoTxDate = NormalizeDate(rawDate);
            string txType;
            decimal normalizedAmount;
            bool requiresManualReview = false;

            bool isCardPayoff = rawDesc.Contains("CHASE CREDIT CRD", StringComparison.OrdinalIgnoreCase)
                || rawDesc.Contains("AUTOPAY", StringComparison.OrdinalIgnoreCase) && rawDesc.Contains("CRD", StringComparison.OrdinalIgnoreCase);

            bool isGooglePayroll = rawAmount > 0
                && !string.IsNullOrWhiteSpace(googlePayrollPattern)
                && rawDesc.Contains(googlePayrollPattern, StringComparison.OrdinalIgnoreCase);

            if (isCardPayoff)
            {
                txType = "CreditCardPayoff";
                normalizedAmount = Math.Abs(rawAmount);
            }
            else if (isGooglePayroll)
            {
                txType = "IncomeDeposit";
                normalizedAmount = Math.Abs(rawAmount);
            }
            else if (rawAmount < 0)
            {
                txType = "Expense";
                normalizedAmount = Math.Abs(rawAmount);
                requiresManualReview = ContainsP2PKeyword(rawDesc);
            }
            else
            {
                txType = "Refund";
                normalizedAmount = -Math.Abs(rawAmount);
                requiresManualReview = true;
            }

            results.Add(new StagedCandidateRow
            {
                AccountId = accountId,
                SourceFileName = fileName,
                TransactionDate = isoTxDate,
                PostDate = null,
                RawDescription = rawDesc,
                CleanMerchantName = CleanMerchantDescription(rawDesc),
                Amount = normalizedAmount,
                TransactionType = txType,
                RequiresManualReview = requiresManualReview,
                DedupHash = ComputeDedupHash(accountId, isoTxDate, normalizedAmount, rawDesc)
            });
        }

        return results;
    }

    public static string ComputeDedupHash(int accountId, string isoTransactionDate, decimal normalizedAmount, string rawDescription)
    {
        string normalizedDesc = MultiSpaceRegex().Replace(rawDescription.Trim().ToUpperInvariant(), " ");
        string payload = $"{accountId}|{isoTransactionDate}|{normalizedAmount:F2}|{normalizedDesc}";
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes);
    }

    private static string NormalizeDate(string rawDate)
    {
        if (DateTime.TryParse(rawDate.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
        {
            return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return rawDate.Trim();
    }

    private static bool ContainsP2PKeyword(string desc) =>
        desc.Contains("ZELLE", StringComparison.OrdinalIgnoreCase) ||
        desc.Contains("VENMO", StringComparison.OrdinalIgnoreCase) ||
        desc.Contains("PAYPAL", StringComparison.OrdinalIgnoreCase);

    private static string CleanMerchantDescription(string rawDesc)
    {
        string cleaned = MultiSpaceRegex().Replace(rawDesc.Trim(), " ");
        return cleaned.Length > 60 ? cleaned[..60].Trim() : cleaned;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();
}
