using System.Globalization;
using System.Security.Claims;
using Dapper;
using HomeBudget.Web.Data;
using HomeBudget.Web.Models;
using HomeBudget.Web.Security;
using HomeBudget.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

SqlMapper.AddTypeHandler(new SqliteDecimalHandler());
SqlMapper.AddTypeHandler(new SqliteBoolHandler());

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.ValueCountLimit = int.MaxValue;
    o.MultipartBodyLengthLimit = 104857600;
});

string configuredDbPath = builder.Configuration["HomeBudget:DatabasePath"] ?? @"C:\ProgramData\HomeBudget\budget.db";
string programDataDir = Path.GetDirectoryName(configuredDbPath) ?? @"C:\ProgramData\HomeBudget";
string keysDir = Path.Combine(programDataDir, "keys");
Directory.CreateDirectory(keysDir);

var dpBuilder = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("HomeBudget")
    .SetDefaultKeyLifetime(TimeSpan.FromDays(90));

if (OperatingSystem.IsWindows())
{
    dpBuilder.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "HomeBudget.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromDays(90);
        options.SlidingExpiration = true;
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "application/json";
                return ctx.Response.WriteAsJsonAsync(new { error = "Authentication required." });
            },
            OnRedirectToAccessDenied = async ctx =>
            {
                var audit = ctx.HttpContext.RequestServices.GetRequiredService<SecurityAuditService>();
                var current = AuthService.ExtractCurrentUser(ctx.Principal);
                await audit.LogEventAsync(
                    "AUTHZ_DENIED",
                    "Denied",
                    actorUserId: current?.UserId,
                    actorUsername: current?.Username,
                    sourceIp: ctx.HttpContext.Connection.RemoteIpAddress?.ToString(),
                    details: $"Forbidden access to {ctx.Request.Method} {ctx.Request.Path}",
                    ct: ctx.HttpContext.RequestAborted);

                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsJsonAsync(new { error = "Administrator privileges required." });
            },
            OnValidatePrincipal = async ctx =>
            {
                var authService = ctx.HttpContext.RequestServices.GetRequiredService<AuthService>();
                bool isValid = await authService.ValidatePrincipalSecurityStampAsync(ctx.Principal, ctx.HttpContext.RequestAborted);
                if (!isValid)
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy("Authenticated", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

builder.Services.AddCors();
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});
builder.Services.AddHomeBudgetRateLimiting();

builder.Services.AddSingleton<SqliteConnectionFactory>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<CsvParserService>();
builder.Services.AddSingleton<IngestionStagingService>();
builder.Services.AddSingleton<CategorizationService>();
builder.Services.AddSingleton<CashflowService>();
builder.Services.AddSingleton<BudgetService>();
builder.Services.AddSingleton<SecurityAuditService>();
builder.Services.AddSingleton<AuthService>();

var app = builder.Build();

await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
await app.Services.GetRequiredService<AuthService>().InitializeStartupSecurityAsync();

bool requireHttps = builder.Configuration.GetValue<bool?>("HomeBudget:RequireHttps") ?? true;

app.Use(async (ctx, next) =>
{
    if (requireHttps && !ctx.Request.IsHttps)
    {
        if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "HTTPS is required for HomeBudget authentication and API access." });
            return;
        }
    }
    await next();
});

if (requireHttps)
{
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        ctx.Response.Headers["Pragma"] = "no-cache";
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CsrfAndOriginMiddleware>();

// ============================================================================
// 0. AUTHENTICATION, USER ADMINISTRATION & SECURITY AUDIT ENDPOINTS
// ============================================================================

app.MapGet("/api/auth/status", async (ClaimsPrincipal principal, AuthService authService, CancellationToken ct) =>
{
    var status = await authService.GetAuthStatusAsync(principal, ct);
    return Results.Ok(status);
}).AllowAnonymous();

app.MapPost("/api/auth/setup", async (
    SetupRequest req,
    HttpContext httpContext,
    AuthService authService,
    BackupService backupService,
    CancellationToken ct) =>
{
    string? sourceIp = httpContext.Connection.RemoteIpAddress?.ToString();
    var (success, statusCode, error) = await authService.CompleteInitialSetupAsync(req, sourceIp, ct);
    if (!success)
    {
        return Results.Json(new { error }, statusCode: statusCode);
    }

    await backupService.RequestBackupAsync("initial_setup", isMajorOperation: true, ct);
    return Results.Ok(new { setupCompleted = true });
}).AllowAnonymous().RequireRateLimiting(RateLimitingExtensions.AuthPolicyName);

app.MapPost("/api/auth/login", async (
    LoginRequest req,
    HttpContext httpContext,
    AuthService authService,
    CancellationToken ct) =>
{
    string? sourceIp = httpContext.Connection.RemoteIpAddress?.ToString();
    var (success, statusCode, error, user) = await authService.AuthenticateAsync(req.Username, req.Password, sourceIp, ct);
    if (!success || user is null)
    {
        return Results.Json(new { error }, statusCode: statusCode);
    }

    var principal = AuthService.BuildClaimsPrincipal(user, req.RememberMe);
    var authProps = new AuthenticationProperties
    {
        IsPersistent = req.RememberMe,
        AllowRefresh = true,
        ExpiresUtc = req.RememberMe
            ? DateTimeOffset.UtcNow.AddDays(90)
            : DateTimeOffset.UtcNow.AddHours(12)
    };

    await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProps);

    return Results.Ok(new
    {
        authenticated = true,
        user = new CurrentUserDto(user.UserId, user.Username, user.DisplayName, user.Role)
    });
}).AllowAnonymous().RequireRateLimiting(RateLimitingExtensions.AuthPolicyName);

