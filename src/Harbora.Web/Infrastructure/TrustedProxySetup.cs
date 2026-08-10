using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Configures forwarded-header handling so <c>RemoteIpAddress</c> is the real client, not the
/// reverse proxy. The shipped topology (deploy/docker-compose.yml) puts the panel behind Traefik on
/// a Docker bridge network — without this, every request carries Traefik's container IP, which
/// silently defeats both the per-IP rate limits (one global bucket for the whole platform) and the
/// audit trail (every row records the proxy).
///
/// Trust is deliberately narrow: headers are honoured only while each peer is inside a configured
/// proxy network. Two hops are available for Cloudflare + Traefik; on a direct request the second
/// hop is the untrusted public client, so unwinding stops before a prepended forged value.
/// </summary>
public static class TrustedProxySetup
{
    /// <summary>
    /// Docker's default bridge/user-defined network ranges. Overridable via
    /// <c>Harbora:TrustedProxyNetworks</c> (comma-separated CIDRs) for non-default topologies.
    /// </summary>
    public static readonly string[] DefaultProxyNetworks =
    [
        "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7",
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22",
        "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20",
        "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22", "2400:cb00::/32",
        "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32",
        "2a06:98c0::/29", "2c0f:f248::/32"
    ];

    /// <summary>
    /// Applies the trusted-proxy configuration. Returns the CIDRs that were accepted so the caller
    /// can log them; unparseable entries are skipped rather than crashing startup.
    /// </summary>
    public static IReadOnlyList<string> Configure(
        ForwardedHeadersOptions options, IEnumerable<string> cidrs, int forwardLimit = 2)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Defaults trust localhost only; we replace them wholesale with the configured networks.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        // Two covers Cloudflare + Traefik. A direct request still unwinds only Traefik: the next peer
        // is the public client, which is not in a trusted network, and processing stops there.
        options.ForwardLimit = Math.Clamp(forwardLimit, 1, 5);

        var accepted = new List<string>();
        foreach (var cidr in cidrs)
        {
            if (!TryParseNetwork(cidr, out var network)) continue;
            options.KnownIPNetworks.Add(network);
            accepted.Add(cidr);
        }
        return accepted;
    }

    /// <summary>
    /// Parses "10.0.0.0/8" style CIDR notation, rejecting anything malformed. A base address with
    /// host bits set ("10.1.2.3/8") is accepted and masked to its prefix — matching is done on the
    /// prefix, so it means the same range as "10.0.0.0/8" rather than something wider.
    /// </summary>
    public static bool TryParseNetwork(string? cidr, out IPNetwork network)
    {
        network = default;
        return !string.IsNullOrWhiteSpace(cidr) && IPNetwork.TryParse(cidr.Trim(), out network);
    }

    /// <summary>Reads the configured CIDR list, falling back to the Docker defaults.</summary>
    public static IEnumerable<string> NetworksFromConfiguration(IConfiguration config)
    {
        var configured = config["Harbora:TrustedProxyNetworks"];
        if (string.IsNullOrWhiteSpace(configured)) return DefaultProxyNetworks;

        return configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static int HopsFromConfiguration(IConfiguration config) =>
        int.TryParse(config["Harbora:TrustedProxyHops"], out var hops)
            ? Math.Clamp(hops, 1, 5)
            : 2;
}
