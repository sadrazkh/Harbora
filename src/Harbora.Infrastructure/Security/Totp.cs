using System.Security.Cryptography;
using System.Text;

namespace Harbora.Infrastructure.Security;

/// <summary>
/// RFC 6238 time-based one-time passwords, plus the recovery codes that stand in for a lost phone.
///
/// Hand-rolled rather than a package: the algorithm is forty lines of HMAC-SHA1 with published
/// test vectors, and an authentication dependency is the last place to take on somebody else's
/// update cadence.
///
/// Everything time-dependent takes the moment as a parameter. This codebase has already paid for
/// tests pinned to a wall clock, and a verifier that reads the clock itself cannot be tested at
/// the step boundaries where its bugs live.
/// </summary>
public static class Totp
{
    /// <summary>RFC 6238 defaults, which is what every authenticator app ships speaking.</summary>
    private const int StepSeconds = 30;
    private const int Digits = 6;

    /// <summary>
    /// One step either side. The user's clock and ours disagree by a few seconds as a matter of
    /// course; more than one step of grace turns "a moment ago" into "ninety seconds of validity".
    /// </summary>
    private const long Window = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A fresh 160-bit secret, base32 — the format authenticator apps take by hand.</summary>
    public static string GenerateSecret() => ToBase32(RandomNumberGenerator.GetBytes(20));

    /// <summary>
    /// Whether the code is right for this secret at this moment, one step of grace either side.
    /// Null and malformed input is a plain no — the login page hands this whatever was typed.
    /// </summary>
    public static bool Verify(string secret, string? code, DateTimeOffset now)
    {
        if (code is null) return false;

        var trimmed = code.Replace(" ", "");
        if (trimmed.Length != Digits || !trimmed.All(char.IsAsciiDigit)) return false;

        byte[] key;
        try { key = FromBase32(secret); }
        catch (FormatException) { return false; }

        var step = now.ToUnixTimeSeconds() / StepSeconds;

        for (var offset = -Window; offset <= Window; offset++)
        {
            if (CodeAt(key, step + offset) == trimmed) return true;
        }

        return false;
    }

    /// <summary>The code for one time step — exposed for the RFC's own test vectors.</summary>
    public static string CodeAt(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);

        var hash = HMACSHA1.HashData(key, counter);

        // RFC 4226 dynamic truncation: the low nibble of the last byte picks the offset.
        var at = hash[^1] & 0x0F;
        var binary = ((hash[at] & 0x7F) << 24) | (hash[at + 1] << 16) | (hash[at + 2] << 8) | hash[at + 3];

        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>The URI an authenticator app understands, shown as text and as a tappable link.</summary>
    public static string OtpauthUri(string issuer, string account, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";

    // ---- recovery codes ----

    /// <summary>
    /// Ten codes shown exactly once; only their hashes are stored. Grouped as xxxx-xxxx because a
    /// person will read these off paper years from now.
    /// </summary>
    public static IReadOnlyList<string> IssueRecoveryCodes()
    {
        var codes = new List<string>(10);
        for (var i = 0; i < 10; i++)
        {
            var raw = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            codes.Add($"{raw[..4]}-{raw[4..]}");
        }
        return codes;
    }

    /// <summary>The stored shape of a recovery code — same discipline as the reset tokens.</summary>
    public static string HashRecoveryCode(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToLowerInvariant())));

    /// <summary>The stored JSON for a fresh set of codes.</summary>
    public static string StoreRecoveryCodes(IEnumerable<string> codes) =>
        System.Text.Json.JsonSerializer.Serialize(codes.Select(HashRecoveryCode));

    /// <summary>
    /// Spend a recovery code: true plus the remaining set when it matched, false and the stored
    /// set unchanged when it did not. The caller persists the remainder before anything else
    /// happens, so the same code read off the same sheet never works twice.
    /// </summary>
    public static (bool Ok, string? RemainingJson) ConsumeRecoveryCode(string? storedJson, string presented)
    {
        if (string.IsNullOrWhiteSpace(storedJson) || string.IsNullOrWhiteSpace(presented))
            return (false, storedJson);

        List<string>? hashes;
        try { hashes = System.Text.Json.JsonSerializer.Deserialize<List<string>>(storedJson); }
        catch (System.Text.Json.JsonException) { return (false, storedJson); }
        if (hashes is null) return (false, storedJson);

        var hash = HashRecoveryCode(presented);
        return hashes.Remove(hash)
            ? (true, System.Text.Json.JsonSerializer.Serialize(hashes))
            : (false, storedJson);
    }

    // ---- base32, because that is the format the apps take ----

    public static string ToBase32(byte[] data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result.Append(Base32Alphabet[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0) result.Append(Base32Alphabet[(buffer << (5 - bits)) & 0x1F]);
        return result.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        var cleaned = text.Trim().TrimEnd('=').ToUpperInvariant();
        var result = new List<byte>(cleaned.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;

        foreach (var c in cleaned)
        {
            var value = Base32Alphabet.IndexOf(c);
            if (value < 0) throw new FormatException($"'{c}' is not base32.");

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                result.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return [.. result];
    }
}
