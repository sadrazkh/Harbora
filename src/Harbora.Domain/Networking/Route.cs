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

    // Toggles surfaced in the designer
    public bool SslEnabled { get; set; } = true;
    public bool RedirectHttpToHttps { get; set; } = true;
    public bool WebSocketEnabled { get; set; }
    public bool BasicAuthEnabled { get; set; }
    public string? BasicAuthUsersEncrypted { get; set; }  // htpasswd lines, encrypted

    /// <summary>Custom response/request headers as JSON: { "X-Frame-Options": "DENY" }.</summary>
    public string? CustomHeadersJson { get; set; }

    /// <summary>Redirect target when Type = Redirect.</summary>
    public string? RedirectTo { get; set; }

    public bool IsEnabled { get; set; } = true;
}
