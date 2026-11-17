using Dapper;
using HomeBudget.Web.Data;
using HomeBudget.Web.Models;

namespace HomeBudget.Web.Services;

public sealed class SecurityAuditService(
    SqliteConnectionFactory connectionFactory,
    ILogger<SecurityAuditService> logger)
{
    public const int MaxRetainedEntries = 10_000;
    public const int MaxRetainedDays = 365;

    public async Task LogEventAsync(
        string eventType,
        string outcome,
        int? actorUserId = null,
        string? actorUsername = null,
        string? targetUsername = null,
        string? sourceIp = null,
        string? details = null,
        CancellationToken ct = default)
    {
        string safeEventType = string.IsNullOrWhiteSpace(eventType) ? "UNKNOWN" : eventType.Trim().ToUpperInvariant();
        string safeOutcome = string.IsNullOrWhiteSpace(outcome) ? "Info" : outcome.Trim();
        string? safeDetails = SanitizeDetails(details);

        try
        {
            await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
            await conn.ExecuteAsync(
                """
                INSERT INTO SecurityAuditLogs (
                    TimestampUtc, EventType, Outcome, ActorUserId, ActorUsername, TargetUsername, SourceIp, Details
                ) VALUES (
                    datetime('now'), @EventType, @Outcome, @ActorUserId, @ActorUsername, @TargetUsername, @SourceIp, @Details
                );
                """,
                new
                {
                    EventType = safeEventType,
                    Outcome = safeOutcome,
                    ActorUserId = actorUserId,
                    ActorUsername = actorUsername,
                    TargetUsername = targetUsername,
                    SourceIp = sourceIp,
                    Details = safeDetails
                });

            await conn.ExecuteAsync(
                """
                DELETE FROM SecurityAuditLogs
                WHERE TimestampUtc < datetime('now', @DaysModifier)
                   OR AuditLogId NOT IN (
                       SELECT AuditLogId FROM SecurityAuditLogs ORDER BY AuditLogId DESC LIMIT @MaxRows
                   );
                """,
                new
                {
                    DaysModifier = $"-{MaxRetainedDays} days",
                    MaxRows = MaxRetainedEntries
                });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist security audit log entry {EventType}", safeEventType);
        }

        logger.LogInformation(
            "SecurityAudit Event={EventType} Outcome={Outcome} Actor={Actor} Target={Target} Ip={Ip} Details={Details}",
            safeEventType,
            safeOutcome,
            actorUsername ?? "-",
            targetUsername ?? "-",
            sourceIp ?? "-",
            safeDetails ?? "-");
    }

    public async Task<AuditLogResponseDto> QueryLogsAsync(
        int limit = 50,
        int offset = 0,
        string? eventType = null,
        CancellationToken ct = default)
    {
        int safeLimit = Math.Clamp(limit, 1, 200);
        int safeOffset = Math.Max(0, offset);
        string? filterType = string.IsNullOrWhiteSpace(eventType) ? null : eventType.Trim().ToUpperInvariant();

        await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct);
        int totalCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM SecurityAuditLogs
            WHERE (@EventType IS NULL OR EventType = @EventType);
            """,
            new { EventType = filterType });

        var items = (await conn.QueryAsync<SecurityAuditLog>(
            """
            SELECT AuditLogId, TimestampUtc, EventType, Outcome, ActorUserId, ActorUsername, TargetUsername, SourceIp, Details
            FROM SecurityAuditLogs
            WHERE (@EventType IS NULL OR EventType = @EventType)
            ORDER BY AuditLogId DESC
            LIMIT @Limit OFFSET @Offset;
            """,
            new { EventType = filterType, Limit = safeLimit, Offset = safeOffset })).ToList();

        return new AuditLogResponseDto(totalCount, items);
    }

    public static string? SanitizeDetails(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string cleaned = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        if (cleaned.Length > 240)
        {
            cleaned = cleaned[..240];
        }
        return cleaned;
    }
}
