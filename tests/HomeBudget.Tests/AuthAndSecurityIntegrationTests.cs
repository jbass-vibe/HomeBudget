using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeBudget.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeBudget.Tests;

public sealed class AuthAndSecurityIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WebApplicationFactory<Program> _factory;

    public AuthAndSecurityIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"HomeBudgetWebTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HomeBudget:DatabasePath"] = Path.Combine(_tempDir, "budget.db"),
                    ["HomeBudget:BackupDirectoryPath"] = Path.Combine(_tempDir, "Backups"),
                    ["HomeBudget:RequireHttps"] = "true"
                });
            });
        });
    }

    private HttpClient CreateHttpsClient(bool handleCookies = true)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = handleCookies,
            AllowAutoRedirect = false
        });
        return client;
    }

    private static HttpRequestMessage CreateCsrfSafeRequest(HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-HomeBudget-Request", "1");
        req.Headers.Add("Origin", "https://localhost");
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        return req;
    }

    private async Task CompleteSetupAsync(HttpClient client)
    {
        string tokenPath = Path.Combine(_tempDir, "setup-token.txt");
        Assert.True(File.Exists(tokenPath), "setup-token.txt should exist before setup.");
        string token = (await File.ReadAllTextAsync(tokenPath)).Trim();

        using var req = CreateCsrfSafeRequest(HttpMethod.Post, "/api/auth/setup", new SetupRequest(
            token,
            "Household Admin",
            "AdminPassphrase!2026",
            "Household User",
            "UserPassphrase!2026"));

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public void RouteCatalog_AllApiEndpointsHaveExplicitAuthMetadata()
    {
        using var scope = _factory.Services.CreateScope();
        var endpointSource = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();
        var routeEndpoints = endpointSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? "").StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(routeEndpoints);
        foreach (var endpoint in routeEndpoints)
        {
            bool hasAllowAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            bool hasAuthorize = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;
            Assert.True(
                hasAllowAnonymous || hasAuthorize,
                $"API endpoint '{endpoint.RoutePattern.RawText}' is missing explicit AllowAnonymous or RequireAuthorization metadata.");
        }
    }

    [Fact]
    public async Task UnauthenticatedApiRequests_Return401Json_AndSecurityHeaders()
    {
        using var client = CreateHttpsClient();
        var statusRes = await client.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, statusRes.StatusCode);
        Assert.Equal("nosniff", statusRes.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", statusRes.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("no-store", statusRes.Headers.CacheControl?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);

        var budgetsRes = await client.GetAsync("/api/budgets");
        Assert.Equal(HttpStatusCode.Unauthorized, budgetsRes.StatusCode);
        string body = await budgetsRes.Content.ReadAsStringAsync();
        Assert.Contains("Authentication required", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CsrfAndOriginMiddleware_RejectsMissingHeaderAndMismatchedOrigin()
    {
        using var client = CreateHttpsClient();
        await CompleteSetupAsync(client);

        // Missing X-HomeBudget-Request header
        using var noHeaderReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("admin", "AdminPassphrase!2026", false))
        };
        noHeaderReq.Headers.Add("Origin", "https://localhost");
        var noHeaderRes = await client.SendAsync(noHeaderReq);
        Assert.Equal(HttpStatusCode.Forbidden, noHeaderRes.StatusCode);

        // Mismatched Origin header
        using var badOriginReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("admin", "AdminPassphrase!2026", false))
        };
        badOriginReq.Headers.Add("X-HomeBudget-Request", "1");
        badOriginReq.Headers.Add("Origin", "https://evil.example");
        var badOriginRes = await client.SendAsync(badOriginReq);
        Assert.Equal(HttpStatusCode.Forbidden, badOriginRes.StatusCode);
    }

    [Fact]
    public async Task RoleBasedAuthorizationMatrix_EnforcesUserVsAdminPermissions()
    {
        using var userClient = CreateHttpsClient();
        await CompleteSetupAsync(userClient);

        // Sign in as User
        using var userLoginReq = CreateCsrfSafeRequest(HttpMethod.Post, "/api/auth/login",
            new LoginRequest("user", "UserPassphrase!2026", true));
        var userLoginRes = await userClient.SendAsync(userLoginReq);
        Assert.Equal(HttpStatusCode.OK, userLoginRes.StatusCode);
        Assert.Contains("HttpOnly", string.Join(";", userLoginRes.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);

        // User-allowed routes
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/api/budgets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/api/cashflow")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/api/topology")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/api/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/api/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await userClient.GetAsync("/api/transactions")).StatusCode);

        // User-forbidden AdminOnly routes -> 403
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.GetAsync("/api/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.GetAsync("/api/audit-logs")).StatusCode);

        using var backPropReq = CreateCsrfSafeRequest(HttpMethod.Post, "/api/rules/back-propagate",
            new BackPropagateRequest { DryRun = true });
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.SendAsync(backPropReq)).StatusCode);

        using var bulkDelReq = CreateCsrfSafeRequest(HttpMethod.Post, "/api/transactions/bulk-delete",
            new BulkDeleteTransactionsRequest { TransactionIds = [1] });
        Assert.Equal(HttpStatusCode.Forbidden, (await userClient.SendAsync(bulkDelReq)).StatusCode);

        // Sign in as Admin and verify AdminOnly routes + Step-up check
        using var adminClient = CreateHttpsClient();
        using var adminLoginReq = CreateCsrfSafeRequest(HttpMethod.Post, "/api/auth/login",
            new LoginRequest("admin", "AdminPassphrase!2026", false));
        Assert.Equal(HttpStatusCode.OK, (await adminClient.SendAsync(adminLoginReq)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/audit-logs")).StatusCode);

        // Step-up failure on reset-all with wrong password -> 403
        using var badStepUpReq = CreateCsrfSafeRequest(HttpMethod.Post, "/api/admin/reset-all",
            new ResetAllDatabaseRequest
            {
                CreateBackupFirst = false,
                ReseedDefaultCategoriesAndRules = true,
                ConfirmPassword = "WrongAdminPassword!"
            });
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.SendAsync(badStepUpReq)).StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
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
