using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Auditing;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Auditing;

/// <summary>
/// Writes append-only <see cref="AuditLog"/> rows for privileged actions (doc 10 §2.13). Actor and
/// workspace default to <see cref="ICurrentUser"/>; the request IP is supplied by the caller so
/// this stays free of any web dependency. Failures are logged, never thrown — auditing must not
/// break the action it records.
/// </summary>
public sealed class AuditLogger(
    HarboraDbContext db,
    ICurrentUser currentUser,
    ISystemClock clock,
    ILogger<AuditLogger> logger) : IAuditLogger
{
    public async Task LogAsync(
        string action,
        string? targetType = null,
        string? targetId = null,
        string? ipAddress = null,
        string? actorEmailOverride = null,
        Guid? userIdOverride = null,
        string? metadataJson = null,
        CancellationToken ct = default)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                UserId = userIdOverride ?? currentUser.UserId,
                ActorEmail = actorEmailOverride ?? currentUser.Email ?? "anonymous",
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                IpAddress = ipAddress,
                MetadataJson = metadataJson,
                CreatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit entry for action {Action}.", action);
        }
    }
}
