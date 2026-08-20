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
///
/// <para>
/// One further thing happens here and nowhere else: when the request is running under a support
/// session, every row it writes is stamped with both ids and its action gains a <c>support.</c>
/// prefix. Doing it centrally is the whole point — no caller has to remember, so no caller can
/// forget, and a support session cannot perform an audited act that reads as the customer's own.
/// </para>
/// </summary>
public sealed class AuditLogger(
    HarboraDbContext db,
    ICurrentUser currentUser,
    ISystemClock clock,
    ISupportSession support,
    ILogger<AuditLogger> logger) : IAuditLogger
{
    /// <summary>
    /// What every action performed under a support session is called instead. Applied once: an
    /// action that already names itself support keeps its name rather than becoming
    /// <c>support.support.…</c>.
    /// </summary>
    public const string SupportPrefix = "support.";

    /// <summary>The action string an entry would be written under, given who is really acting.</summary>
    public static string ActionUnderSupport(string action, bool underSupport) =>
        underSupport && !action.StartsWith(SupportPrefix, StringComparison.Ordinal)
            ? SupportPrefix + action
            : action;

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
                // Still the customer's account: that is who the request ran as, and a row claiming
                // otherwise would misattribute every ordinary act back to the administrator.
                UserId = userIdOverride ?? currentUser.UserId,
                ActorEmail = actorEmailOverride ?? currentUser.Email ?? "anonymous",
                Action = ActionUnderSupport(action, support.IsActive),
                TargetType = targetType,
                TargetId = targetId,
                IpAddress = ipAddress,
                MetadataJson = metadataJson,
                SupportSessionId = support.SessionId,
                SupportAdminUserId = support.AdminUserId,
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
