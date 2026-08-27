namespace Harbora.Application.Abstractions;

/// <summary>Symmetric encryption for secrets at rest (env secrets, tokens, credentials).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);

    /// <summary>
    /// A stable 32-byte key derived from the master key for a named purpose (HKDF). Deterministic by
    /// contract: callers encrypt with it now and must decrypt with it later.
    /// <para>
    /// Do NOT derive a key by hashing <see cref="Protect"/>'s output — it uses a fresh nonce per
    /// call, so it returns something different every time. Backup archive encryption did exactly
    /// that and produced an archive nothing could ever decrypt.
    /// </para>
    /// </summary>
    byte[] DeriveKey(string purpose);
}

/// <summary>Password hashing (PBKDF2). Kept behind an interface so it can be swapped for Argon2.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

/// <summary>Creates and validates API/CLI tokens. Only hashes are persisted.</summary>
public interface ITokenService
{
    NewToken Issue(Guid userId, string name, Domain.Common.TokenType type, TimeSpan? lifetime);
    /// <summary>Returns the userId if the presented token is valid, else null.</summary>
    Task<Guid?> ValidateAsync(string presentedToken, CancellationToken ct);
}

public record NewToken(string Prefix, string PlaintextToken, string Hash);

/// <summary>Ambient info about the caller, resolved from cookie or bearer token.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
    Guid? WorkspaceId { get; }
}

/// <summary>
/// Whether this request is running under a platform support session, and whose.
///
/// <para>
/// Separate from <see cref="ICurrentUser"/> on purpose: under impersonation the current user IS the
/// customer, and every tenancy decision in the platform must go on believing that. This answers the
/// different question of who is really at the keyboard, which only the audit trail, the banner and
/// the small set of refused acts have any business asking.
/// </para>
///
/// <para>
/// The values here come off the cookie's claims, so they say what the session was issued saying.
/// They are enough to label a row and draw a banner; they are NOT the authority on whether the
/// session is still alive. That is the row, re-read on every request.
/// </para>
/// </summary>
public interface ISupportSession
{
    /// <summary>The <c>SupportSession</c> row's id, or null when nobody is impersonating.</summary>
    Guid? SessionId { get; }

    /// <summary>The platform administrator behind the session.</summary>
    Guid? AdminUserId { get; }

    /// <summary>Their address, for an audit row that has to be readable a year later.</summary>
    string? AdminEmail { get; }

    /// <summary>Whether a support session is in force at all.</summary>
    bool IsActive => SessionId is not null;
}

/// <summary>
/// The answer everywhere that is not a browser request: nobody is impersonating. Background jobs,
/// the CLI and the deploy pipeline have no claims to read and must never be labelled as support.
/// </summary>
public sealed class NoSupportSession : ISupportSession
{
    public static readonly NoSupportSession Instance = new();
    public Guid? SessionId => null;
    public Guid? AdminUserId => null;
    public string? AdminEmail => null;
}

/// <summary>Removes known secret values from a string before it is logged or displayed.</summary>
public interface ISecretRedactor
{
    string Redact(string text, IEnumerable<string> secretValues);
}

/// <summary>
/// Append-only audit trail for security-relevant actions (doc 10 §2.13). The actor defaults to the
/// current user; callers pass the request IP (the abstraction stays free of any web dependency).
/// Best-effort — an audit failure must never break the action being audited.
///
/// <para>
/// <paramref name="LogAsync.workspaceId"/> is the opposite of a default: this sink never fills it in
/// from <c>ICurrentUser.WorkspaceId</c> on the caller's behalf (HARBORA-0056). Doing that centrally
/// would be convenient exactly once and wrong every time a caller runs with a workspace ambient in
/// the request but is recording a platform-level act — a node enrolled, a platform setting changed,
/// a sign-in before any workspace was chosen — which would silently mislabel the row as belonging to
/// whichever workspace the caller happened to be in. Every call site decides for itself, explicitly:
/// the resource's own workspace when the action plainly has one, or <c>null</c>, named as such, when
/// it plainly does not.
/// </para>
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(
        string action,
        string? targetType = null,
        string? targetId = null,
        string? ipAddress = null,
        string? actorEmailOverride = null,
        Guid? userIdOverride = null,
        string? metadataJson = null,
        Guid? workspaceId = null,
        CancellationToken ct = default);
}
