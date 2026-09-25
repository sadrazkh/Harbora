using Harbora.Domain.Common;

namespace Harbora.Domain.Identity;

/// <summary>
/// A one-time credential an owner mints so somebody else's browser — in practice, an AI agent driving
/// the panel on their behalf — can sign in as them once, without handing over a password.
///
/// <para>
/// This is <see cref="SupportSession"/>'s architecture turned around: there, a platform administrator
/// borrows a customer's session and the row is what the borrowed access depends on; here, an owner
/// hands a bearer credential to their own agent and this row is what the resulting session depends on.
/// Same discipline either way — the cookie a redemption issues carries nothing but this row's id
/// (<see cref="SessionId"/> plus a claim naming this row), every request under it re-reads the row, and
/// a cookie that outlives the row is inert.
/// </para>
///
/// <para>
/// Two lifetimes live on this one row, anchored at two different moments, because they answer two
/// different questions:
/// </para>
/// <list type="bullet">
/// <item><b>Redeemable until <see cref="ExpiresAt"/></b> (anchored at <see cref="BaseEntity.CreatedAt"/>,
/// via <see cref="SignInTokenAccess.RedemptionLifetime"/>) — the token is a bearer secret in transit,
/// sitting in a chat log or a clipboard until the agent uses it. Short, the way a password-reset link
/// is short, for the same reason.</item>
/// <item><b>Live until <see cref="SessionExpiresAt"/></b> (anchored at <see cref="RedeemedAt"/>, via
/// <see cref="SignInTokenAccess.SessionLifetime"/>) — once redeemed, the session it opened is bounded on
/// its own, exactly like <see cref="SupportAccess.Lifetime"/> bounds a support session. It cannot be
/// written at creation time because redemption — and the clock reading that starts it — has not
/// happened yet.</item>
/// </list>
///
/// <para>
/// Both are written once, never recomputed, for the reason <see cref="SupportSession.ExpiresAt"/>
/// gives: a later change to either constant must not retroactively move an end time the owner (or the
/// agent) was already shown.
/// </para>
/// </summary>
public sealed class SignInToken : BaseEntity
{
    /// <summary>Whose session this grants. Always the creator's own account — a token cannot
    /// elevate — but named separately from <see cref="CreatedByUserId"/> because the two are
    /// conceptually different roles on the row that today happen to coincide.</summary>
    public Guid UserId { get; set; }

    /// <summary>The scope the session opens in.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>SHA-256 hash of the token secret, in the same shape <c>TokenService.Sha256</c> produces
    /// for API/CLI tokens — one hashing scheme for every bearer credential this platform issues, not a
    /// second one invented here. The plaintext is shown exactly once, at creation, and never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Why the owner minted this, in their own words. Required and shown back on the list and
    /// on the banner while the session is live — the same reasoning <see cref="SupportSession.Reason"/>
    /// gives: "a token session is active" tells the owner nothing they can act on.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>The account that minted this token. Equal to <see cref="UserId"/> in every row this
    /// round, since nobody may mint a token naming somebody else's account.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>Written once at creation: <see cref="BaseEntity.CreatedAt"/> +
    /// <see cref="SignInTokenAccess.RedemptionLifetime"/>. Past this instant the token can no longer be
    /// redeemed, whether or not it ever was.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When this token was redeemed. Null until then, and never cleared afterwards — the row
    /// is the receipt.</summary>
    public DateTimeOffset? RedeemedAt { get; set; }

    /// <summary>Where the redemption came from.</summary>
    public string? RedeemedIp { get; set; }

    /// <summary>The <c>UserSession</c> row the redemption opened. What lets revoking this token also
    /// end the session it created, rather than leaving an orphaned browser session nobody can reach
    /// from here.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>Written once, at redemption: <see cref="RedeemedAt"/> +
    /// <see cref="SignInTokenAccess.SessionLifetime"/>. Null until redeemed. This — not the underlying
    /// <c>UserSession.ExpiresAt</c>, which keeps its own ordinary sliding window — is what bounds the
    /// token-opened session the way <see cref="SupportSession.ExpiresAt"/> bounds a support session.</summary>
    public DateTimeOffset? SessionExpiresAt { get; set; }

    /// <summary>When the owner revoked this token by hand. Null unless that is exactly what happened —
    /// an expired-unused or naturally-ended session is recorded through <see cref="EndedBy"/> alone,
    /// not here.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>How this token's story concluded. Null while it is still either unredeemed-and-live or
    /// redeemed-and-live.</summary>
    public SignInTokenEnding? EndedBy { get; set; }

    /// <summary>Whether the session this token opened still authorises anything at <paramref
    /// name="now"/>. False for a token never redeemed — that question belongs to
    /// <see cref="SignInTokenAccess.Expired"/> instead.</summary>
    public bool IsSessionLiveAt(DateTimeOffset now) =>
        RedeemedAt is not null && RevokedAt is null &&
        SessionExpiresAt is { } expires && now < expires;

