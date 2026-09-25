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
/// A session opened by a <c>SignInToken</c> gets the identical treatment, one layer down: a
/// <c>token.</c> prefix and a <see cref="Domain.Auditing.AuditLog.SignInTokenId"/> stamp, so an
/// agent's act reads apart from the owner's own act just as a support engineer's does.
/// </para>
/// </summary>
public sealed class AuditLogger(
    HarboraDbContext db,
    ICurrentUser currentUser,
    ISystemClock clock,
    ISupportSession support,
    ILogger<AuditLogger> logger,
    ISignInTokenSession? signInToken = null) : IAuditLogger
{
    /// <summary>
    /// What every action performed under a support session is called instead. Applied once: an
    /// action that already names itself support keeps its name rather than becoming
    /// <c>support.support.…</c>.
    /// </summary>
    public const string SupportPrefix = "support.";

    /// <summary>The analogous prefix for a session a <see cref="SignInToken"/> opened — see
    /// <see cref="SupportPrefix"/>'s own remark; the same idempotence applies.</summary>
    public const string TokenPrefix = "token.";

    /// <summary>The action string an entry would be written under, given who is really acting.</summary>
    public static string ActionUnderSupport(string action, bool underSupport) =>
        underSupport && !action.StartsWith(SupportPrefix, StringComparison.Ordinal)
            ? SupportPrefix + action
            : action;

    /// <summary>The token-session counterpart of <see cref="ActionUnderSupport"/>.</summary>
    public static string ActionUnderSignInToken(string action, bool underToken) =>
        underToken && !action.StartsWith(TokenPrefix, StringComparison.Ordinal)
            ? TokenPrefix + action
            : action;

    /// <summary>Defaults to "nobody" for every call site that constructs this directly (every existing
    /// test) rather than through DI — the same accommodation <paramref name="support"/> does not need
    /// only because it predates this feature and every call site already supplies it.</summary>
    private readonly ISignInTokenSession _tokenSession = signInToken ?? NoSignInTokenSession.Instance;

    public async Task LogAsync(
        string action,
        string? targetType = null,
        string? targetId = null,
        string? ipAddress = null,
        string? actorEmailOverride = null,
        Guid? userIdOverride = null,
        string? metadataJson = null,
        Guid? workspaceId = null,
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
                Action = ActionUnderSignInToken(
                    ActionUnderSupport(action, support.IsActive), _tokenSession.IsActive),
                TargetType = targetType,
                TargetId = targetId,
                IpAddress = ipAddress,
                MetadataJson = metadataJson,
                SupportSessionId = support.SessionId,
                SupportAdminUserId = support.AdminUserId,
                SignInTokenId = _tokenSession.SignInTokenId,
                // Exactly what the caller passed — never ICurrentUser.WorkspaceId as a fallback. See
                // IAuditLogger.LogAsync's own remark: this sink has no way to tell "the caller forgot"
                // from "this action genuinely has no workspace", so it does not guess either way.
                WorkspaceId = workspaceId,
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
