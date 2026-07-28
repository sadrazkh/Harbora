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

    // ---- Health gate (the cutover decision) ----
    // Defaults reproduce the previous hardcoded behaviour: up to 16s to reach "running", then up to
    // 20s of HTTP probing. Configurable because a slow-booting app (JVM, migrations on start) needs
    // longer, and because tests must not sleep for real.

    /// <summary>Seconds between health polls.</summary>
    public double HealthPollIntervalSeconds { get; set; } = 2;

    /// <summary>How many polls to wait for the container to report "running" before giving up.</summary>
    public int HealthRunningAttempts { get; set; } = 8;

    /// <summary>How many HTTP probes of the health path before declaring the deployment unhealthy.</summary>
    public int HealthHttpAttempts { get; set; } = 10;

    /// <summary>Per-request timeout for a single HTTP health probe.</summary>
    public double HealthHttpTimeoutSeconds { get; set; } = 5;

    internal TimeSpan HealthPollInterval => TimeSpan.FromSeconds(Math.Max(0, HealthPollIntervalSeconds));
    internal TimeSpan HealthHttpTimeout => TimeSpan.FromSeconds(Math.Max(0.001, HealthHttpTimeoutSeconds));
}
