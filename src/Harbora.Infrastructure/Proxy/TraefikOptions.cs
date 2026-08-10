namespace Harbora.Infrastructure.Proxy;

/// <summary>
/// Why Traefik over Nginx: routes change constantly on a deploy platform. Traefik hot-reloads
/// this dynamic-config file with no process reload, ships built-in ACME/Let's Encrypt, and
/// discovers containers by label — so the visual designer just emits routes and Harbora renders
/// them here. Nginx would need full config regeneration + reload + a separate certbot.
/// </summary>
public sealed class TraefikOptions
{
    /// <summary>Directory Traefik watches for dynamic config (file provider).</summary>
    public string DynamicConfigPath { get; set; } = "/etc/harbora/traefik/dynamic/harbora.yml";

    /// <summary>Name of the ACME cert resolver configured in traefik.yml.</summary>
    public string CertResolver { get; set; } = "letsencrypt";

    public string EntryPointWeb { get; set; } = "web";
    public string EntryPointWebSecure { get; set; } = "websecure";

    /// <summary>
    /// Which incoming X-Forwarded-For address an IP allowlist uses. Zero keeps Traefik's direct peer;
    /// Cloudflare mode uses one because Cloudflare supplies the visitor as the rightmost address.
    /// Safe only when the origin accepts traffic from the proxy network, as the runbook requires.
    /// </summary>
    public int ForwardedClientIpDepth { get; set; }

    /// <summary>
    /// Marker written by the panel when Cloudflare mode is active. It switches generated routes to
    /// the DNS-01 resolver without restarting the panel that is performing the change.
    /// </summary>
    public string CloudflareEnabledMarkerPath { get; set; } = "/dynamic/cloudflare.enabled";
}
