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
/// Trust is deliberately narrow: headers are honoured only when the immediate peer is inside a
/// configured proxy network, and only one hop is unwound, so a client cannot prepend a forged
/// <c>X-Forwarded-For</c> entry and be believed.
/// </summary>
public static class TrustedProxySetup
{
    /// <summary>
    /// Docker's default bridge/user-defined network ranges. Overridable via
    /// <c>Harbora:TrustedProxyNetworks</c> (comma-separated CIDRs) for non-default topologies.
    /// </summary>
    public static readonly string[] DefaultProxyNetworks =
    [
        "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7"
    ];

    /// <summary>
    /// Applies the trusted-proxy configuration. Returns the CIDRs that were accepted so the caller
    /// can log them; unparseable entries are skipped rather than crashing startup.
    /// </summary>
    public static IReadOnlyList<string> Configure(ForwardedHeadersOptions options, IEnumerable<string> cidrs)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Defaults trust localhost only; we replace them wholesale with the configured networks.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        // Exactly one proxy hop (Traefik). With ForwardLimit = 1 the rightmost X-Forwarded-For entry
        // wins — the one Traefik appended — so entries a client injected are never trusted.
        options.ForwardLimit = 1;

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
}
