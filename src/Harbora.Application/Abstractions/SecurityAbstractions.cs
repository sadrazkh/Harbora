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
