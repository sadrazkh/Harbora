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

    /// <summary>
    /// How many rollback-eligible deployments keep their build image after a successful deploy.
    /// This is the real depth of "instant rollback": beyond it, an artifact rollback is impossible
    /// and the user must redeploy from source. 0 disables pruning entirely.
    /// </summary>
    public int ImageRetentionCount { get; set; } = 5;

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

    /// <summary>
    /// After the proxy accepts a new configuration, make one request through it for the app's
    /// primary domain and fail the deployment if nothing answers.
    ///
    /// What that proves, exactly: the proxy is reachable from the panel and answering on :80. It
    /// does NOT prove the route matched or that the domain serves. The request is made to the proxy
    /// container on the plain HTTP entry point with the domain in a Host header, and
    /// <c>deploy/docker-compose.yml</c> configures the redirect to HTTPS at the ENTRYPOINT
    /// (<c>--entrypoints.web.http.redirections.entrypoint.to=websecure</c>), which Traefik applies
    /// to everything arriving on :80 before any router is consulted. The 308 therefore comes back
    /// identically whether the route applied, never applied, or points at a container that no longer
    /// exists. The failure it does catch — a proxy that accepted the config and then died, or that
    /// the panel cannot reach at all — is real and is caught by no other step.
    ///
    /// Verifying the route itself is a later-phase decision and needs a different client: a named
    /// <see cref="System.Net.Http.HttpClient"/> whose <c>SocketsHttpHandler.ConnectCallback</c>
    /// dials the proxy container while the request URI stays <c>https://{domain}/</c>, so SNI and
    /// certificate validation remain on the domain and the request reaches the routers on
    /// <c>websecure</c> instead of being answered by the entrypoint redirect. That is the true
    /// equivalent of the <c>curl --resolve</c> check in <c>deploy/install.sh</c>. It also wants the
    /// retry install.sh has (12 attempts over a minute), because Traefik's file-provider watch means
    /// a single immediate probe reports a false failure on a healthy install.
    ///
    /// Off by default, deliberately. Turning it on makes every deployment of an app with a domain
    /// depend on the panel being able to reach the proxy, and that is only worth asserting once
    /// there is a live-host CI lane to prove it holds — otherwise the first thing this flag would
    /// do is fail deployments that worked.
    /// </summary>
    public bool VerifyThroughProxy { get; set; }

    /// <summary>
    /// How long a release task may run before the deployment gives up on it. Generous, because a
    /// migration against a large database legitimately takes minutes; bounded, because a command
    /// that waits for input otherwise leaves a deployment "in progress" for ever, with nothing on
    /// the screen to click and no way to tell a slow migration from a stuck one.
    /// </summary>
    public double ReleaseTaskTimeoutMinutes { get; set; } = 30;

    internal TimeSpan HealthPollInterval => TimeSpan.FromSeconds(Math.Max(0, HealthPollIntervalSeconds));
    internal TimeSpan HealthHttpTimeout => TimeSpan.FromSeconds(Math.Max(0.001, HealthHttpTimeoutSeconds));
    internal TimeSpan ReleaseTaskTimeout => TimeSpan.FromMinutes(Math.Max(0.0001, ReleaseTaskTimeoutMinutes));
}
