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

    // --- Maintenance mode (P5, 2026-08-20 platform-options plan) ---
    //
    // A route belonging to an app in maintenance is temporarily repointed at the panel's own
    // maintenance endpoint instead of the app's real container — the exact same TargetService/
    // TargetPort/ExtraUpstreamsJson/LoadBalancerHealthCheckPath fields DeploymentPipeline.WireProxyAsync
    // already treats as "the live upstream", overwritten the same way and for the same reason. The
    // difference is lifetime: a deployment's own revert is an in-memory list that only has to survive
    // one failed request, while maintenance can stay on for days, so what it overwrote has to be
    // persisted here rather than held in a local variable.

    /// <summary>Whether this route is currently redirected to the panel's maintenance endpoint rather
    /// than the app's real upstream. Set and cleared only by
    /// <c>AppOperationsService.SetMaintenanceModeAsync</c>.</summary>
    public bool MaintenanceRedirected { get; set; }

    /// <summary>The real <see cref="TargetService"/> this route pointed at before maintenance mode
    /// overwrote it, so turning maintenance off restores exactly what was there — never re-derived,
    /// for the same reason a deployment's own revert never re-derives it either.</summary>
    public string? SavedTargetService { get; set; }
    public int? SavedTargetPort { get; set; }
    public string? SavedExtraUpstreamsJson { get; set; }
    public string? SavedLoadBalancerHealthCheckPath { get; set; }
}
