using System.Security.Cryptography;
using System.Text;

namespace Harbora.Infrastructure.Ai;

/// <summary>A new key. The secret exists here and nowhere else afterwards.</summary>
public sealed record IssuedApiKey(string Secret, string Prefix, string Hash);

/// <summary>
/// Harbora's own API keys for the AI gateway.
///
/// The customer holds one of these and never a provider token. That is the whole point of the
/// gateway: revoking here really revokes, whereas a provider token handed to a customer keeps
/// working in whatever environment file it was pasted into, and bills whoever owns it.
///
/// Only the hash is stored. A key that can be read back from the panel is a key that leaks through a
/// support screenshot, a screen share, or a database backup.
/// </summary>
public static class AiApiKeys
{
    /// <summary>
    /// Identifiable on sight, so a key found in a log or a paste can be recognised as Harbora's and
    /// reported. Secret scanners key off exactly this kind of prefix.
    /// </summary>
    public const string Prefix = "har_";

    /// <summary>Characters shown in the panel to tell keys apart. Useless on its own.</summary>
    private const int VisibleChars = 6;

    private const int SecretBytes = 32;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;

    private const string Alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static IssuedApiKey Create()
    {
        var body = Random(SecretBytes);
        var secret = Prefix + body;

        return new IssuedApiKey(secret, secret[..(Prefix.Length + VisibleChars)], Hash(secret));
    }

    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Whether a presented key matches. Constant-time: a comparison that returns early tells an
    /// attacker how much of their guess was right, one character at a time.
    /// </summary>
    public static bool Verify(string presented, string stored)
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
            Encoding.UTF8.GetBytes(presented), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// The prefix of a presented key, for narrowing a lookup to one row.
    ///
    /// Without this every authenticated request would hash the candidate against every key in the
    /// table — which is both slow and a way to make the server do unbounded work on request.
    /// Returns null for anything that is not shaped like one of our keys.
    /// </summary>
    public static string? PrefixOf(string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented)) return null;
        if (!presented.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        if (presented.Length < Prefix.Length + VisibleChars) return null;

        return presented[..(Prefix.Length + VisibleChars)];
    }

    /// <summary>
    /// Reads a bearer token out of an Authorization header.
    ///
    /// Deliberately strict. A header parsed loosely is a header an attacker can shape, and this one
    /// decides who the request belongs to.
    /// </summary>
    public static string? FromAuthorizationHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;

        var value = header[scheme.Length..].Trim();
        return value.Length == 0 ? null : value;
    }

    private static string Random(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return new string(chars);
    }
}
