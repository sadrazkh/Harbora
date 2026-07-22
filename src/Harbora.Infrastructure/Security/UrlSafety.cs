using System.Net;
using System.Net.Sockets;

namespace Harbora.Infrastructure.Security;

/// <summary>
/// SSRF guard for outbound, user-configured URLs (notification webhooks/Discord) — doc 10 §2.8.
/// Rejects non-http(s) schemes, localhost/metadata hostnames, and IP literals in loopback,
/// link-local, private, or unique-local ranges so a tenant can't point a webhook at internal
/// services or a cloud metadata endpoint. Hostname-based checks are conservative (no DNS
/// resolution) so legitimate public endpoints are never blocked; DNS-rebinding-aware resolution is
/// a documented follow-up.
/// </summary>
public static class UrlSafety
{
    private static readonly string[] BlockedHosts =
    {
        "localhost", "ip6-localhost", "metadata", "metadata.google.internal"
    };

    public static bool IsAllowedOutboundUrl(string? url, out string reason)
    {
        reason = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            reason = "not an absolute URL";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = "only http/https URLs are allowed";
            return false;
        }

        var host = uri.Host.Trim().TrimEnd('.');
        if (BlockedHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"host '{host}' is not allowed";
            return false;
        }

        if (IPAddress.TryParse(host, out var ip) && IsPrivateOrReserved(ip))
        {
            reason = $"IP {ip} is in a private/reserved range";
            return false;
        }

        return true;
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;                                  // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                  // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;                  // 169.254.0.0/16 (link-local, metadata)
            if (b[0] == 127) return true;                                  // loopback
            if (b[0] == 0) return true;                                    // 0.0.0.0/8
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;                        // fc00::/7 unique-local
            return false;
        }

        return false;
    }
}
