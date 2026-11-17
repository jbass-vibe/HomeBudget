namespace HomeBudget.Web.Services;

public static class PasswordPolicyValidator
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    private static readonly HashSet<string> CommonWeakPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1234",
        "password12345",
        "123456789012",
        "1234567890123",
        "qwertyuiop12",
        "qwerty123456",
        "letmein12345",
        "welcome12345",
        "admin1234567",
        "passw0rd1234",
        "iloveyou1234",
        "homebudget12",
        "homebudget123",
        "abcdefghijkl",
        "000000000000",
        "111111111111"
    };

    public static (bool IsValid, string? Error) Validate(
        string? password,
        string? username = null,
        string? displayName = null)
    {
        if (string.IsNullOrEmpty(password))
        {
            return (false, $"Passphrase is required and must be between {MinLength} and {MaxLength} characters.");
        }

        // Preserve exact whitespace and Unicode; never trim or silently truncate.
        if (password.Length < MinLength)
        {
            return (false, $"Passphrase must be at least {MinLength} characters long.");
        }

        if (password.Length > MaxLength)
        {
            return (false, $"Passphrase cannot exceed {MaxLength} characters.");
        }

        if (password.Distinct().Count() <= 1)
        {
            return (false, "Passphrase cannot consist of a single repeated character.");
        }

        if (CommonWeakPasswords.Contains(password) || CommonWeakPasswords.Contains(password.Trim()))
        {
            return (false, "Passphrase is too common. Please choose a stronger passphrase.");
        }

        if (!string.IsNullOrWhiteSpace(username) &&
            password.Trim().Equals(username.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Passphrase cannot match the account username.");
        }

        if (!string.IsNullOrWhiteSpace(displayName) &&
            displayName.Trim().Length >= 3 &&
            password.Trim().Equals(displayName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Passphrase cannot match the account display name.");
        }

        return (true, null);
    }
}
