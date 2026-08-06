using System.Net;

namespace Harbora.Infrastructure.Proxy;

/// <summary>
/// The addresses allowed to reach a route.
///
/// A pure rule because of what an empty list means: <b>everyone</b>. A parser that silently drops a
/// malformed entry can turn "only the office" into "only these two of the three you typed", and one
/// that returns nothing for a whole-list typo turns it into "nobody" — a site that is down for its
/// owner with no error anywhere. Both failures are silent, so both are decided here and tested.
///
/// Traefik's ipAllowList takes IPv4/IPv6 addresses and CIDR ranges. Anything else is refused at the
/// form rather than written into a config that would then be rejected on apply.
/// </summary>
public static class AccessList
{
    /// <summary>
    /// The valid entries in a comma- or newline-separated list, in the order given and de-duplicated.
    /// <paramref name="rejected"/> receives anything unusable so the form can say which entry it was
    /// — "invalid allowlist" alone leaves somebody hunting through fifteen addresses.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text, out IReadOnlyList<string> rejected)
    {
        var accepted = new List<string>();
        var bad = new List<string>();

        foreach (var raw in (text ?? "").Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;

            if (IsValid(entry))
            {
                if (!accepted.Contains(entry, StringComparer.OrdinalIgnoreCase)) accepted.Add(entry);
            }
            else bad.Add(entry);
        }

        rejected = bad;
        return accepted;
    }

    /// <summary>One address or CIDR range, as Traefik would read it.</summary>
    public static bool IsValid(string entry)
    {
        var slash = entry.IndexOf('/');
        if (slash < 0) return IPAddress.TryParse(entry, out _);

        var address = entry[..slash];
        var prefix = entry[(slash + 1)..];

        if (!IPAddress.TryParse(address, out var parsed)) return false;
        if (!int.TryParse(prefix, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var bits)) return false;

        // A /33 on IPv4 or /129 on IPv6 is a typo that Traefik rejects at apply time — which is to
        // say, after the operator has already left the page believing it saved.
        var max = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return bits >= 0 && bits <= max;
    }

    /// <summary>The stored form: one line, comma-separated. Empty means no restriction.</summary>
    public static string Format(IEnumerable<string> entries) => string.Join(",", entries);
}
