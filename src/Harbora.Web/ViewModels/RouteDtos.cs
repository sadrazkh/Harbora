using Harbora.Domain.Common;
using Route = Harbora.Domain.Networking.Route;

namespace Harbora.Web.ViewModels;

/// <summary>Wire shape for a routing rule exchanged with the visual designer island.</summary>
public sealed class RouteDto
{
    public Guid? Id { get; set; }
    public Guid? AppId { get; set; }
    public string Type { get; set; } = nameof(RouteType.HostBased);
    public string Host { get; set; } = string.Empty;
    public string PathPrefix { get; set; } = "/";
    public int Priority { get; set; }
    public string TargetService { get; set; } = string.Empty;
    public int TargetPort { get; set; } = 80;
    public bool SslEnabled { get; set; } = true;
    public bool RedirectHttpToHttps { get; set; } = true;
    public bool WebSocketEnabled { get; set; }
    public bool BasicAuthEnabled { get; set; }
    public string? CustomHeadersJson { get; set; }
    public string? RedirectTo { get; set; }
    public bool IsEnabled { get; set; } = true;

    public static RouteDto FromEntity(Route r) => new()
    {
        Id = r.Id,
        AppId = r.AppId,
        Type = r.Type.ToString(),
        Host = r.Host,
        PathPrefix = r.PathPrefix,
        Priority = r.Priority,
        TargetService = r.TargetService,
        TargetPort = r.TargetPort,
        SslEnabled = r.SslEnabled,
        RedirectHttpToHttps = r.RedirectHttpToHttps,
        WebSocketEnabled = r.WebSocketEnabled,
        BasicAuthEnabled = r.BasicAuthEnabled,
        CustomHeadersJson = r.CustomHeadersJson,
        RedirectTo = r.RedirectTo,
        IsEnabled = r.IsEnabled
    };

    /// <summary>Applies this DTO onto an entity (create or update). Workspace is set by the caller.</summary>
    public void ApplyTo(Route r)
    {
        r.AppId = AppId;
        r.Type = Enum.TryParse<RouteType>(Type, out var t) ? t : RouteType.HostBased;
        r.Host = Host.Trim().ToLowerInvariant();
        r.PathPrefix = string.IsNullOrWhiteSpace(PathPrefix) ? "/" : PathPrefix.Trim();
        r.Priority = Priority;
        r.TargetService = TargetService.Trim();
        r.TargetPort = TargetPort;
        r.SslEnabled = SslEnabled;
        r.RedirectHttpToHttps = RedirectHttpToHttps;
        r.WebSocketEnabled = WebSocketEnabled;
        r.BasicAuthEnabled = BasicAuthEnabled;
        r.CustomHeadersJson = string.IsNullOrWhiteSpace(CustomHeadersJson) ? null : CustomHeadersJson;
        r.RedirectTo = string.IsNullOrWhiteSpace(RedirectTo) ? null : RedirectTo;
        r.IsEnabled = IsEnabled;
    }

    /// <summary>A transient entity used only to feed the proxy engine for preview/validate.</summary>
    public Route ToTransientEntity()
    {
        var r = new Route { Id = Id ?? Guid.CreateVersion7() };
        ApplyTo(r);
        return r;
    }
}

/// <summary>A deployable target the designer can point a route at.</summary>
public sealed record RouteTargetDto(string Label, string Service, int Port, Guid AppId);
