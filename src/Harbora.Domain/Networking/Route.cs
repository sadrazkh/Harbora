using Harbora.Domain.Common;

namespace Harbora.Domain.Networking;

/// <summary>
/// A routing rule produced by the visual route designer. The proxy engine compiles the
/// full set of Routes into a Traefik dynamic-config document (validated before apply).
/// </summary>
public class Route : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid? AppId { get; set; }

    public RouteType Type { get; set; } = RouteType.HostBased;

    public string Host { get; set; } = string.Empty;      // domain / subdomain
    public string PathPrefix { get; set; } = "/";
    public int Priority { get; set; }                     // higher wins on overlap

    // Upstream target
    public string TargetService { get; set; } = string.Empty; // container/service name
    public int TargetPort { get; set; } = 80;

    /// <summary>
    /// Upstreams beyond <see cref="TargetService"/>/<see cref="TargetPort"/>, as JSON —
    /// <c>[{"Host":"…","Port":123}, …]</c>. Null or empty for an ordinary single-target route, which
    /// is every route the designer creates by hand and every deploy of an app running one replica.
    /// Populated only by a deployment that started more than one replica container, so Traefik's
    /// loadBalancer spreads traffic across all of them rather than the first alone. See
    /// <see cref="RouteUpstreams"/> for the read/write helpers — nothing should touch this string
    /// directly.
    /// </summary>
    public string? ExtraUpstreamsJson { get; set; }

    /// <summary>
    /// Path Traefik should keep polling on every server behind this route, so a replica that stops
    /// answering is pulled out of rotation on its own — no panel-side polling loop needed. Set
    /// alongside <see cref="ExtraUpstreamsJson"/> when the app being routed both runs more than one
    /// replica and has a health check path configured; null otherwise, which renders no active check
    /// at all (Traefik's default — every server is trusted until a request to it fails).
    /// </summary>
    public string? LoadBalancerHealthCheckPath { get; set; }

    // Toggles surfaced in the designer
    public bool SslEnabled { get; set; } = true;
    public bool RedirectHttpToHttps { get; set; } = true;
    public bool WebSocketEnabled { get; set; }
    public bool BasicAuthEnabled { get; set; }
    public string? BasicAuthUsersEncrypted { get; set; }  // htpasswd lines, encrypted

    /// <summary>
    /// Comma-separated addresses or CIDR ranges allowed to reach this route; empty means everyone.
    /// Not encrypted — an allowlist is configuration, not a credential, and an operator needs to
    /// read it back to edit it.
    /// </summary>
    public string? IpAllowlist { get; set; }

    /// <summary>Custom response/request headers as JSON: { "X-Frame-Options": "DENY" }.</summary>
    public string? CustomHeadersJson { get; set; }

    /// <summary>Redirect target when Type = Redirect.</summary>
    public string? RedirectTo { get; set; }

    public bool IsEnabled { get; set; } = true;
}
