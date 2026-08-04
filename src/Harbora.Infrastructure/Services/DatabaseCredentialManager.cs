using System.Security.Cryptography;
using System.Text;

namespace Harbora.Infrastructure.Services;

/// <summary>A freshly minted login. The password exists here and nowhere else afterwards.</summary>
/// <param name="Username">Unique per grant, so revoking one cannot affect another.</param>
/// <param name="Password">Shown once. Only <paramref name="PasswordHash"/> is persisted.</param>
public sealed record GeneratedCredential(string Username, string Password, string PasswordHash);

/// <summary>
/// Makes and checks the logins handed out for external database access.
///
/// The password is returned once and never stored. What is stored is a salted hash, which means a
/// leaked copy of Harbora's own database does not also hand over live logins into every customer
/// database — the failure that turns one breach into all of them.
///
/// Each grant gets its own username. Sharing one login across grants would make "revoke this
/// access" impossible to honour without cutting off everybody else using it.
/// </summary>
public static class DatabaseCredentialManager
{
    /// <summary>
    /// Long enough that guessing is not a strategy, and drawn from an alphabet with no characters
    /// that break a connection string or vanish when pasted through a shell.
    /// </summary>
    private const int PasswordLength = 32;

    private const string Alphabet =
        "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;

    /// <summary>
    /// A login for one grant. The username carries a short random suffix rather than a counter:
    /// a predictable name tells an attacker what to try next.
    /// </summary>
    public static GeneratedCredential Create(string prefix = "harbora")
    {
        var suffix = RandomString(8).ToLowerInvariant();
        var username = $"{Clean(prefix)}_{suffix}";
        var password = RandomString(PasswordLength);

        return new GeneratedCredential(username, password, Hash(password));
    }

    /// <summary>PBKDF2, stored as <c>iterations.salt.hash</c> in base64.</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Whether a presented password matches. Compared in constant time: a comparison that returns
    /// early leaks how much of the guess was right.
    /// </summary>
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iterations) || iterations <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException) { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// The connection string a person copies. Built here so the password appears in exactly one
    /// place in the codebase and can be kept out of everything else.
    /// </summary>
    public static string ConnectionString(
        string engine, string host, int port, string username, string password, string database)
    {
        var user = Uri.EscapeDataString(username);
        var secret = Uri.EscapeDataString(password);

        return engine.ToLowerInvariant() switch
        {
            "postgresql" or "postgres" => $"postgresql://{user}:{secret}@{host}:{port}/{database}",
            "mysql" or "mariadb" => $"mysql://{user}:{secret}@{host}:{port}/{database}",
            "mongodb" => $"mongodb://{user}:{secret}@{host}:{port}/{database}",
            "redis" => $"redis://:{secret}@{host}:{port}",
            _ => $"{engine.ToLowerInvariant()}://{user}:{secret}@{host}:{port}/{database}"
        };
    }

    private static string RandomString(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return new string(chars);
    }

    /// <summary>Database identifiers are not free-form; anything else becomes an underscore.</summary>
    private static string Clean(string value)
    {
        var cleaned = new string(value.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');

        return cleaned.Length == 0 ? "harbora" : cleaned[..Math.Min(16, cleaned.Length)];
    }
}
