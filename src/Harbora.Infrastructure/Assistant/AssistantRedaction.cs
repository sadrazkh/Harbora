using System.Text.RegularExpressions;

namespace Harbora.Infrastructure.Assistant;

/// <summary>What is left of a log once everything that must not leave the server is taken out.</summary>
/// <param name="Text">The text that may be sent.</param>
/// <param name="Removed">How many things were taken out — shown to the person before they send it.</param>
public sealed record RedactedLog(string Text, int Removed);

/// <summary>
/// The boundary between this server and an outside model.
///
/// Everything else in the assistant is a convenience. This is the part that can do harm, because a
/// deployment log is one of the most secret-dense things a PaaS holds: connection strings, tokens
/// echoed by a build tool, a framework dumping its whole configuration on the way down.
///
/// Two layers, because either one alone is not enough. The values Harbora knows are removed by
/// value — that catches a secret however it was printed. But a log also carries secrets Harbora has
/// never seen: a key belonging to a third-party service, printed by the application itself. Those are
/// caught by shape.
///
/// It over-redacts on purpose. A masked line costs an explanation that was slightly less useful; a
/// missed one is somebody's production database password sitting in a third party's request log, and
/// no amount of usefulness buys that back.
/// </summary>
public static class AssistantRedaction
{
    /// <summary>Stand-in for anything taken out, kept visible so the shape of the line survives.</summary>
    public const string Mask = "[redacted]";

    /// <summary>
    /// Below this a "secret" is too short to replace safely: masking every "abc" in a log turns it
    /// into nonsense, and a three-character secret is not one.
    /// </summary>
    private const int ShortestReplaceableSecret = 4;

    /// <summary>
    /// Secrets by shape, for the ones Harbora was never told about.
    ///
    /// Deliberately not a general "high entropy" rule: image digests, layer ids and commit hashes are
    /// long, random-looking, and exactly what somebody reads to understand a failed build. Masking
    /// those buys no safety and costs the explanation.
    /// </summary>
    private static readonly (string Name, Regex Pattern)[] Shapes =
    [
        // A whole PEM block, not just its header — the key material is the part that matters.
        ("private key", new Regex(
            @"-{5}BEGIN[^-]{0,40}PRIVATE KEY-{5}.*?-{5}END[^-]{0,40}PRIVATE KEY-{5}",
            RegexOptions.Singleline | RegexOptions.Compiled)),

        // user:password@host — how a connection string carries its credentials.
        ("credentials in a url", new Regex(
            @"(?<scheme>[a-z][a-z0-9+.\-]*://)(?<user>[^\s:/@]+):(?<secret>[^\s/@]+)@",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // A JWT: three base64url segments. Often an access token pasted into a log line.
        ("token", new Regex(
            @"\beyJ[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{4,}\b",
            RegexOptions.Compiled)),

        ("authorization header", new Regex(
            @"\b(?<kind>bearer|basic|token)\s+(?<secret>[A-Za-z0-9_\-.=+/]{8,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // NAME=value, where the name says the value is sensitive. The name is kept: knowing that
        // DATABASE_PASSWORD was set is useful, knowing what it is helps nobody safely.
        ("secret value", new Regex(
            @"(?<name>[A-Za-z0-9_\-.]*(?:password|passwd|pwd|secret|token|api[_\-]?key|access[_\-]?key|private[_\-]?key|credential|passphrase|auth)[A-Za-z0-9_\-.]*)"
            + @"(?<sep>\s*[:=]\s*)(?<secret>""[^""\n]+""|'[^'\n]+'|\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    ];

    /// <summary>
    /// Redacts a log so it can leave the server.
    ///
    /// <paramref name="knownSecrets"/> is every secret value Harbora holds for the service — already
    /// decrypted, because a secret is only recognisable in a log as its plaintext.
    /// </summary>
    public static RedactedLog Redact(string? text, IEnumerable<string>? knownSecrets = null)
    {
        // Nothing in, nothing out. Not an error: there is simply nothing to send.
        if (string.IsNullOrEmpty(text)) return new RedactedLog(string.Empty, 0);

        var removed = 0;

        // By value first. A known secret masked here cannot be missed by the shape rules below.
        foreach (var secret in (knownSecrets ?? []).Where(NotTooShort).OrderByDescending(s => s.Length))
        {
            var replaced = text.Replace(secret, Mask, StringComparison.Ordinal);
            if (!ReferenceEquals(replaced, text) && replaced != text)
            {
                removed += Occurrences(text, secret);
                text = replaced;
            }
        }

        // Then by shape, for everything Harbora was never told about.
        foreach (var (_, pattern) in Shapes)
        {
            text = pattern.Replace(text, match =>
            {
                removed++;
                return Rewrite(match);
            });
        }

        return new RedactedLog(text, removed);
    }

    /// <summary>
    /// Keeps whatever part of a match is safe and says what it was, rather than deleting the line.
    /// "DATABASE_URL=[redacted]" tells somebody the variable was set; a blank line tells them nothing,
    /// and an explanation built on a blank line is worse than none.
    /// </summary>
    private static string Rewrite(Match match)
    {
        var name = match.Groups["name"];
        if (name.Success) return $"{name.Value}{match.Groups["sep"].Value}{Mask}";

        var scheme = match.Groups["scheme"];
        if (scheme.Success) return $"{scheme.Value}{match.Groups["user"].Value}:{Mask}@";

        var kind = match.Groups["kind"];
        if (kind.Success) return $"{kind.Value} {Mask}";

        return Mask;
    }

    private static bool NotTooShort(string? secret) =>
        !string.IsNullOrEmpty(secret) && secret.Length >= ShortestReplaceableSecret;

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var at = text.IndexOf(value, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal);
        }
        return count;
    }
}
