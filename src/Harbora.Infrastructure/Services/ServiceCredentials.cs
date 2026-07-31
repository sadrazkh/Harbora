using System.Security.Cryptography;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Passwords for managed databases.
///
/// Kept in one place because the rule is not "random enough" but "random and still safe to put
/// through a shell and a SQL statement". A generated password ends up inside
/// <c>ALTER USER … PASSWORD '…'</c>, and one stray quote there ends the statement early.
/// </summary>
public static class ServiceCredentials
{
    private const string Alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// A password with no characters that need escaping anywhere. Ambiguous glyphs (l, I, 0, O) are
    /// left out because these get read aloud and typed by hand.
    /// </summary>
    public static string Generate(int length = 24)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}
