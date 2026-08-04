namespace Harbora.Infrastructure.Ai;

/// <summary>
/// Builds and checks the address a request is forwarded to.
///
/// An administrator can type a provider's base URL, and Harbora's server then makes a request to
/// whatever they typed. That is a server-side request forgery hole unless something checks it: the
/// classic exploit points it at a cloud metadata endpoint and reads the platform's own credentials
/// back out of the response body.
/// </summary>
public static class AiUpstreamUrl
{
    /// <summary>
    /// Addresses that must never be reachable through a provider URL.
    ///
    /// Loopback and link-local cover the metadata services; private ranges cover reaching back into
    /// the platform's own network, where the panel and the database live.
    /// </summary>
    public static bool IsForbiddenHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;

        var trimmed = host.Trim().Trim('[', ']');

        if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        if (!System.Net.IPAddress.TryParse(trimmed, out var ip))
        {
            // A name that is not an address is resolved by the HTTP stack later. Names ending in
            // .internal or .local are the ones that resolve inside a cluster.
            return trimmed.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        }

        if (System.Net.IPAddress.IsLoopback(ip)) return true;

        var bytes = ip.GetAddressBytes();

        if (bytes.Length == 4)
        {
            // 10/8, 172.16/12, 192.168/16 — private. 169.254/16 — link-local, where cloud metadata
            // services live. 0/8 — "this network".
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            if (bytes[0] == 0) return true;
        }
        else if (bytes.Length == 16)
        {
            // fc00::/7 unique-local, fe80::/10 link-local.
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
        }

        return false;
    }

    /// <summary>
    /// The full URL to call, or null when the base is not usable.
    ///
    /// HTTPS only, except on loopback — and loopback is already refused above, so in practice this
    /// means HTTPS. Sending a provider token over plain HTTP hands it to anything on the path.
    /// </summary>
    public static string? Build(string? baseUrl, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return null;

        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (IsForbiddenHost(uri.Host)) return null;

        return $"{baseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}";
    }
}