app.MapPost("/api/auth/logout", async (
    bool? allDevices,
    ClaimsPrincipal principal,
    HttpContext httpContext,
    AuthService authService,
    CancellationToken ct) =>
{
    var current = AuthService.ExtractCurrentUser(principal);
    if (allDevices == true && current is not null)
    {
        await authService.RotateSecurityStampAsync(
            current.UserId,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            "User requested sign-out across all devices",
            ct);
    }

    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { loggedOut = true });
}).RequireAuthorization("Authenticated");

app.MapGet("/api/auth/me", (ClaimsPrincipal principal) =>
{
    var current = AuthService.ExtractCurrentUser(principal);
    return current is null
        ? Results.Unauthorized()
        : Results.Ok(current);
}).RequireAuthorization("Authenticated");

app.MapPut("/api/auth/change-password", async (
    ChangePasswordRequest req,
    ClaimsPrincipal principal,
    HttpContext httpContext,
    AuthService authService,
    CancellationToken ct) =>
{
    var current = AuthService.ExtractCurrentUser(principal);
    if (current is null) return Results.Unauthorized();

    string? sourceIp = httpContext.Connection.RemoteIpAddress?.ToString();
    var (success, error, updatedUser) = await authService.ChangePasswordAsync(
        current.UserId,
        req.CurrentPassword,
        req.NewPassword,
        sourceIp,
        ct);

    if (!success || updatedUser is null)
    {
        return Results.BadRequest(new { error });
    }

    bool isPersistent = principal.FindFirstValue(AuthService.ClaimPersistent) == "1";
    var refreshedPrincipal = AuthService.BuildClaimsPrincipal(updatedUser, isPersistent);
    await httpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        refreshedPrincipal,
        new AuthenticationProperties
        {
            IsPersistent = isPersistent,
            AllowRefresh = true,
            ExpiresUtc = isPersistent
                ? DateTimeOffset.UtcNow.AddDays(90)
                : DateTimeOffset.UtcNow.AddHours(12)
        });

    return Results.Ok(new { updated = true });
}).RequireAuthorization("Authenticated");

app.MapGet("/api/users", async (AuthService authService, CancellationToken ct) =>
{
    var users = await authService.GetUsersAsync(ct);
    return Results.Ok(users);
}).RequireAuthorization("AdminOnly");

app.MapPut("/api/users/{id:int}/reset-password", async (
    int id,
    ResetUserPasswordRequest req,
    ClaimsPrincipal principal,
    HttpContext httpContext,
    AuthService authService,
    CancellationToken ct) =>
{
    var current = AuthService.ExtractCurrentUser(principal);
    if (current is null) return Results.Unauthorized();

    var (success, error) = await authService.ResetUserPasswordAsync(
        current.UserId,
        current.Username,
        id,
        req.NewPassword,
        httpContext.Connection.RemoteIpAddress?.ToString(),
        ct);

    return success ? Results.Ok(new { updated = true }) : Results.BadRequest(new { error });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/users/{id:int}/unlock", async (
    int id,
    ClaimsPrincipal principal,
    HttpContext httpContext,
    AuthService authService,
    CancellationToken ct) =>
{
    var current = AuthService.ExtractCurrentUser(principal);
    if (current is null) return Results.Unauthorized();

    var (success, error) = await authService.UnlockUserAsync(
        current.UserId,
        current.Username,
        id,
        httpContext.Connection.RemoteIpAddress?.ToString(),
        ct);

    return success ? Results.Ok(new { unlocked = true }) : Results.BadRequest(new { error });
}).RequireAuthorization("AdminOnly");

app.MapPut("/api/users/{id:int}/active", async (
    int id,
    UpdateUserActiveRequest req,
    ClaimsPrincipal principal,
    HttpContext httpContext,
    AuthService authService,
    CancellationToken ct) =>
{
    var current = AuthService.ExtractCurrentUser(principal);
    if (current is null) return Results.Unauthorized();

    var (success, error) = await authService.SetUserActiveAsync(
        current.UserId,
        current.Username,
        id,
        req.IsActive,
        httpContext.Connection.RemoteIpAddress?.ToString(),
        ct);

    return success ? Results.Ok(new { updated = true, isActive = req.IsActive }) : Results.BadRequest(new { error });
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/audit-logs", async (
    int? limit,
    int? offset,
    string? eventType,
    SecurityAuditService auditService,
    CancellationToken ct) =>
{
    var result = await auditService.QueryLogsAsync(limit ?? 50, offset ?? 0, eventType, ct);
    return Results.Ok(result);
}).RequireAuthorization("AdminOnly");

