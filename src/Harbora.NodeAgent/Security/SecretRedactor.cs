using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Harbora.NodeAgent.Security;

/// <summary>
/// Removes secret material from anything on its way to a log, an event, an error message or the
/// control plane.
///
/// <para>
/// Two mechanisms, because either alone is insufficient. Registered values catch the secrets this
/// agent was handed and therefore knows exactly; the patterns catch the ones it was never told
/// about — a password inside a connection string a container printed, a bearer token in a stack
/// trace. Redaction that only knows its own secrets misses everything the workload leaks.
/// </para>
/// </summary>
public sealed partial class SecretRedactor
{
    /// <summary>What replaces a redacted span. Fixed, so a diff of two logs stays readable.</summary>
    public const string Mask = "***REDACTED***";

    /// <summary>
    /// Below this length a "secret" is more likely to be a substring of ordinary text. Redacting
    /// the value "1" would turn every log line into noise and hide nothing.
    /// </summary>
    private const int MinimumRegisteredLength = 6;

    /// <summary>Bounded so a long-lived agent that has injected thousands of secrets does not grow forever.</summary>
    private const int MaxRegisteredValues = 2_000;

    private readonly ConcurrentDictionary<string, byte> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// Remember a value so it is scrubbed wherever it later appears. Called at secret-injection
    /// time, before the value can reach anything that writes.
    /// </summary>
    public void Register(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinimumRegisteredLength) return;
        if (_values.Count >= MaxRegisteredValues) return;
        _values.TryAdd(value, 0);
    }

    public void RegisterAll(IEnumerable<string?> values)
    {
        foreach (var value in values) Register(value);
    }

    /// <summary>Forget a value, e.g. after a credential rotation retired it.</summary>
    public void Forget(string? value)
    {
        if (!string.IsNullOrEmpty(value)) _values.TryRemove(value, out _);
    }

    internal int RegisteredCount => _values.Count;

    /// <summary>Scrub known values and well-known secret shapes out of <paramref name="text"/>.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var result = text;

        // Longest first: a short secret that is a prefix of a longer one must not mask it
        // partially and leave the tail readable.
        foreach (var value in _values.Keys.OrderByDescending(v => v.Length))
            if (result.Contains(value, StringComparison.Ordinal))
                result = result.Replace(value, Mask, StringComparison.Ordinal);

        result = PrivateKeyBlock().Replace(result, $"-----BEGIN PRIVATE KEY-----{Mask}-----END PRIVATE KEY-----");
        result = UrlCredentials().Replace(result, $"$1{Mask}@");
        result = KeyValueSecret().Replace(result, $"$1{Mask}");
        result = BearerToken().Replace(result, $"$1{Mask}");

        return result;
    }

    /// <summary>Scrub every value in a dictionary's values, leaving keys intact.</summary>
    public IReadOnlyDictionary<string, string> RedactValues(IReadOnlyDictionary<string, string> source)
    {
        var result = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
        foreach (var (key, value) in source)
            result[key] = LooksSecret(key) ? Mask : Redact(value);
        return result;
    }

    /// <summary>
    /// Whether a variable name is one whose value should never be printed regardless of content.
    /// A key named PASSWORD holding something that looks harmless is still a password.
    /// </summary>
    public static bool LooksSecret(string key) =>
        SecretishKey().IsMatch(key);

    // Any PEM private key block, however it is labelled (RSA, EC, plain).
    [GeneratedRegex(
        @"-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----.*?-----END (?:[A-Z ]+ )?PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyBlock();

    // scheme://user:password@host — the password half only.
    [GeneratedRegex(@"([a-zA-Z][a-zA-Z0-9+.-]*://[^\s:/@]+:)[^\s@]+@")]
    private static partial Regex UrlCredentials();

    // key=value / "key": "value" for password-ish keys, in text the agent did not author.
    [GeneratedRegex(
        @"((?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key|credential)[""']?\s*[:=]\s*[""']?)[^\s""',;}\]]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"((?:Bearer|Basic)\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex(
        @"(password|passwd|pwd|secret|token|apikey|api_key|accesskey|access_key|privatekey|private_key|credential|conn(ection)?string)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretishKey();
}
