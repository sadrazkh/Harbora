using Harbora.Domain.Common;

namespace Harbora.Domain.Identity;

/// <summary>
/// One period during which a platform administrator was signed in as one customer's user.
///
/// <para>
/// The owner rejected silent support access outright, so this row is not a log written after the
/// fact — it is the thing the access itself depends on. The cookie a support session issues carries
/// nothing but this row's id; every request under it re-reads the row and stops the moment the row
/// says stop. A cookie that outlives its row is inert, which is the only reason a one-hour lifetime
/// means anything at all.
/// </para>
///
/// <para>
/// <see cref="Reason"/> is required and free text on purpose. "Support signed in as you" is not an
/// answer a customer can act on; "support signed in as you to reproduce the failing deploy on shop"
/// is. It is shown to the customer on the banner and on their own support-access page, so it is
/// written for them rather than for an internal ticket queue.
/// </para>
/// </summary>
public sealed class SupportSession : BaseEntity
{
    /// <summary>The platform administrator who started it. Never cleared — the row is the receipt.</summary>
    public Guid AdminUserId { get; set; }

    /// <summary>
    /// The administrator's address at the time, copied rather than joined. The customer's own page
    /// must still name who this was after the account is renamed, disabled or deleted, and a join
    /// that returns nothing would show them a support session with nobody attached to it.
    /// </summary>
    public string AdminEmail { get; set; } = string.Empty;

    /// <summary>The customer's user the administrator was signed in as.</summary>
    public Guid TargetUserId { get; set; }

    /// <summary>
    /// The workspace the session was opened against. This is what scopes the customer's own view of
    /// it: <see cref="SupportSession"/> carries no global query filter, so an explicit
    /// <c>TargetWorkspaceId ==</c> is the only tenant protection on every read of this table.
    /// </summary>
    public Guid TargetWorkspaceId { get; set; }

    /// <summary>Why, in the operator's own words. Required; shown to the customer verbatim.</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// <see cref="StartedAt"/> + <see cref="SupportAccess.Lifetime"/>, written once at the start.
    /// Stored rather than recomputed so a later change to the lifetime cannot retroactively extend
    /// or shorten a session a customer was already told the end time of.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when the session stopped, whichever way it stopped. Null while it is live.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>How it stopped. Null while it is live.</summary>
    public SupportSessionEnding? EndedBy { get; set; }

    /// <summary>Where the administrator was, for the platform side of the audit.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Whether this row still authorises anything at <paramref name="now"/>.</summary>
    public bool IsLiveAt(DateTimeOffset now) => EndedAt is null && !SupportAccess.Expired(StartedAt, now);
}

/// <summary>How a support session stopped. Recorded because "ended" and "ran out" read differently.</summary>
public enum SupportSessionEnding
{
    /// <summary>The person pressed the button on the banner.</summary>
    EndedByOperator = 0,

    /// <summary>Nobody pressed anything; the hour ran out and a request found the row past its end.</summary>
    Expired = 1
}

/// <summary>
/// The rules of a borrowed customer session.
///
/// <para>
/// Pure, and deliberately shaped like <c>AdminerSession</c> in the infrastructure layer — that class
/// is this platform's existing vocabulary for temporary borrowed access, down to the one-hour span
/// and the <c>Expired(startedAt, now)</c> comparison with the clock passed in. This is the same
/// value expressed where the domain can reach it, not a second notion of "temporary", and
/// <c>SupportAccessTests</c> pins the two together so they cannot drift apart.
/// </para>
/// </summary>
public static class SupportAccess
{
    /// <summary>
    /// How long a support session lives. Long enough to reproduce a customer's problem, short enough
    /// that forgetting about it is not a standing exposure — and it is enforced on every request
    /// against the row, not by asking the cookie what it thinks.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether a session that started at <paramref name="startedAt"/> is past its life. The clock is
    /// a parameter for the usual reason: a rule that reads it cannot be tested at the boundary where
    /// it matters.
    /// </summary>
    public static bool Expired(DateTimeOffset startedAt, DateTimeOffset now) =>
        now - startedAt >= Lifetime;

    /// <summary>The longest a reason may be. Long enough for a sentence, short enough for a banner.</summary>
    public const int MaxReasonLength = 280;

    /// <summary>
    /// Null when a session may be started with these arguments; otherwise why not, in words the
    /// administrator can act on. Pure, so the refusals can be read without a request.
    /// </summary>
    public static string? RefuseStart(Guid adminUserId, Guid targetUserId, bool targetIsActive,
        bool targetIsMember, string? reason)
    {
        if (adminUserId == targetUserId)
            return "You are already signed in as this account.";

        if (!targetIsMember)
            return "That person is not a member of this workspace, so there is nothing to sign in to.";

        if (!targetIsActive)
            return "That account is suspended. Reactivate it first, or sign in as somebody who can still use the panel.";

        if (string.IsNullOrWhiteSpace(reason))
            return "Say why you are signing in as this customer. They will be shown this sentence while you are.";

        if (reason.Trim().Length > MaxReasonLength)
            return $"Keep the reason under {MaxReasonLength} characters — the customer reads it on a banner.";

        return null;
    }
}