// ============================================================================
// 1. ACCOUNTS & CATEGORIES / TOPOLOGY ENDPOINTS (WITH AUTOMATIC DB BACKUPS)
// ============================================================================

app.MapGet("/api/accounts", async (SqliteConnectionFactory db) =>
{
    await using var conn = await db.CreateOpenConnectionAsync();
    var accounts = await conn.QueryAsync<Account>("SELECT * FROM Accounts WHERE IsActive = 1 ORDER BY AccountId;");
    return Results.Ok(accounts);
}).RequireAuthorization("Authenticated");

app.MapPost("/api/accounts", async (Account req, SqliteConnectionFactory db, BackupService backupService, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.AccountName))
    {
        return Results.BadRequest(new { error = "AccountName is required." });
    }

    await using var conn = await db.CreateOpenConnectionAsync(ct);
    int id = await conn.ExecuteScalarAsync<int>(
        "INSERT INTO Accounts (AccountName, Institution, AccountType, IsActive) VALUES (@AccountName, @Institution, @AccountType, 1); SELECT last_insert_rowid();",
        new
        {
            AccountName = req.AccountName.Trim(),
            Institution = string.IsNullOrWhiteSpace(req.Institution) ? "Chase" : req.Institution.Trim(),
            AccountType = string.IsNullOrWhiteSpace(req.AccountType) ? "CreditCard" : req.AccountType.Trim()
}).RequireAuthorization("AdminOnly");
    req.AccountId = id;
    string? backupFile = await backupService.RequestBackupAsync("account_create", isMajorOperation: false, ct);
    return Results.Ok(new
    {
        req.AccountId,
        req.AccountName,
        req.Institution,
        req.AccountType,
        req.IsActive,
        backupFileCreated = backupFile
    });
});

app.MapPut("/api/accounts/{id:int}", async (int id, Account req, SqliteConnectionFactory db, BackupService backupService, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.AccountName))
    {
        return Results.BadRequest(new { error = "AccountName is required." });
    }

    await using var conn = await db.CreateOpenConnectionAsync(ct);
    var existing = await conn.QuerySingleOrDefaultAsync<Account>(
        "SELECT * FROM Accounts WHERE AccountId = @Id;", new { Id = id });
    if (existing == null)
    {
        return Results.NotFound(new { error = $"Account with ID {id} not found." });
    }

    string newName = req.AccountName.Trim();
    string institution = string.IsNullOrWhiteSpace(req.Institution) ? existing.Institution : req.Institution.Trim();
    string accountType = string.IsNullOrWhiteSpace(req.AccountType) ? existing.AccountType : req.AccountType.Trim();

    await conn.ExecuteAsync(
        """
        UPDATE Accounts 
        SET AccountName = @AccountName, 
            Institution = @Institution, 
            AccountType = @AccountType
        WHERE AccountId = @AccountId;
        """,
        new
        {
            AccountId = id,
            AccountName = newName,
            Institution = institution,
            AccountType = accountType
}).RequireAuthorization("AdminOnly");

    string? backupFile = await backupService.RequestBackupAsync("account_rename", isMajorOperation: false, ct);
    return Results.Ok(new
    {
        accountId = id,
        accountName = newName,
        institution,
        accountType,
        backupFileCreated = backupFile
    });
});

app.MapGet("/api/categories", async (string? sortBy, SqliteConnectionFactory db) =>
{
    await using var conn = await db.CreateOpenConnectionAsync();
    string orderClause = (sortBy ?? "custom_order").Trim().ToLowerInvariant() switch
    {
        "alpha_asc" => "Name COLLATE NOCASE ASC",
        "alpha_desc" => "Name COLLATE NOCASE DESC",
        "recent" => "COALESCE(UpdatedAt, CreatedAt, '') DESC, Name COLLATE NOCASE ASC",
        "budget_desc" => "MonthlyBudget DESC, Name COLLATE NOCASE ASC",
        _ => "SortOrder ASC, Name COLLATE NOCASE ASC"
    };
    var categories = await conn.QueryAsync<Category>(
        $"SELECT * FROM Categories WHERE IsActive = 1 ORDER BY {orderClause};");
    return Results.Ok(categories);
}).RequireAuthorization("Authenticated");

