namespace Harbora.Infrastructure.Deployments;

/// <summary>Filesystem + naming conventions the deployment engine uses on the host.</summary>
public sealed class HarboraRuntimeOptions
{
    /// <summary>Where sources are checked out and built.</summary>
    public string WorkDir { get; set; } = "/var/lib/harbora/builds";

    /// <summary>Shared docker network apps + Traefik join so the proxy can reach containers by name.</summary>
    public string Network { get; set; } = "harbora";

    /// <summary>Traefik container name; joined to each tenant network so it can route ingress in.</summary>
    public string ProxyContainerName { get; set; } = "harbora-traefik";

    /// <summary>Panel container name; joined to each tenant network so it can HTTP health-probe apps by name.</summary>
    public string PanelContainerName { get; set; } = "harbora-panel";

    /// <summary>Per-workspace network name pattern giving tenant-to-tenant isolation.</summary>
    public string WorkspaceNetwork(string slug) => $"harbora-ws-{slug}";

    /// <summary>Image repository prefix, e.g. "harbora/{slug}:build-{n}".</summary>
    public string ImagePrefix { get; set; } = "harbora";

    /// <summary>Root domain used to build default subdomains: {slug}.{RootDomain}.</summary>
    public string RootDomain { get; set; } = "localhost";
}
