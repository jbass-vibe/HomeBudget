using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using HomeBudget.Web.Data;
using HomeBudget.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HomeBudget.Web.Services;

public sealed class AuthService
{
    public const int MaxFailedAttempts = 5;
    public const int LockoutMinutes = 15;
    public const int PersistentAbsoluteMaxDays = 180;
    public const int SessionAbsoluteMaxHours = 24;

    public const string ClaimUserId = "hb:uid";
    public const string ClaimSecurityStamp = "hb:stamp";
    public const string ClaimIssuedAtUtc = "hb:iat";
    public const string ClaimPersistent = "hb:persist";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SecurityAuditService _auditService;
    private readonly ILogger<AuthService> _logger;
    private readonly PasswordHasher<User> _hasher;
    private readonly string _dummyHash;
    private readonly SemaphoreSlim _setupMutex = new(1, 1);

    public string SetupTokenFilePath { get; }
    public string RecoveryTokenFilePath { get; }

    public AuthService(
        SqliteConnectionFactory connectionFactory,
        SecurityAuditService auditService,
        ILogger<AuthService> logger)
    {
        _connectionFactory = connectionFactory;
        _auditService = auditService;
        _logger = logger;

        _hasher = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3
        }));
        _dummyHash = _hasher.HashPassword(new User { Username = "dummy" }, "DummyTimingBalancePassphrase!2026");

        string dbDir = Path.GetDirectoryName(_connectionFactory.DatabasePath) ?? @"C:\ProgramData\HomeBudget";
        SetupTokenFilePath = Path.Combine(dbDir, "setup-token.txt");
        RecoveryTokenFilePath = Path.Combine(dbDir, "admin-recovery.token");
    }

    public async Task InitializeStartupSecurityAsync(CancellationToken ct = default)
    {
        await ProcessBreakGlassRecoveryAsync(ct);
        await EnsureBootstrapTokenIfNeededAsync(ct);
    }

    public async Task EnsureBootstrapTokenIfNeededAsync(CancellationToken ct = default)
    {
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
        bool setupRequired = await IsSetupRequiredCoreAsync(conn);

        if (!setupRequired)
        {
            TryDeleteFileQuietly(SetupTokenFilePath);
            return;
        }

        if (!File.Exists(SetupTokenFilePath) || string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(SetupTokenFilePath, ct)))
        {
            string? dir = Path.GetDirectoryName(SetupTokenFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            string token = Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            await File.WriteAllTextAsync(SetupTokenFilePath, token + Environment.NewLine, Encoding.UTF8, ct);
            _logger.LogWarning("First-run authentication setup is required. A one-time bootstrap token has been written to {TokenPath}.", SetupTokenFilePath);
        }
    }

    public async Task<bool> IsSetupRequiredAsync(CancellationToken ct = default)
    {
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
        return await IsSetupRequiredCoreAsync(conn);
    }

    private static async Task<bool> IsSetupRequiredCoreAsync(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction? tx = null)
    {
        int userCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users;", transaction: tx);
        if (userCount > 0)
        {
            return false;
        }

        string? authInit = await conn.ExecuteScalarAsync<string?>(
            "SELECT Value FROM Settings WHERE Key = 'AuthInitialized' LIMIT 1;",
            transaction: tx);

        return !string.Equals(authInit, "1", StringComparison.Ordinal);
    }

    public async Task<AuthStatusDto> GetAuthStatusAsync(ClaimsPrincipal? principal, CancellationToken ct = default)
    {
        bool setupRequired = await IsSetupRequiredAsync(ct);
        if (setupRequired || principal?.Identity?.IsAuthenticated != true)
        {
            return new AuthStatusDto(setupRequired, false, null);
        }

        CurrentUserDto? currentUser = ExtractCurrentUser(principal);
        if (currentUser is null)
        {
            return new AuthStatusDto(false, false, null);
        }

        return new AuthStatusDto(false, true, currentUser);
    }

    public static CurrentUserDto? ExtractCurrentUser(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;

        string? uidRaw = principal.FindFirstValue(ClaimUserId);
        if (!int.TryParse(uidRaw, out int userId)) return null;

        string username = principal.FindFirstValue(ClaimTypes.Name) ?? "";
        string displayName = principal.FindFirstValue("hb:display") ?? username;
        string role = principal.FindFirstValue(ClaimTypes.Role) ?? "User";
        return new CurrentUserDto(userId, username, displayName, role);
    }

    public async Task<(bool Success, int StatusCode, string? Error)> CompleteInitialSetupAsync(
        SetupRequest req,
        string? sourceIp,
        CancellationToken ct = default)
    {
        await _setupMutex.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(req.BootstrapToken) || !File.Exists(SetupTokenFilePath))
            {
                await _auditService.LogEventAsync("SETUP_FAILED", "Denied", sourceIp: sourceIp, details: "Missing bootstrap token", ct: ct);
                return (false, StatusCodes.Status403Forbidden, "Invalid or missing host bootstrap token.");
            }

            string expectedToken = (await File.ReadAllTextAsync(SetupTokenFilePath, ct)).Trim();
            string providedToken = req.BootstrapToken.Trim();

            byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
            byte[] providedBytes = Encoding.UTF8.GetBytes(providedToken);
            if (expectedBytes.Length == 0 || !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
            {
                await _auditService.LogEventAsync("SETUP_FAILED", "Denied", sourceIp: sourceIp, details: "Bootstrap token mismatch", ct: ct);
                return (false, StatusCodes.Status403Forbidden, "Invalid or missing host bootstrap token.");
            }

            string adminDisplay = string.IsNullOrWhiteSpace(req.AdminDisplayName) ? "Admin" : req.AdminDisplayName.Trim();
            string userDisplay = string.IsNullOrWhiteSpace(req.UserDisplayName) ? "User" : req.UserDisplayName.Trim();

            var (adminValid, adminErr) = PasswordPolicyValidator.Validate(req.AdminPassword, "admin", adminDisplay);
            if (!adminValid)
            {
                return (false, StatusCodes.Status400BadRequest, $"Admin passphrase error: {adminErr}");
            }

            var (userValid, userErr) = PasswordPolicyValidator.Validate(req.UserPassword, "user", userDisplay);
            if (!userValid)
            {
                return (false, StatusCodes.Status400BadRequest, $"User passphrase error: {userErr}");
            }

            await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            bool stillRequired = await IsSetupRequiredCoreAsync(conn, tx);
            if (!stillRequired)
            {
                await tx.RollbackAsync(ct);
                return (false, StatusCodes.Status409Conflict, "Initial setup has already been completed.");
            }

            var adminUser = new User
            {
                Username = "admin",
                DisplayName = adminDisplay,
                Role = "Admin",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                IsActive = true
            };
            adminUser.PasswordHash = _hasher.HashPassword(adminUser, req.AdminPassword);

            var standardUser = new User
            {
                Username = "user",
                DisplayName = userDisplay,
                Role = "User",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                IsActive = true
            };
            standardUser.PasswordHash = _hasher.HashPassword(standardUser, req.UserPassword);

            const string insertSql =
                """
                INSERT INTO Users (
                    Username, PasswordHash, DisplayName, Role, SecurityStamp,
                    FailedLoginAttempts, LockoutEndUtc, IsActive, CreatedAt, UpdatedAt
                ) VALUES (
                    @Username, @PasswordHash, @DisplayName, @Role, @SecurityStamp,
                    0, NULL, 1, datetime('now'), datetime('now')
                );
                """;

            await conn.ExecuteAsync(insertSql, adminUser, tx);
            await conn.ExecuteAsync(insertSql, standardUser, tx);
            await conn.ExecuteAsync(
                "INSERT INTO Settings (Key, Value, UpdatedAt) VALUES ('AuthInitialized', '1', datetime('now')) ON CONFLICT(Key) DO UPDATE SET Value = '1', UpdatedAt = datetime('now');",
                transaction: tx);

            await tx.CommitAsync(ct);

            TryDeleteFileQuietly(SetupTokenFilePath);
            await _auditService.LogEventAsync("SETUP_COMPLETED", "Success", actorUsername: "admin", targetUsername: "admin,user", sourceIp: sourceIp, details: "Initial fixed accounts created", ct: ct);

            return (true, StatusCodes.Status200OK, null);
        }
        finally
        {
            _setupMutex.Release();
        }
    }

    public async Task<(bool Success, int StatusCode, string? Error, User? User)> AuthenticateAsync(
        string username,
        string password,
        string? sourceIp,
        CancellationToken ct = default)
    {
        const string genericInvalidError = "Invalid username or password.";
        const string genericLockedError = "Sign-in temporarily throttled or credentials invalid. Please wait before retrying or contact the Administrator.";

        string normalizedUsername = (username ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(normalizedUsername) || string.IsNullOrEmpty(password))
        {
            return (false, StatusCodes.Status401Unauthorized, genericInvalidError, null);
        }

        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var user = await conn.QueryFirstOrDefaultAsync<User>(
            "SELECT * FROM Users WHERE Username = @Username COLLATE NOCASE LIMIT 1;",
            new { Username = normalizedUsername });

        if (user is null)
        {
            // Equalize timing to avoid username enumeration
            _hasher.VerifyHashedPassword(new User { Username = normalizedUsername }, _dummyHash, password);
            await _auditService.LogEventAsync("LOGIN_FAILURE", "Failure", targetUsername: normalizedUsername, sourceIp: sourceIp, details: "Unknown username", ct: ct);
            return (false, StatusCodes.Status401Unauthorized, genericInvalidError, null);
        }

        if (!user.IsActive)
        {
            _hasher.VerifyHashedPassword(user, _dummyHash, password);
            await _auditService.LogEventAsync("LOGIN_FAILURE", "Denied", actorUserId: user.UserId, actorUsername: user.Username, targetUsername: user.Username, sourceIp: sourceIp, details: "Inactive account", ct: ct);
            return (false, StatusCodes.Status401Unauthorized, genericInvalidError, null);
        }

        if (!string.IsNullOrWhiteSpace(user.LockoutEndUtc) &&
            DateTime.TryParse(user.LockoutEndUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime lockoutEnd) &&
            lockoutEnd > DateTime.UtcNow)
        {
            await _auditService.LogEventAsync("LOGIN_FAILURE", "Denied", actorUserId: user.UserId, actorUsername: user.Username, targetUsername: user.Username, sourceIp: sourceIp, details: "Account currently locked", ct: ct);
            return (false, StatusCodes.Status429TooManyRequests, genericLockedError, null);
        }

        var verifyResult = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verifyResult == PasswordVerificationResult.Failed)
        {
            // Atomic SQLite counter increment so concurrent failed logins never lose increments
            var updatedLockout = await conn.QuerySingleAsync<(int FailedLoginAttempts, string? LockoutEndUtc)>(
                """
                UPDATE Users
                SET FailedLoginAttempts = FailedLoginAttempts + 1,
                    LockoutEndUtc = CASE
                        WHEN FailedLoginAttempts + 1 >= @MaxAttempts THEN datetime('now', @LockoutModifier)
                        ELSE LockoutEndUtc
                    END,
                    UpdatedAt = datetime('now')
                WHERE UserId = @UserId
                RETURNING FailedLoginAttempts, LockoutEndUtc;
                """,
                new
                {
                    UserId = user.UserId,
                    MaxAttempts = MaxFailedAttempts,
                    LockoutModifier = $"+{LockoutMinutes} minutes"
                });

            await _auditService.LogEventAsync("LOGIN_FAILURE", "Failure", actorUserId: user.UserId, actorUsername: user.Username, targetUsername: user.Username, sourceIp: sourceIp, details: $"Failed attempt {updatedLockout.FailedLoginAttempts}", ct: ct);

            if (updatedLockout.FailedLoginAttempts >= MaxFailedAttempts)
            {
                await _auditService.LogEventAsync("ACCOUNT_LOCKED", "Failure", targetUsername: user.Username, sourceIp: sourceIp, details: $"Locked for {LockoutMinutes} minutes", ct: ct);
                return (false, StatusCodes.Status429TooManyRequests, genericLockedError, null);
            }

            return (false, StatusCodes.Status401Unauthorized, genericInvalidError, null);
        }

        string? upgradedHash = verifyResult == PasswordVerificationResult.SuccessRehashNeeded
            ? _hasher.HashPassword(user, password)
            : null;

        await conn.ExecuteAsync(
            """
            UPDATE Users
            SET FailedLoginAttempts = 0,
                LockoutEndUtc = NULL,
                LastLoginAt = datetime('now'),
                PasswordHash = COALESCE(@UpgradedHash, PasswordHash),
                UpdatedAt = datetime('now')
            WHERE UserId = @UserId;
            """,
            new { UserId = user.UserId, UpgradedHash = upgradedHash });

        if (upgradedHash is not null)
        {
            user.PasswordHash = upgradedHash;
        }

        await _auditService.LogEventAsync("LOGIN_SUCCESS", "Success", actorUserId: user.UserId, actorUsername: user.Username, targetUsername: user.Username, sourceIp: sourceIp, ct: ct);
        return (true, StatusCodes.Status200OK, null, user);
    }

    public static ClaimsPrincipal BuildClaimsPrincipal(User user, bool isPersistent)
    {
        var claims = new List<Claim>
        {
            new(ClaimUserId, user.UserId.ToString()),
            new(ClaimTypes.Name, user.Username),
            new("hb:display", user.DisplayName),
            new(ClaimTypes.Role, user.Role),
            new(ClaimSecurityStamp, user.SecurityStamp),
            new(ClaimIssuedAtUtc, DateTimeOffset.UtcNow.ToString("O")),
            new(ClaimPersistent, isPersistent ? "1" : "0")
        };

        var identity = new ClaimsIdentity(claims, Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    public async Task<bool> ValidatePrincipalSecurityStampAsync(ClaimsPrincipal? principal, CancellationToken ct = default)
    {
        if (principal?.Identity?.IsAuthenticated != true) return false;

        string? uidRaw = principal.FindFirstValue(ClaimUserId);
        string? claimStamp = principal.FindFirstValue(ClaimSecurityStamp);
        string? claimRole = principal.FindFirstValue(ClaimTypes.Role);
        string? issuedAtRaw = principal.FindFirstValue(ClaimIssuedAtUtc);
        bool isPersistent = principal.FindFirstValue(ClaimPersistent) == "1";

        if (!int.TryParse(uidRaw, out int userId) || string.IsNullOrEmpty(claimStamp))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(issuedAtRaw, out DateTimeOffset issuedAt))
        {
            return false;
        }

        TimeSpan age = DateTimeOffset.UtcNow - issuedAt;
        TimeSpan maxAllowed = isPersistent
            ? TimeSpan.FromDays(PersistentAbsoluteMaxDays)
            : TimeSpan.FromHours(SessionAbsoluteMaxHours);

        if (age < TimeSpan.FromMinutes(-5) || age > maxAllowed)
        {
            return false;
        }

        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var dbUser = await conn.QueryFirstOrDefaultAsync<User>(
            "SELECT UserId, Role, SecurityStamp, IsActive FROM Users WHERE UserId = @UserId LIMIT 1;",
            new { UserId = userId });

        if (dbUser is null || !dbUser.IsActive)
        {
            return false;
        }

        if (!string.Equals(dbUser.SecurityStamp, claimStamp, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(dbUser.Role, claimRole, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    public async Task<(bool Success, string? Error, User? UpdatedUser)> ChangePasswordAsync(
        int userId,
        string currentPassword,
        string newPassword,
        string? sourceIp,
        CancellationToken ct = default)
    {
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var user = await conn.QueryFirstOrDefaultAsync<User>("SELECT * FROM Users WHERE UserId = @UserId LIMIT 1;", new { UserId = userId });
        if (user is null || !user.IsActive)
        {
            return (false, "Account not found or inactive.", null);
        }

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword ?? "") == PasswordVerificationResult.Failed)
        {
            await _auditService.LogEventAsync("PASSWORD_CHANGE_FAILED", "Failure", actorUserId: user.UserId, actorUsername: user.Username, targetUsername: user.Username, sourceIp: sourceIp, details: "Current password mismatch", ct: ct);
            return (false, "Current passphrase is incorrect.", null);
        }

        var (valid, err) = PasswordPolicyValidator.Validate(newPassword, user.Username, user.DisplayName);
        if (!valid)
        {
            return (false, err, null);
        }

        string newHash = _hasher.HashPassword(user, newPassword);
        string newStamp = Guid.NewGuid().ToString("N");

        await conn.ExecuteAsync(
            "UPDATE Users SET PasswordHash = @Hash, SecurityStamp = @Stamp, UpdatedAt = datetime('now') WHERE UserId = @UserId;",
            new { Hash = newHash, Stamp = newStamp, UserId = user.UserId });

        user.PasswordHash = newHash;
        user.SecurityStamp = newStamp;

        await _auditService.LogEventAsync("PASSWORD_CHANGED", "Success", actorUserId: user.UserId, actorUsername: user.Username, targetUsername: user.Username, sourceIp: sourceIp, details: "Self-service change; prior sessions revoked", ct: ct);
        return (true, null, user);
    }

    public async Task<(bool Success, string? Error)> ResetUserPasswordAsync(
        int actorUserId,
        string actorUsername,
        int targetUserId,
        string newPassword,
        string? sourceIp,
        CancellationToken ct = default)
    {
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var target = await conn.QueryFirstOrDefaultAsync<User>("SELECT * FROM Users WHERE UserId = @UserId LIMIT 1;", new { UserId = targetUserId });
        if (target is null)
        {
            return (false, "Target account not found.");
        }

        var (valid, err) = PasswordPolicyValidator.Validate(newPassword, target.Username, target.DisplayName);
        if (!valid)
        {
            return (false, err);
        }

        string newHash = _hasher.HashPassword(target, newPassword);
        string newStamp = Guid.NewGuid().ToString("N");

        await conn.ExecuteAsync(
            """
            UPDATE Users
            SET PasswordHash = @Hash,
                SecurityStamp = @Stamp,
                FailedLoginAttempts = 0,
                LockoutEndUtc = NULL,
                UpdatedAt = datetime('now')
            WHERE UserId = @UserId;
            """,
            new { Hash = newHash, Stamp = newStamp, UserId = target.UserId });

        await _auditService.LogEventAsync("ADMIN_PASSWORD_RESET", "Success", actorUserId: actorUserId, actorUsername: actorUsername, targetUsername: target.Username, sourceIp: sourceIp, details: "Admin reset passphrase and rotated SecurityStamp", ct: ct);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UnlockUserAsync(
        int actorUserId,
        string actorUsername,
        int targetUserId,
        string? sourceIp,
        CancellationToken ct = default)
    {
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var target = await conn.QueryFirstOrDefaultAsync<User>("SELECT UserId, Username FROM Users WHERE UserId = @UserId LIMIT 1;", new { UserId = targetUserId });
        if (target is null)
        {
            return (false, "Target account not found.");
        }

        await conn.ExecuteAsync(
            "UPDATE Users SET FailedLoginAttempts = 0, LockoutEndUtc = NULL, UpdatedAt = datetime('now') WHERE UserId = @UserId;",
            new { UserId = target.UserId });

        await _auditService.LogEventAsync("ACCOUNT_UNLOCKED", "Success", actorUserId: actorUserId, actorUsername: actorUsername, targetUsername: target.Username, sourceIp: sourceIp, ct: ct);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> SetUserActiveAsync(
        int actorUserId,
        string actorUsername,
        int targetUserId,
        bool isActive,
        string? sourceIp,
        CancellationToken ct = default)
    {
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var target = await conn.QueryFirstOrDefaultAsync<User>("SELECT * FROM Users WHERE UserId = @UserId LIMIT 1;", new { UserId = targetUserId });
        if (target is null)
        {
            return (false, "Target account not found.");
        }

        if (!isActive && string.Equals(target.Role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            int activeAdmins = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Users WHERE Role = 'Admin' AND IsActive = 1 AND UserId != @TargetId;",
                new { TargetId = target.UserId });
            if (activeAdmins == 0)
            {
                return (false, "Cannot disable the last active Admin account.");
            }
        }

        string newStamp = Guid.NewGuid().ToString("N");
        await conn.ExecuteAsync(
            "UPDATE Users SET IsActive = @IsActive, SecurityStamp = @Stamp, UpdatedAt = datetime('now') WHERE UserId = @UserId;",
            new { IsActive = isActive ? 1 : 0, Stamp = newStamp, UserId = target.UserId });

        await _auditService.LogEventAsync(
            "USER_STATUS_CHANGED",
            "Success",
            actorUserId: actorUserId,
            actorUsername: actorUsername,
            targetUsername: target.Username,
            sourceIp: sourceIp,
            details: isActive ? "Account enabled" : "Account disabled and sessions revoked",
            ct: ct);

        if (!isActive)
        {
            await _auditService.LogEventAsync("SESSION_REVOKED", "Success", actorUserId: actorUserId, actorUsername: actorUsername, targetUsername: target.Username, sourceIp: sourceIp, details: "Revoked due to account deactivation", ct: ct);
        }

        return (true, null);
    }

    public async Task RotateSecurityStampAsync(int userId, string? sourceIp, string reason, CancellationToken ct = default)
    {
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var user = await conn.QueryFirstOrDefaultAsync<User>("SELECT UserId, Username FROM Users WHERE UserId = @UserId LIMIT 1;", new { UserId = userId });
        if (user is null) return;

        await conn.ExecuteAsync(
            "UPDATE Users SET SecurityStamp = @Stamp, UpdatedAt = datetime('now') WHERE UserId = @UserId;",
            new { Stamp = Guid.NewGuid().ToString("N"), UserId = userId });

        await _auditService.LogEventAsync("SESSION_REVOKED", "Success", actorUserId: user.UserId, actorUsername: user.Username, targetUsername: user.Username, sourceIp: sourceIp, details: reason, ct: ct);
    }

    public async Task<bool> VerifyStepUpPasswordAsync(int userId, string? password, string? sourceIp, string operationName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password))
        {
            await _auditService.LogEventAsync("STEPUP_FAILED", "Denied", actorUserId: userId, sourceIp: sourceIp, details: $"Missing step-up password for {operationName}", ct: ct);
            return false;
        }

        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var user = await conn.QueryFirstOrDefaultAsync<User>("SELECT * FROM Users WHERE UserId = @UserId AND IsActive = 1 LIMIT 1;", new { UserId = userId });
        if (user is null) return false;

        var res = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (res == PasswordVerificationResult.Failed)
        {
            await _auditService.LogEventAsync("STEPUP_FAILED", "Failure", actorUserId: user.UserId, actorUsername: user.Username, targetUsername: user.Username, sourceIp: sourceIp, details: $"Incorrect step-up password for {operationName}", ct: ct);
            return false;
        }

        return true;
    }

    public async Task<List<UserSummaryDto>> GetUsersAsync(CancellationToken ct = default)
    {
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<User>("SELECT * FROM Users ORDER BY UserId;");
        DateTime now = DateTime.UtcNow;

        return rows.Select(u =>
        {
            bool locked = false;
            int remainingMins = 0;
            if (!string.IsNullOrWhiteSpace(u.LockoutEndUtc) &&
                DateTime.TryParse(u.LockoutEndUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime end) &&
                end > now)
            {
                locked = true;
                remainingMins = Math.Max(1, (int)Math.Ceiling((end - now).TotalMinutes));
            }

            return new UserSummaryDto(u.UserId, u.Username, u.DisplayName, u.Role, u.IsActive, u.LastLoginAt, locked, remainingMins);
        }).ToList();
    }

    public async Task<bool> ProcessBreakGlassRecoveryAsync(CancellationToken ct = default)
    {
        if (!File.Exists(RecoveryTokenFilePath)) return false;

        try
        {
            string rawSecret = await File.ReadAllTextAsync(RecoveryTokenFilePath, Encoding.UTF8, ct);
            string tempPassphrase = rawSecret.TrimEnd('\r', '\n');

            var (valid, err) = PasswordPolicyValidator.Validate(tempPassphrase, "admin");
            if (!valid)
            {
                _logger.LogError("Break-glass recovery file found at {Path} but passphrase failed policy check ({Error}). Deleting token file.", RecoveryTokenFilePath, err);
                TryDeleteFileQuietly(RecoveryTokenFilePath);
                return false;
            }

            await using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
            var admin = await conn.QueryFirstOrDefaultAsync<User>(
                "SELECT * FROM Users WHERE Username = 'admin' COLLATE NOCASE LIMIT 1;");
            if (admin is null)
            {
                _logger.LogWarning("Break-glass recovery file found, but no 'admin' account exists yet. Deleting token file.");
                TryDeleteFileQuietly(RecoveryTokenFilePath);
                return false;
            }

            string newHash = _hasher.HashPassword(admin, tempPassphrase);
            string newStamp = Guid.NewGuid().ToString("N");

            await conn.ExecuteAsync(
                """
                UPDATE Users
                SET PasswordHash = @Hash,
                    SecurityStamp = @Stamp,
                    FailedLoginAttempts = 0,
                    LockoutEndUtc = NULL,
                    IsActive = 1,
                    UpdatedAt = datetime('now')
                WHERE UserId = @UserId;
                """,
                new { Hash = newHash, Stamp = newStamp, UserId = admin.UserId });

            TryDeleteFileQuietly(RecoveryTokenFilePath);
            await _auditService.LogEventAsync(
                "EMERGENCY_RECOVERY",
                "Success",
                actorUsername: "host-break-glass",
                targetUsername: admin.Username,
                details: "One-time recovery token consumed; admin passphrase reset and sessions revoked",
                ct: ct);

            _logger.LogWarning("One-time break-glass admin recovery completed and token file deleted. Change the admin passphrase immediately upon login.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process break-glass recovery file at {Path}.", RecoveryTokenFilePath);
            TryDeleteFileQuietly(RecoveryTokenFilePath);
            return false;
        }
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
