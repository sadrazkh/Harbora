using System.Net;
using System.Text;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Where an outside client connects, and what stands between them and the database.
///
/// The gateway is a small proxy container per grant. It publishes one host port and forwards to the
/// service on its private network, so the database container itself is never published — the
/// database keeps no route to the internet of its own, and closing a grant is removing one
/// container rather than reconfiguring the thing holding the customer's data.
///
/// The allowlist is enforced here rather than at the database, because this is the last place the
/// real client address still exists: past the proxy every connection appears to come from the
/// gateway container.
/// </summary>
public static class TcpGatewayPlan
{
    /// <summary>
    /// Ports Harbora will publish on. Deliberately a narrow, memorable band well clear of anything
    /// the platform itself runs, so an operator reading their firewall can tell at a glance which
    /// ports are database grants.
    /// </summary>
    public const int FirstPort = 15432;
    public const int LastPort = 15999;

    /// <summary>
    /// The lowest free port, or null when the band is full.
    ///
    /// Lowest rather than random: a grant closed and reopened lands on the same number, so a
    /// connection string somebody saved yesterday keeps working today instead of silently pointing
    /// at whichever database took the port next — which is the far worse failure.
    /// </summary>
    public static int? NextPort(IEnumerable<int> taken)
    {
        var used = taken.ToHashSet();
        for (var port = FirstPort; port <= LastPort; port++)
            if (!used.Contains(port)) return port;

        return null;
    }

    /// <summary>
    /// The hostname to hand out: the service's own subdomain of the platform's root domain.
    ///
    /// The same wildcard that already answers for deployed applications, so no DNS record has to be
    /// added for a grant that might last fifteen minutes. Falls back to whatever address the caller
    /// knows when no root domain is configured — a connection string with a bare IP still works,
    /// and one with a hostname that does not resolve does not.
    /// </summary>
    public static string HostFor(string? rootDomain, string serviceSlug, string? fallbackAddress)
    {
        var slug = Slug(serviceSlug);

        if (!string.IsNullOrWhiteSpace(rootDomain)
            && !rootDomain.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && slug.Length > 0)
            return $"{slug}.{rootDomain.Trim().Trim('.')}";

        return string.IsNullOrWhiteSpace(fallbackAddress) ? "localhost" : fallbackAddress.Trim();
    }

    /// <summary>
    /// The container's name, derived so a stray gateway can be found and removed by hand.
    ///
    /// The whole id, not a prefix of it. Grant ids are version 7, which begin with a timestamp, so
    /// two grants created in the same millisecond share their leading digits — a truncated name
    /// collides, and the second grant either fails to start or revoking one removes the other's
    /// gateway. Docker allows the length; there was nothing to save.
    /// </summary>
    public static string ContainerName(Guid grantId) => $"harbora-gw-{grantId:N}";

    /// <summary>
    /// The proxy's configuration.
    ///
    /// HAProxy rather than a plainer forwarder because the allowlist can hold several ranges, and
    /// this is the one place a rejection can happen before the database sees a connection at all.
    /// Returns null when an entry cannot be parsed: a typo must close the door, never widen it.
    /// </summary>
    public static string? Config(string target, int targetPort, string? allowedIps)
    {
        var allow = new List<string>();

        foreach (var entry in (allowedIps ?? string.Empty)
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsCidrOrAddress(entry)) return null;
            allow.Add(entry);
        }

        var config = new StringBuilder();
        config.AppendLine("global");
        config.AppendLine("  log stdout format raw local0");
        config.AppendLine("defaults");
        config.AppendLine("  mode tcp");
        config.AppendLine("  timeout connect 10s");

        // Long enough for a session somebody is typing into, short enough that an abandoned client
        // does not hold the grant's only connection open until the grant expires.
        config.AppendLine("  timeout client 1h");
        config.AppendLine("  timeout server 1h");
        config.AppendLine("frontend db");
        config.AppendLine("  bind :5432");

        if (allow.Count > 0)
        {
            config.AppendLine($"  acl allowed src {string.Join(' ', allow)}");
            config.AppendLine("  tcp-request connection reject if !allowed");
        }

        config.AppendLine("  default_backend service");
        config.AppendLine("backend service");
        config.AppendLine($"  server db {target}:{targetPort}");

        return config.ToString();
    }

    /// <summary>
    /// How the proxy is started: the config is written from an environment variable rather than a
    /// mounted file, so nothing has to exist on the host's disk for a grant that may last an hour.
    /// </summary>
    public static IReadOnlyList<string> Entrypoint() =>
    [
        "sh", "-c",
        "printf '%s' \"$HARBORA_GATEWAY_CONFIG\" > /tmp/haproxy.cfg && exec haproxy -f /tmp/haproxy.cfg -db"
    ];

    public const string Image = "haproxy:3.0-alpine";
    public const string ConfigVariable = "HARBORA_GATEWAY_CONFIG";

    /// <summary>The port the proxy listens on inside its own container.</summary>
    public const int ListenPort = 5432;

    private static bool IsCidrOrAddress(string entry)
    {
        var slash = entry.IndexOf('/');
        if (slash < 0) return IPAddress.TryParse(entry, out _);

        if (!IPAddress.TryParse(entry[..slash], out var address)) return false;
        if (!int.TryParse(entry[(slash + 1)..], out var prefix)) return false;

        var bits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        return prefix >= 0 && prefix <= bits;
    }

    private static string Slug(string value)
    {
        var slug = new string((value ?? string.Empty).Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}
