using Dapper;
using HomeBudget.Web.Data;
using HomeBudget.Web.Models;
using HomeBudget.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeBudget.Tests;

public sealed class AuthServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SecurityAuditService _auditService;
    private readonly AuthService _authService;

    public AuthServiceTests()
    {
        SqlMapper.AddTypeHandler(new SqliteDecimalHandler());
        SqlMapper.AddTypeHandler(new SqliteBoolHandler());

        _tempDir = Path.Combine(Path.GetTempPath(), $"HomeBudgetAuthTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HomeBudget:DatabasePath"] = Path.Combine(_tempDir, "budget.db"),
                ["HomeBudget:BackupDirectoryPath"] = Path.Combine(_tempDir, "Backups")
            })
            .Build();

        _connectionFactory = new SqliteConnectionFactory(config);
        _auditService = new SecurityAuditService(_connectionFactory, NullLogger<SecurityAuditService>.Instance);
        _authService = new AuthService(_connectionFactory, _auditService, NullLogger<AuthService>.Instance);
    }

    private async Task InitializeDbAsync()
    {
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """);
        await DatabaseInitializer.ApplyIdempotentSchemaMigrationsAsync(conn);
        await _authService.InitializeStartupSecurityAsync();
    }

    [Fact]
    public async Task InitialSetup_CreatesBootstrapToken_RejectsInvalidToken_AndCreatesFixedAccounts()
    {
        await InitializeDbAsync();

        Assert.True(await _authService.IsSetupRequiredAsync());
        Assert.True(File.Exists(_authService.SetupTokenFilePath));
        string token = (await File.ReadAllTextAsync(_authService.SetupTokenFilePath)).Trim();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var badTokenRes = await _authService.CompleteInitialSetupAsync(
            new SetupRequest("wrong-token", "Admin", "CorrectAdminPass1!", "User", "CorrectUserPass1!"),
            "127.0.0.1");
        Assert.False(badTokenRes.Success);
        Assert.Equal(403, badTokenRes.StatusCode);

        var weakPassRes = await _authService.CompleteInitialSetupAsync(
            new SetupRequest(token, "Admin", "short", "User", "CorrectUserPass1!"),
            "127.0.0.1");
        Assert.False(weakPassRes.Success);
        Assert.Equal(400, weakPassRes.StatusCode);

        var okRes = await _authService.CompleteInitialSetupAsync(
            new SetupRequest(token, "Household Admin", "CorrectAdminPass1!", "Household User", "CorrectUserPass1!"),
            "127.0.0.1");
        Assert.True(okRes.Success);
        Assert.False(await _authService.IsSetupRequiredAsync());
        Assert.False(File.Exists(_authService.SetupTokenFilePath));

        var secondSetup = await _authService.CompleteInitialSetupAsync(
            new SetupRequest(token, "Admin", "CorrectAdminPass1!", "User", "CorrectUserPass1!"),
            "127.0.0.1");
        Assert.False(secondSetup.Success);
    }

    [Fact]
    public async Task Authenticate_LocksAfterFiveFailures_AndUnlockRestoresAccess()
    {
        await InitializeDbAsync();
        string token = (await File.ReadAllTextAsync(_authService.SetupTokenFilePath)).Trim();
        await _authService.CompleteInitialSetupAsync(
            new SetupRequest(token, "Admin", "CorrectAdminPass1!", "User", "CorrectUserPass1!"),
            "127.0.0.1");

        for (int i = 1; i <= 4; i++)
        {
            var fail = await _authService.AuthenticateAsync("user", "WrongPassphrase99", "192.168.1.25");
            Assert.False(fail.Success);
            Assert.Equal(401, fail.StatusCode);
        }

        var fifthFail = await _authService.AuthenticateAsync("user", "WrongPassphrase99", "192.168.1.25");
        Assert.False(fifthFail.Success);
        Assert.Equal(429, fifthFail.StatusCode);

        // Even the right password is throttled while locked
        var whileLocked = await _authService.AuthenticateAsync("user", "CorrectUserPass1!", "192.168.1.25");
        Assert.False(whileLocked.Success);
        Assert.Equal(429, whileLocked.StatusCode);

        var users = await _authService.GetUsersAsync();
        var userSummary = users.Single(u => u.Username == "user");
        Assert.True(userSummary.IsLockedOut);

        var adminSummary = users.Single(u => u.Username == "admin");
        var unlock = await _authService.UnlockUserAsync(adminSummary.UserId, "admin", userSummary.UserId, "192.168.1.10");
        Assert.True(unlock.Success);

        var afterUnlock = await _authService.AuthenticateAsync("user", "CorrectUserPass1!", "192.168.1.25");
        Assert.True(afterUnlock.Success);
        Assert.NotNull(afterUnlock.User);
        Assert.Equal("User", afterUnlock.User!.Role);
    }

    [Fact]
    public async Task ChangePassword_RotatesSecurityStamp_AndInvalidatesOldPrincipal()
    {
        await InitializeDbAsync();
        string token = (await File.ReadAllTextAsync(_authService.SetupTokenFilePath)).Trim();
        await _authService.CompleteInitialSetupAsync(
            new SetupRequest(token, "Admin", "CorrectAdminPass1!", "User", "CorrectUserPass1!"),
            "127.0.0.1");

        var login = await _authService.AuthenticateAsync("user", "CorrectUserPass1!", "192.168.1.25");
        Assert.True(login.Success);
        var oldPrincipal = AuthService.BuildClaimsPrincipal(login.User!, isPersistent: true);
        Assert.True(await _authService.ValidatePrincipalSecurityStampAsync(oldPrincipal));

        var changed = await _authService.ChangePasswordAsync(login.User!.UserId, "CorrectUserPass1!", "BrandNewUserPassphrase2!", "192.168.1.25");
        Assert.True(changed.Success);
        Assert.False(await _authService.ValidatePrincipalSecurityStampAsync(oldPrincipal));

        var newPrincipal = AuthService.BuildClaimsPrincipal(changed.UpdatedUser!, isPersistent: true);
        Assert.True(await _authService.ValidatePrincipalSecurityStampAsync(newPrincipal));
    }

    [Fact]
    public async Task SetUserActive_PreventsDisablingLastActiveAdmin()
    {
        await InitializeDbAsync();
        string token = (await File.ReadAllTextAsync(_authService.SetupTokenFilePath)).Trim();
        await _authService.CompleteInitialSetupAsync(
            new SetupRequest(token, "Admin", "CorrectAdminPass1!", "User", "CorrectUserPass1!"),
            "127.0.0.1");

        var users = await _authService.GetUsersAsync();
        var admin = users.Single(u => u.Username == "admin");
        var disableAdmin = await _authService.SetUserActiveAsync(admin.UserId, "admin", admin.UserId, false, "127.0.0.1");
        Assert.False(disableAdmin.Success);
    }

    [Fact]
    public async Task BreakGlassRecovery_ResetsAdminPassphraseAndDeletesTokenFile()
    {
        await InitializeDbAsync();
        string token = (await File.ReadAllTextAsync(_authService.SetupTokenFilePath)).Trim();
        await _authService.CompleteInitialSetupAsync(
            new SetupRequest(token, "Admin", "CorrectAdminPass1!", "User", "CorrectUserPass1!"),
            "127.0.0.1");

        await File.WriteAllTextAsync(_authService.RecoveryTokenFilePath, "RecoveredAdminPassphrase99!\n");
        await _authService.ProcessBreakGlassRecoveryAsync();

        Assert.False(File.Exists(_authService.RecoveryTokenFilePath));

        var oldLogin = await _authService.AuthenticateAsync("admin", "CorrectAdminPass1!", "127.0.0.1");
        Assert.False(oldLogin.Success);

        var recoveredLogin = await _authService.AuthenticateAsync("admin", "RecoveredAdminPassphrase99!", "127.0.0.1");
        Assert.True(recoveredLogin.Success);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
