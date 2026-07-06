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
}
