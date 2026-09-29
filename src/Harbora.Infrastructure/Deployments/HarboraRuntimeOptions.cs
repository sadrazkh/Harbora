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

    /// <summary>
    /// The port the proxy's plain-HTTP entry point listens on, as the panel reaches it over the
    /// container network — the <c>web</c> entrypoint in <c>deploy/docker-compose.yml</c>, which is
    /// 80 there and is what the default matches.
    ///
    /// <para>
    /// Configurable because <see cref="ProxyContainerName"/> is: an install that renamed its proxy
    /// is an install that changed its proxy, and one that fronts Traefik differently — or runs the
    /// entry point on another port — would have had <see cref="VerifyThroughProxy"/> dial a port
    /// nothing was listening on and report every deployment as failed. A knob with no companion is
    /// how a configurable thing stays half-configurable.
    /// </para>
    /// </summary>
    public int ProxyHttpPort { get; set; } = 80;

    /// <summary>Panel container name; joined to each tenant network so it can HTTP health-probe apps by name.</summary>
    public string PanelContainerName { get; set; } = "harbora-panel";

    /// <summary>
    /// The port the panel container itself listens on, as the proxy reaches it over the container
    /// network — <c>ASPNETCORE_URLS</c> in the <c>Dockerfile</c>, which is 8080, and what
    /// <c>deploy/docker-compose.yml</c>'s own <c>harbora</c> router label
    /// (<c>traefik.http.services.harbora.loadbalancer.server.port=8080</c>) already targets.
    ///
    /// <para>
    /// Configurable for the same reason <see cref="ProxyHttpPort"/> is: <see cref="PanelContainerName"/>
    /// already is, and a maintenance-mode route built from the name but a hardcoded port would aim at
    /// a container that answers on a port nothing is listening on the moment either knob is changed
    /// without the other.
    /// </para>
    /// </summary>
    public int PanelHttpPort { get; set; } = 8080;

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

    /// <summary>
    /// How often, in hours, the panel prunes every server's DANGLING images on its own — and
    /// <c>0</c> (or less) turns that background sweep off. Default 24: once a day.
    ///
    /// <para>
    /// Dangling images are the untagged layers each classic-builder rebuild leaves behind. They are the
    /// part of the disk that <c>ImageRetentionCount</c> cannot reach, because retention is about TAGGED
    /// build images; on the server that prompted this setting 165 of them (70 GB) took the disk to 96%
    /// while the tagged build images were about 10 GB. Only dangling images are ever pruned, through
    /// the daemon's own prune call — never a tagged image, so build bases and rollback tags are safe at
    /// any value here.
    /// </para>
    ///
    /// <para>
    /// A day is the shape of the leak (it grows with rebuilds, over days and weeks, not minutes), so a
    /// shorter period only adds daemon work and more chances to overlap a build, and a longer one lets
    /// the disk refill in between. Fractions are accepted (<c>0.5</c> is every half hour).
    /// </para>
    /// </summary>
    public double DanglingImageSweepHours { get; set; } = 24;

    /// <summary>The longest the sweep interval is allowed to be. Not a policy: a timer or delay refuses
    /// a period past about 49 days, and a configuration typo that made the background service throw
    /// while starting would stop the panel from starting at all — which is the one thing this sweep
    /// must never do. A larger value is read as this, not rejected.</summary>
    internal static readonly TimeSpan MaxDanglingImageSweepInterval = TimeSpan.FromDays(30);

    /// <summary>The shortest: one minute. Below that a typo would have every server's daemon pruned in
    /// a tight loop, each pass logging a line, for no benefit a person could measure.</summary>
    internal static readonly TimeSpan MinDanglingImageSweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// <see cref="DanglingImageSweepHours"/> as a period, or null when the sweep is off — a value of
    /// zero or less, and also a value that is not a number, which is read as off rather than thrown.
    /// Anything else is clamped into [one minute, thirty days].
    /// </summary>
    internal TimeSpan? DanglingImageSweepInterval
    {
        get
        {
            // "Not greater than zero" rather than "less than or equal", so NaN lands here too.
            if (!(DanglingImageSweepHours > 0)) return null;

            // Capped in hours BEFORE it becomes a TimeSpan: FromHours throws OverflowException for a
            // value past what a TimeSpan can hold, and 1e12 or infinity is a typo, not a request.
            var span = TimeSpan.FromHours(Math.Min(DanglingImageSweepHours, MaxDanglingImageSweepInterval.TotalHours));

            return span < MinDanglingImageSweepInterval ? MinDanglingImageSweepInterval : span;
        }
    }

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