    /// <summary>The status a list of this owner's tokens shows: revoked beats everything else because
    /// it is the one ending the owner chose; otherwise unredeemed tokens read off
    /// <see cref="SignInTokenAccess.Expired"/> against <see cref="BaseEntity.CreatedAt"/>, and redeemed
    /// ones off <see cref="IsSessionLiveAt"/>.</summary>
    public SignInTokenStatus StatusAt(DateTimeOffset now)
    {
        if (RevokedAt is not null) return SignInTokenStatus.Revoked;
        if (RedeemedAt is null)
            return SignInTokenAccess.Expired(CreatedAt, now)
                ? SignInTokenStatus.Expired
                : SignInTokenStatus.Unredeemed;
        return IsSessionLiveAt(now) ? SignInTokenStatus.RedeemedLive : SignInTokenStatus.Spent;
    }
}

/// <summary>How a <see cref="SignInToken"/>'s story concluded. Recorded because "revoked", "ran out
/// unused" and "its session ended" read differently to an owner checking what happened — the same
/// distinction <see cref="SupportSessionEnding"/> makes for a support session.</summary>
public enum SignInTokenEnding
{
    /// <summary>The owner pressed "revoke", whether before or after the token was redeemed.</summary>
    Revoked = 0,

    /// <summary>Nobody redeemed it before <see cref="SignInToken.ExpiresAt"/> ran out.</summary>
    ExpiredUnused = 1,

    /// <summary>It was redeemed, and the session it opened ran its own course to
    /// <see cref="SignInToken.SessionExpiresAt"/> without being revoked first.</summary>
    SessionEnded = 2
}

/// <summary>The five states a token can show on the owner's own list — see
/// <see cref="SignInToken.StatusAt"/>.</summary>
public enum SignInTokenStatus
{
    Unredeemed,
    RedeemedLive,
    Spent,
    Revoked,
    Expired
}

/// <summary>
/// The rules of a one-time sign-in token, pure and shaped like <see cref="SupportAccess"/> beside it —
/// so the refusals below can be read, and tested, without a request or a database.
/// </summary>
public static class SignInTokenAccess
{
    /// <summary>
    /// How long the token itself stays redeemable. Short: it is a bearer credential that travels in
    /// plain text — a clipboard, a chat log handed to an agent — so the window it can still be used in
    /// is kept to about as long as it takes to actually hand it over and have the agent use it, not the
    /// length of the work the agent will then go on to do.
    /// </summary>
    public static readonly TimeSpan RedemptionLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long the session a token opens stays live once redeemed. This platform already has a
    /// vocabulary for "a bounded, revocable, per-request-checked session" — <see
    /// cref="SupportAccess.Lifetime"/> — and an agent let loose in the panel to finish a task and be
    /// done is the same shape of access as a support engineer borrowing an hour, not the shape of an
    /// ordinary seven-day, sliding-expiry sign-in. Reusing the constant is deliberate, not incidental —
    /// a test pins the two together so they cannot silently drift apart, the same way this platform's
    /// own <c>SupportAccessTests</c> pins <see cref="SupportAccess.Lifetime"/> to <c>AdminerSession</c>.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = SupportAccess.Lifetime;

    /// <summary>The longest a purpose may be. Matches <see cref="SupportAccess.MaxReasonLength"/> — the
    /// same banner-width reasoning applies to both.</summary>
    public const int MaxPurposeLength = SupportAccess.MaxReasonLength;

    /// <summary>Whether a token created at <paramref name="createdAt"/> can still be redeemed at
    /// <paramref name="now"/>. The clock is a parameter for the usual reason: a rule that reads it
    /// cannot be tested at the boundary where it matters.</summary>
    public static bool Expired(DateTimeOffset createdAt, DateTimeOffset now) =>
        now - createdAt >= RedemptionLifetime;

    /// <summary>
    /// Null when a token may be minted with these arguments; otherwise why not, in words the owner can
    /// act on. <paramref name="creatorIsWorkspaceMember"/> is supplied by the caller after the one
    /// database read this needs — the same shape <see cref="SupportAccess.RefuseStart"/> takes its
    /// membership and active-account facts in.
    /// </summary>
    public static string? RefuseCreate(bool creatorIsWorkspaceMember, string? purpose)
    {
        if (!creatorIsWorkspaceMember)
            return "You are not a member of that workspace, so there is nothing to open a session in.";

        if (string.IsNullOrWhiteSpace(purpose))
            return "Say what this token is for. You will see this sentence on the list, and the agent's banner shows it too.";

        if (purpose.Trim().Length > MaxPurposeLength)
            return $"Keep the purpose under {MaxPurposeLength} characters — it is shown on a banner.";

        return null;
    }

    /// <summary>
    /// Null when a presented token may be redeemed; otherwise why not. <paramref name="alreadyRedeemed"/>
    /// covers both an honest second use and the losing side of two concurrent redemptions — the two
    /// read as one sentence on purpose, because a redemption that lost a race is not a different kind
    /// of failure from one that arrived after the token was already spent.
    /// </summary>
    public static string? RefuseRedeem(bool alreadyRedeemed, bool isRevoked, bool isExpired)
    {
        if (isRevoked) return "This token has been revoked.";
        if (alreadyRedeemed) return "This token has already been used.";
        if (isExpired) return "This token has expired.";
        return null;
    }

    /// <summary>What an unrecognised token — one whose hash matches no row at all — is refused with.
    /// The same sentence a spent one gets, deliberately: telling the two apart would tell somebody
    /// guessing whether a hash they do not hold ever existed.</summary>
    public const string UnknownTokenMessage = "This token is not valid.";
}
