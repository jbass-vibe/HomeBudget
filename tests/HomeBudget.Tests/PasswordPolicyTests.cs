using HomeBudget.Web.Services;
using Xunit;

namespace HomeBudget.Tests;

public sealed class PasswordPolicyTests
{
    [Fact]
    public void Validate_RejectsEmptyOrShortPassphrase()
    {
        Assert.False(PasswordPolicyValidator.Validate(null).IsValid);
        Assert.False(PasswordPolicyValidator.Validate("").IsValid);
        Assert.False(PasswordPolicyValidator.Validate("short123456").IsValid); // 11 chars
    }

    [Fact]
    public void Validate_Accepts12And128CharacterPassphrases()
    {
        Assert.True(PasswordPolicyValidator.Validate("CorrectHorse").IsValid); // 12 chars
        Assert.True(PasswordPolicyValidator.Validate(new string('A', 64) + new string('b', 64)).IsValid); // 128 chars
    }

    [Fact]
    public void Validate_RejectsOver128Characters()
    {
        string tooLong = new string('A', 65) + new string('b', 64); // 129 chars
        Assert.False(PasswordPolicyValidator.Validate(tooLong).IsValid);
    }

    [Fact]
    public void Validate_PreservesWhitespaceAndUnicodeWithoutTrimming()
    {
        // Leading/trailing spaces count toward length and are valid if not all identical
        Assert.True(PasswordPolicyValidator.Validate("  my passphrase  ").IsValid);
        Assert.True(PasswordPolicyValidator.Validate("café-☕-passphrase").IsValid);
    }

    [Fact]
    public void Validate_RejectsSingleRepeatedCharacterAndCommonWeakPasswords()
    {
        Assert.False(PasswordPolicyValidator.Validate("aaaaaaaaaaaa").IsValid);
        Assert.False(PasswordPolicyValidator.Validate("            ").IsValid);
        Assert.False(PasswordPolicyValidator.Validate("password1234").IsValid);
        Assert.False(PasswordPolicyValidator.Validate("homebudget123").IsValid);
    }

    [Fact]
    public void Validate_RejectsMatchingUsernameOrDisplayName()
    {
        Assert.False(PasswordPolicyValidator.Validate("administrator123", "administrator123", "Admin").IsValid);
        Assert.False(PasswordPolicyValidator.Validate("HouseholdUser", "user", "HouseholdUser").IsValid);
    }
}
