using System.Collections.Concurrent;
using Harbora.Infrastructure.Deployments;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Scrubs secret material from engine output before it reaches a log, a database column or a screen.
///
/// <para>
/// Covers the values this module hands to an engine and therefore knows exactly: repository
/// passwords, access keys, SFTP passwords. That is the case that matters here, because every secret
/// on this path is one we injected ourselves — unlike the node agent, which must also cope with
/// secrets a third-party container prints without ever being told about them, and whose
/// <c>SecretRedactor</c> carries a pattern engine for that reason.
/// </para>
/// <para>
/// Scoped per operation, not a singleton. A long-lived registry would accumulate every credential
/// the panel has ever used and mask their substrings in unrelated output.
/// </para>
/// </summary>
public sealed class EngineOutputRedactor
{
    public const string Mask = "***REDACTED***";

    /// <summary>
    /// Below this length a "secret" is more likely a substring of ordinary text. Masking the value
    /// "1" would blank every log line and hide nothing.
    /// </summary>
    private const int MinimumLength = 6;

    /// <summary>Bounded so a pathological caller cannot grow this without limit.</summary>
    private const int MaximumValues = 256;

    private readonly ConcurrentDictionary<string, byte> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// Remember a value so it is masked wherever it later appears. Called before the process that
    /// receives it starts — registering afterwards is a race the secret can win.
    /// </summary>
    public void Register(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinimumLength) return;
        if (_values.Count >= MaximumValues) return;
        _values.TryAdd(value, 0);
    }

    /// <summary>
    /// Mask every registered value, then strip control characters.
    ///
    /// <para>
    /// Longest first, so a password that contains a shorter registered value is masked whole rather
    /// than leaving its remainder visible around a mask in the middle.
    /// </para>
    /// </summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var result = text;
        foreach (var secret in _values.Keys.OrderByDescending(v => v.Length))
            result = result.Replace(secret, Mask, StringComparison.Ordinal);

        // Reuses the platform's existing cleaner: NUL cannot be stored at all, and other control
        // characters carry no meaning in a line a person reads.
        return LogText.Clean(result);
    }
}
