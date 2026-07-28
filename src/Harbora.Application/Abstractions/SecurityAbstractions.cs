namespace Harbora.Application.Abstractions;

/// <summary>Symmetric encryption for secrets at rest (env secrets, tokens, credentials).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
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

/// <summary>Removes known secret values from a string before it is logged or displayed.</summary>
public interface ISecretRedactor
{
    string Redact(string text, IEnumerable<string> secretValues);
}

/// <summary>
/// Append-only audit trail for security-relevant actions (doc 10 §2.13). The actor/workspace
/// default to the current user; callers pass the request IP (the abstraction stays free of any
/// web dependency). Best-effort — an audit failure must never break the action being audited.
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
        CancellationToken ct = default);
}
