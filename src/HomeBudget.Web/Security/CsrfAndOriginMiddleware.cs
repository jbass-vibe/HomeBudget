using HomeBudget.Web.Services;

namespace HomeBudget.Web.Security;

public sealed class CsrfAndOriginMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> MutatingMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Delete,
        HttpMethods.Patch
    };

    public async Task InvokeAsync(HttpContext context, SecurityAuditService auditService)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
            MutatingMethods.Contains(context.Request.Method))
        {
            string? sourceIp = context.Connection.RemoteIpAddress?.ToString();

            // 1. Validate Origin (or fallback Referer) matches Request Scheme + Host
            string expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
            string? origin = context.Request.Headers.Origin.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(origin))
            {
                if (!string.Equals(origin.TrimEnd('/'), expectedOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    await auditService.LogEventAsync("CSRF_REJECTED", "Denied", sourceIp: sourceIp, details: $"Mismatched Origin header on {context.Request.Path}", ct: context.RequestAborted);
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "Cross-origin request rejected." });
                    return;
                }
            }
            else
            {
                string? referer = context.Request.Headers.Referer.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(referer) &&
                    Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
                {
                    string refererOrigin = $"{refererUri.Scheme}://{refererUri.Authority}";
                    if (!string.Equals(refererOrigin, expectedOrigin, StringComparison.OrdinalIgnoreCase))
                    {
                        await auditService.LogEventAsync("CSRF_REJECTED", "Denied", sourceIp: sourceIp, details: $"Mismatched Referer header on {context.Request.Path}", ct: context.RequestAborted);
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new { error = "Cross-origin referer rejected." });
                        return;
                    }
                }
            }

            // 2. Require custom CSRF header on all mutating /api/* calls (including login, setup, logout, uploads)
            string? csrfHeader = context.Request.Headers["X-HomeBudget-Request"].FirstOrDefault();
            if (!string.Equals(csrfHeader, "1", StringComparison.Ordinal))
            {
                await auditService.LogEventAsync("CSRF_REJECTED", "Denied", sourceIp: sourceIp, details: $"Missing X-HomeBudget-Request header on {context.Request.Path}", ct: context.RequestAborted);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Missing required CSRF protection header." });
                return;
            }
        }

        await next(context);
    }
}