app.MapPost("/api/categories", async (UpsertCategoryRequest req, CategorizationService catService, CancellationToken ct) =>
{
    try
    {
        var created = await catService.CreateCategoryAsync(req, ct);
        return Results.Ok(created);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("Authenticated");

app.MapPut("/api/categories/{id:int}", async (int id, UpsertCategoryRequest req, CategorizationService catService, CancellationToken ct) =>
{
    try
    {
        var updated = await catService.UpdateCategoryAsync(id, req, ct);
        return updated is not null ? Results.Ok(updated) : Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("Authenticated");

app.MapDelete("/api/categories/{id:int}", async (int id, int? replacementCategoryId, CategorizationService catService, CancellationToken ct) =>
{
    try
    {
        var res = await catService.DeleteCategoryAsync(id, replacementCategoryId, ct);
        return res is not null ? Results.Ok(res) : Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("Authenticated");

app.MapPut("/api/categories/{id:int}/budget", async (int id, UpdateBudgetRequest req, BudgetService budgetService, CancellationToken ct) =>
{
    if (req.MonthlyBudget < 0m)
    {
        return Results.BadRequest(new { error = "MonthlyBudget cannot be negative." });
    }
    var updated = await budgetService.UpdateCategoryMonthlyBudgetAsync(id, req.MonthlyBudget, ct);
    return updated is not null ? Results.Ok(updated) : Results.NotFound();
}).RequireAuthorization("Authenticated");

app.MapPost("/api/categories/{id:int}/merge-into/{targetId:int}", async (
    int id,
    int targetId,
    CategorizationService catService,
    CancellationToken ct) =>
{
    try
    {
        var result = await catService.MergeCategoriesAsync(id, targetId, ct);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("Authenticated");

app.MapGet("/api/topology", async (CategorizationService catService, CancellationToken ct) =>
{
    var result = await catService.GetTopologyAsync(ct);
    return Results.Ok(result);
}).RequireAuthorization("Authenticated");

app.MapPost("/api/rules", async (UpsertRuleRequest req, CategorizationService catService, CancellationToken ct) =>
{
    try
    {
        var rule = await catService.CreateRuleAsync(req, ct);
        return Results.Ok(rule);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("Authenticated");

app.MapPut("/api/rules/{id:int}", async (int id, UpsertRuleRequest req, CategorizationService catService, CancellationToken ct) =>
{
    try
    {
        var rule = await catService.UpdateRuleAsync(id, req, ct);
        return rule is not null ? Results.Ok(rule) : Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("Authenticated");

app.MapDelete("/api/rules/{id:int}", async (int id, CategorizationService catService, CancellationToken ct) =>
{
    bool deleted = await catService.DeleteRuleAsync(id, ct);
    return deleted ? Results.NoContent() : Results.NotFound();
}).RequireAuthorization("Authenticated");

app.MapPost("/api/rules/back-propagate", async (
    BackPropagateRequest req,
    CategorizationService catService,
    CancellationToken ct) =>
{
    var response = await catService.BackPropagateRulesAsync(req, ct);
    return Results.Ok(response);
}).RequireAuthorization("AdminOnly");

// ============================================================================
// 2. CASHFLOW SANKEY & MONTHLY BUDGET VIEWS
// ============================================================================

app.MapGet("/api/cashflow", async (
    string? windowType,
    string? startDate,
    string? endDate,
    int? months,
    bool? includeSubcategories,
    string? categorySort,
    CashflowService cashflowService,
    CancellationToken ct) =>
{
    var result = await cashflowService.GetCashflowAsync(
        windowType,
        startDate,
        endDate,
        months,
        includeSubcategories ?? false,
        categorySort,
        ct);
    return Results.Ok(result);
}).RequireAuthorization("Authenticated");

app.MapGet("/api/budgets/{yearMonth?}", async (
    string? yearMonth,
    BudgetService budgetService,
    CancellationToken ct) =>
{
    var result = await budgetService.GetMonthlyBudgetAsync(yearMonth, ct);
    return Results.Ok(result);
}).RequireAuthorization("Authenticated");

// ============================================================================
// 3. CSV UPLOAD STAGING & DE-DUPLICATION ENDPOINTS
// ============================================================================

app.MapPost("/api/uploads/stage", async (
    HttpRequest request,
    IngestionStagingService stagingService,
    CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    if (!int.TryParse(form["accountId"], out int accountId))
    {
        return Results.BadRequest(new { error = "Valid accountId is required." });
    }

    if (form.Files.Count == 0)
    {
        return Results.BadRequest(new { error = "At least one CSV file is required." });
    }

    try
    {
        var session = await stagingService.StageFilesAsync(accountId, form.Files, ct);

        return Results.Ok(new
        {
            session.StagingSessionId,
            session.AccountId,
            session.FileNames,
            summary = new
            {
                totalParsed = session.Candidates.Count,
                autoApproveCount = session.Candidates.Count(c => c.DedupStatus == "AutoApprove"),
                potentialCollisionCount = session.Candidates.Count(c => c.DedupStatus == "PotentialCollision"),
                manualReviewCount = session.Candidates.Count(c => c.RequiresManualReview && c.DedupStatus == "AutoApprove"),
                skippedDuplicateCount = session.Candidates.Count(c => c.DedupStatus == "SkippedDuplicate"),
                skippedDeletedCount = session.Candidates.Count(c => c.DedupStatus == "SkippedDeleted")
            },
            candidates = session.Candidates
}).RequireAuthorization("AdminOnly");
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapPost("/api/uploads/commit", async (
    CommitStagingRequest request,
    IngestionStagingService stagingService,
    CancellationToken ct) =>
{
    try
    {
        var result = await stagingService.CommitSessionAsync(request, ct);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

// ============================================================================
// 4. TRANSACTION INSPECTOR, CATEGORIZATION PROMPTS, EDIT, SPLIT & DELETE
// ============================================================================

app.MapGet("/api/transactions/similar-count", async (
    string? pattern,
    int? excludeTransactionId,
    CategorizationService catService,
    CancellationToken ct) =>
{
    var res = await catService.GetSimilarTransactionsCountAsync(pattern, excludeTransactionId, ct);
    return Results.Ok(res);
}).RequireAuthorization("Authenticated");

app.MapPost("/api/transactions/{id:int}/categorize", async (
    int id,
    CategorizeTransactionRequest req,
    CategorizationService catService,
    CancellationToken ct) =>
{
    try
    {
        var res = await catService.CategorizeTransactionWithOptionsAsync(id, req, ct);
        return res is not null ? Results.Ok(res) : Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("Authenticated");

app.MapGet("/api/transactions", async (
    string? startDate,
    string? endDate,
    int? accountId,
    int? categoryId,
    bool? uncategorizedOnly,
    string? transactionType,
    decimal? minAmount,
    decimal? maxAmount,
    string? search,
    string? sortBy,
    int? page,
    int? pageSize,
    SqliteConnectionFactory db,
    CancellationToken ct) =>
{
    await using var conn = await db.CreateOpenConnectionAsync(ct);

    var conditions = new List<string> { "1=1" };
    var parameters = new DynamicParameters();

    if (!string.IsNullOrWhiteSpace(startDate))
    {
        conditions.Add("t.TransactionDate >= @StartDate");
        parameters.Add("StartDate", startDate.Trim());
    }
    if (!string.IsNullOrWhiteSpace(endDate))
    {
        conditions.Add("t.TransactionDate <= @EndDate");
        parameters.Add("EndDate", endDate.Trim());
    }
    if (accountId.HasValue)
    {
        conditions.Add("t.AccountId = @AccountId");
        parameters.Add("AccountId", accountId.Value);
    }
    if (uncategorizedOnly == true)
    {
        conditions.Add("t.CategoryId IS NULL AND t.TransactionType IN ('Expense', 'Refund')");
    }
    else if (categoryId.HasValue)
    {
        conditions.Add("t.CategoryId = @CategoryId");
        parameters.Add("CategoryId", categoryId.Value);
    }
    if (!string.IsNullOrWhiteSpace(transactionType))
    {
        string tt = transactionType.Trim();
        if (tt.Equals("ExpenseOrRefund", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add("t.TransactionType IN ('Expense', 'Refund')");
        }
        else
        {
            string normalizedType = TransactionTypes.Normalize(tt);
            conditions.Add("t.TransactionType = @TransactionType");
            parameters.Add("TransactionType", normalizedType);
        }
    }
    if (minAmount.HasValue)
    {
        conditions.Add("ABS(t.Amount) >= @MinAmount");
        parameters.Add("MinAmount", (double)minAmount.Value);
    }
    if (maxAmount.HasValue)
    {
        conditions.Add("ABS(t.Amount) <= @MaxAmount");
        parameters.Add("MaxAmount", (double)maxAmount.Value);
    }
    if (!string.IsNullOrWhiteSpace(search))
    {
        conditions.Add("(t.RawDescription LIKE @Search OR t.CleanMerchantName LIKE @Search OR t.Subcategory LIKE @Search OR t.Notes LIKE @Search)");
        parameters.Add("Search", $"%{search.Trim()}%");
    }

    string orderByClause = (sortBy ?? "date_desc").ToLowerInvariant() switch
    {
        "date_asc" => "t.TransactionDate ASC, t.TransactionId ASC",
        "recent" or "recent_changes" => "COALESCE(t.UpdatedAt, t.CreatedAt, t.TransactionDate) DESC, t.TransactionId DESC",
        "amount_desc" => "ABS(t.Amount) DESC, t.TransactionDate DESC",
        "amount_asc" => "ABS(t.Amount) ASC, t.TransactionDate DESC",
        "alpha_asc" or "merchant_asc" => "t.CleanMerchantName COLLATE NOCASE ASC, t.TransactionDate DESC",
        "alpha_desc" or "merchant_desc" => "t.CleanMerchantName COLLATE NOCASE DESC, t.TransactionDate DESC",
        "account_asc" => "a.AccountName COLLATE NOCASE ASC, t.TransactionDate DESC",
        "category_asc" => "COALESCE(c.Name, 'ZZZZ') COLLATE NOCASE ASC, t.TransactionDate DESC",
        _ => "t.TransactionDate DESC, t.TransactionId DESC"
    };

    string whereClause = string.Join(" AND ", conditions);
    int limit = Math.Clamp(pageSize ?? 200, 1, 1000);
    int offset = (Math.Max(page ?? 1, 1) - 1) * limit;
    parameters.Add("Limit", limit);
    parameters.Add("Offset", offset);

    int totalCount = await conn.ExecuteScalarAsync<int>(
        $"SELECT COUNT(*) FROM Transactions t WHERE {whereClause};", parameters);

    var items = await conn.QueryAsync<TransactionDto>(
        $"SELECT t.*, a.AccountName, c.Name AS CategoryName " +
        $"FROM Transactions t " +
        $"JOIN Accounts a ON t.AccountId = a.AccountId " +
        $"LEFT JOIN Categories c ON t.CategoryId = c.CategoryId " +
        $"WHERE {whereClause} " +
        $"ORDER BY {orderByClause} " +
        $"LIMIT @Limit OFFSET @Offset;",
        parameters);

    return Results.Ok(new { totalCount, page = page ?? 1, pageSize = limit, items });
}).RequireAuthorization("Authenticated");

app.MapPut("/api/transactions/{id:int}", async (
    int id,
    UpdateTransactionRequest req,
    CategorizationService catService,
    CancellationToken ct) =>
{
    try
    {
        var result = await catService.UpdateTransactionAsync(id, req, ct);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/transactions/{id:int}/split", async (
    int id,
    SplitTransactionRequest req,
    CategorizationService catService,
    CancellationToken ct) =>
{
    try
    {
        var childRows = await catService.SplitTransactionAsync(id, req, ct);
        return Results.Ok(childRows);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

app.MapDelete("/api/transactions/{id:int}", async (
    int id,
    bool? addToTombstone,
    SqliteConnectionFactory db,
    BackupService backupService,
    CancellationToken ct) =>
{
    await using var conn = await db.CreateOpenConnectionAsync(ct);
    var txRow = await conn.QuerySingleOrDefaultAsync<TransactionEntity>(
        "SELECT * FROM Transactions WHERE TransactionId = @Id;", new { Id = id });

    if (txRow is null)
    {
        return Results.NotFound();
    }

    await using var sqlTx = await conn.BeginTransactionAsync(ct);

    if (addToTombstone ?? true)
    {
        await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO DeletedTransactionHashes (DedupHash, AccountId, RawDescription, TransactionDate, Amount) VALUES (@DedupHash, @AccountId, @RawDescription, @TransactionDate, @Amount);",
            txRow,
            sqlTx);

        string baseHash = CsvParserService.ExtractBaseHash(txRow.DedupHash);
        if (!string.Equals(baseHash, txRow.DedupHash, StringComparison.OrdinalIgnoreCase))
        {
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO DeletedTransactionHashes (DedupHash, AccountId, RawDescription, TransactionDate, Amount) VALUES (@BaseHash, @AccountId, @RawDescription, @TransactionDate, @Amount);",
                new { BaseHash = baseHash, txRow.AccountId, txRow.RawDescription, txRow.TransactionDate, txRow.Amount },
                sqlTx);
        }
    }

    await conn.ExecuteAsync("DELETE FROM Transactions WHERE TransactionId = @Id;", new { Id = id }, sqlTx);
    await sqlTx.CommitAsync(ct);

    await backupService.RequestBackupAsync("transaction_delete", isMajorOperation: false, ct);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/transactions/bulk-delete", async (
    BulkDeleteTransactionsRequest req,
    SqliteConnectionFactory db,
    BackupService backupService,
    CancellationToken ct) =>
{
    if (req.TransactionIds is null || req.TransactionIds.Count == 0)
    {
        return Results.BadRequest(new { error = "No transactionIds provided." });
    }

    if (req.TransactionIds.Count > 500)
    {
        return Results.BadRequest(new { error = "Bulk delete is limited to at most 500 transactions per request." });
    }

    await using var conn = await db.CreateOpenConnectionAsync(ct);
    var rows = (await conn.QueryAsync<TransactionEntity>(
        "SELECT * FROM Transactions WHERE TransactionId IN @Ids;",
        new { Ids = req.TransactionIds })).ToList();

    if (rows.Count == 0)
    {
        return Results.Ok(new { deletedCount = 0, backupFileCreated = (string?)null });
    }

    await using var sqlTx = await conn.BeginTransactionAsync(ct);
    if (req.AddToTombstone)
    {
        foreach (var txRow in rows)
        {
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO DeletedTransactionHashes (DedupHash, AccountId, RawDescription, TransactionDate, Amount) VALUES (@DedupHash, @AccountId, @RawDescription, @TransactionDate, @Amount);",
                txRow,
                sqlTx);

            string baseHash = CsvParserService.ExtractBaseHash(txRow.DedupHash);
            if (!string.Equals(baseHash, txRow.DedupHash, StringComparison.OrdinalIgnoreCase))
            {
                await conn.ExecuteAsync(
                    "INSERT OR IGNORE INTO DeletedTransactionHashes (DedupHash, AccountId, RawDescription, TransactionDate, Amount) VALUES (@BaseHash, @AccountId, @RawDescription, @TransactionDate, @Amount);",
                    new { BaseHash = baseHash, txRow.AccountId, txRow.RawDescription, txRow.TransactionDate, txRow.Amount },
                    sqlTx);
            }
        }
    }

    int deletedCount = await conn.ExecuteAsync(
        "DELETE FROM Transactions WHERE TransactionId IN @Ids;",
        new { Ids = req.TransactionIds },
        sqlTx);

    await sqlTx.CommitAsync(ct);
    string? backupFile = await backupService.RequestBackupAsync("bulk_delete", isMajorOperation: true, ct);

    return Results.Ok(new { deletedCount, backupFileCreated = backupFile });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/transactions/clear-history", async (
    ClearTransactionHistoryRequest? req,
    CategorizationService catService,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    try
    {
        var result = await catService.ClearTransactionHistoryAsync(req ?? new ClearTransactionHistoryRequest(), ct);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        var logger = loggerFactory.CreateLogger("ClearHistory");
        logger.LogError(ex, "ClearTransactionHistoryAsync failed");
        return Results.Problem(detail: ex.Message, statusCode: 500, title: "Failed to clear transaction history");
    }
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/admin/reset-all", async (
    ResetAllDatabaseRequest? req,
    ClaimsPrincipal principal,
    HttpContext httpContext,
    AuthService authService,
    CategorizationService catService,
    ILogger<CategorizationService> logger,
    CancellationToken ct) =>
{
    var current = AuthService.ExtractCurrentUser(principal);
    if (current is null) return Results.Unauthorized();

    bool stepUpOk = await authService.VerifyStepUpPasswordAsync(
        current.UserId,
        req?.ConfirmPassword,
        httpContext.Connection.RemoteIpAddress?.ToString(),
        "ResetAllDatabase",
        ct);
    if (!stepUpOk)
    {
        return Results.Json(new { error = "Current admin password confirmation is required to reset all data." }, statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        var result = await catService.ResetAllDatabaseAsync(req ?? new ResetAllDatabaseRequest(), ct);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to reset database: {Message}", ex.Message);
        return Results.Problem(detail: ex.Message, title: "Database Reset Error", statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization("AdminOnly");


// ============================================================================
// 5. SETTINGS, INCOME OVERRIDES & LOCAL BACKUP MANAGEMENT
// ============================================================================

app.MapGet("/api/settings", async (SqliteConnectionFactory db) =>
{
    await using var conn = await db.CreateOpenConnectionAsync();
    var dict = (await conn.QueryAsync<(string Key, string Value)>("SELECT Key, Value FROM Settings;"))
        .ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    var overrides = await conn.QueryAsync<IncomeOverrideDto>(
        "SELECT YearMonth, IncomeAmount, Notes FROM IncomeOverrides ORDER BY YearMonth DESC;");

    return Results.Ok(new
    {
        persistentMonthlyIncome = dict.TryGetValue("PersistentMonthlyIncome", out var inc) && decimal.TryParse(inc, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 10000.00m,
        googlePayrollMatchPattern = dict.GetValueOrDefault("GooglePayrollMatchPattern", "GOOGLE"),
        currentMonthIncomeMode = dict.GetValueOrDefault("CurrentMonthIncomeMode", "Auto"),
        hasGeminiApiKey = !string.IsNullOrWhiteSpace(dict.GetValueOrDefault("GeminiApiKey", "")),
        incomeOverrides = overrides
}).RequireAuthorization("AdminOnly");
});

app.MapPut("/api/settings", async (SettingsDto req, SqliteConnectionFactory db, BackupService backupService, CancellationToken ct) =>
{
    if (req.PersistentMonthlyIncome < 0m)
    {
        return Results.BadRequest(new { error = "PersistentMonthlyIncome cannot be negative." });
    }

    await using var conn = await db.CreateOpenConnectionAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    async Task UpsertSetting(string key, string val)
    {
        await conn.ExecuteAsync(
            "INSERT INTO Settings (Key, Value, UpdatedAt) VALUES (@Key, @Value, datetime('now')) ON CONFLICT(Key) DO UPDATE SET Value = @Value, UpdatedAt = datetime('now');",
            new { Key = key, Value = val },
            tx);
    }

    await UpsertSetting("PersistentMonthlyIncome", req.PersistentMonthlyIncome.ToString("F2", CultureInfo.InvariantCulture));
    await UpsertSetting("GooglePayrollMatchPattern", (req.GooglePayrollMatchPattern ?? "GOOGLE").Trim());
    await UpsertSetting("CurrentMonthIncomeMode", (req.CurrentMonthIncomeMode ?? "Auto").Trim());
    if (!string.IsNullOrWhiteSpace(req.GeminiApiKey))
    {
        await UpsertSetting("GeminiApiKey", req.GeminiApiKey.Trim());
    }

    await tx.CommitAsync(ct);
    string? backupFile = await backupService.RequestBackupAsync("settings_update", isMajorOperation: false, ct);
    return Results.Ok(new { updated = true, backupFileCreated = backupFile });
}).RequireAuthorization("AdminOnly");

app.MapPut("/api/settings/income-overrides", async (IncomeOverrideDto req, SqliteConnectionFactory db, BackupService backupService, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.YearMonth) || req.IncomeAmount < 0m)
    {
        return Results.BadRequest(new { error = "Valid YearMonth and non-negative IncomeAmount are required." });
    }

    await using var conn = await db.CreateOpenConnectionAsync(ct);
    await conn.ExecuteAsync(
        "INSERT INTO IncomeOverrides (YearMonth, IncomeAmount, Notes) VALUES (@YearMonth, @IncomeAmount, @Notes) ON CONFLICT(YearMonth) DO UPDATE SET IncomeAmount = @IncomeAmount, Notes = @Notes;",
        req);
    await backupService.RequestBackupAsync("income_override", isMajorOperation: false, ct);
    return Results.Ok(req);
}).RequireAuthorization("AdminOnly");

app.MapDelete("/api/settings/income-overrides/{yearMonth}", async (string yearMonth, SqliteConnectionFactory db, BackupService backupService, CancellationToken ct) =>
{
    await using var conn = await db.CreateOpenConnectionAsync(ct);
    await conn.ExecuteAsync("DELETE FROM IncomeOverrides WHERE YearMonth = @YearMonth;", new { YearMonth = yearMonth });
    await backupService.RequestBackupAsync("income_override_delete", isMajorOperation: false, ct);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/backups", async (BackupService backupService, CancellationToken ct) =>
{
    var config = await backupService.GetPolicyConfigAsync(ct);
    var backups = backupService.ListBackups();
    var status = backupService.GetStatus(backups);
    return Results.Ok(new
    {
        backupDirectoryPath = backupService.BackupDirectoryPath,
        config,
        status,
        backups
}).RequireAuthorization("AdminOnly");
});

app.MapPut("/api/backups/config", async (BackupPolicyConfigDto req, BackupService backupService, CancellationToken ct) =>
{
    var config = await backupService.UpdatePolicyConfigAsync(req, ct);
    var backups = backupService.ListBackups();
    var status = backupService.GetStatus(backups);
    return Results.Ok(new { config, status, backups });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/backups/trigger", async (string? reason, BackupService backupService, CancellationToken ct) =>
{
    string effectiveReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason.Trim();
    string? fileName = await backupService.FlushPendingSnapshotAsync(effectiveReason, ct);
    if (string.IsNullOrWhiteSpace(fileName))
    {
        return Results.Problem("Failed to create SQLite backup snapshot.", statusCode: 500);
    }
    var backups = backupService.ListBackups();
    return Results.Ok(new { fileName, backupSucceeded = true, status = backupService.GetStatus(backups) });
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/backups/download/{fileName}", (string fileName, BackupService backupService) =>
{
    string safeName = Path.GetFileName(fileName);
    string fullPath = Path.Combine(backupService.BackupDirectoryPath, safeName);
    if (!File.Exists(fullPath))
    {
        return Results.NotFound();
    }
    return Results.File(fullPath, "application/octet-stream", safeName);
}).RequireAuthorization("AdminOnly");

app.MapDelete("/api/backups/{fileName}", (string fileName, BackupService backupService) =>
{
    bool deleted = backupService.DeleteBackup(fileName);
    if (!deleted)
    {
        return Results.NotFound(new { error = "Backup snapshot not found." });
    }
    var backups = backupService.ListBackups();
    return Results.Ok(new { deleted = true, status = backupService.GetStatus(backups) });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/backups/bulk-delete", (BulkDeleteBackupsRequest req, BackupService backupService) =>
{
    int deletedCount = backupService.BulkDeleteBackups(req.FileNames ?? []);
    var backups = backupService.ListBackups();
    return Results.Ok(new { deletedCount, status = backupService.GetStatus(backups) });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/backups/prune", (PruneBackupsRequest req, BackupService backupService) =>
{
    int deletedCount = backupService.PruneBackupsToCount(req.KeepCount);
    var backups = backupService.ListBackups();
    return Results.Ok(new { deletedCount, status = backupService.GetStatus(backups) });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/backups/restore/{fileName}", async (
    string fileName,
    RestoreBackupRequest? req,
    ClaimsPrincipal principal,
    HttpContext httpContext,
    AuthService authService,
    BackupService backupService,
    CancellationToken ct) =>
{
    var current = AuthService.ExtractCurrentUser(principal);
    if (current is null) return Results.Unauthorized();

    bool stepUpOk = await authService.VerifyStepUpPasswordAsync(
        current.UserId,
        req?.ConfirmPassword,
        httpContext.Connection.RemoteIpAddress?.ToString(),
        $"RestoreSnapshot:{fileName}",
        ct);
    if (!stepUpOk)
    {
        return Results.Json(new { error = "Current admin password confirmation is required to restore the database." }, statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        var result = await backupService.RestoreSnapshotAsync(
            fileName,
            req?.CreatePreRestoreBackup ?? true,
            ct);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/backups/restore-upload", async (
    HttpRequest request,
    ClaimsPrincipal principal,
    AuthService authService,
    BackupService backupService,
    CancellationToken ct) =>
{
    var current = AuthService.ExtractCurrentUser(principal);
    if (current is null) return Results.Unauthorized();

    var form = await request.ReadFormAsync(ct);
    string? confirmPassword = form["confirmPassword"].FirstOrDefault();
    bool stepUpOk = await authService.VerifyStepUpPasswordAsync(
        current.UserId,
        confirmPassword,
        request.HttpContext.Connection.RemoteIpAddress?.ToString(),
        "RestoreUploadedSnapshot",
        ct);
    if (!stepUpOk)
    {
        return Results.Json(new { error = "Current admin password confirmation is required to restore the database." }, statusCode: StatusCodes.Status403Forbidden);
    }

    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "A valid non-empty SQLite .db snapshot file is required." });
    }

    bool createPreRestoreBackup = !bool.TryParse(form["createPreRestoreBackup"], out bool flag) || flag;

    try
    {
        await using var stream = file.OpenReadStream();
        var result = await backupService.RestoreUploadedSnapshotAsync(
            stream,
            file.FileName,
            createPreRestoreBackup,
            ct);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("AdminOnly").DisableAntiforgery();

app.Run();

public partial class Program { }


