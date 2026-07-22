namespace Harbora.Infrastructure.Security;

/// <summary>
/// Resolves the master encryption key with a <b>fail-closed</b> policy: in Production a key MUST be
/// supplied (an unset key would make every "encrypted" secret trivially decryptable). Only the
/// Development environment may fall back to a well-known insecure dev key, and only with a loud
/// warning. See ADR-009 and the threat model (doc 10 §2.2).
/// </summary>
public static class MasterKeyResolver
{
    /// <summary>Well-known, INSECURE key used only in Development when none is configured.</summary>
    public const string DevFallbackKey = "dev-insecure-master-key-change-me";

    /// <summary>
    /// Keys we must never accept in Production: the dev fallback and the placeholder that used to
    /// ship in appsettings.json. Guards against an operator inheriting an insecure default.
    /// </summary>
    private static readonly HashSet<string> KnownInsecureKeys = new(StringComparer.Ordinal)
    {
        DevFallbackKey,
        "dev-insecure-master-key-change-me-in-production",
    };

    public sealed record Result(string Key, bool UsedDevFallback);

    /// <param name="configuredKey">Value from config/env (Harbora:MasterKey / HARBORA_MASTER_KEY).</param>
    /// <param name="isProduction">True when running outside the Development environment.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown in Production when no key is configured, or when the configured key is a known
    /// insecure placeholder.
    /// </exception>
    public static Result Resolve(string? configuredKey, bool isProduction)
    {
        var key = configuredKey?.Trim();
        var isInsecure = string.IsNullOrEmpty(key) || KnownInsecureKeys.Contains(key);

        if (!isInsecure)
            return new Result(key!, UsedDevFallback: false);

        if (isProduction)
            throw new InvalidOperationException(
                "No secure HARBORA_MASTER_KEY is configured. Harbora refuses to start in Production " +
                "with a missing or default master encryption key, because secrets would be " +
                "trivially decryptable. The installer generates one in deploy/.env — set " +
                "HARBORA_MASTER_KEY to a base64-encoded 32-byte value and restart.");

        // Development only: use whatever (insecure) value exists, or the canonical dev key, and warn.
        return new Result(string.IsNullOrEmpty(key) ? DevFallbackKey : key, UsedDevFallback: true);
    }
}
