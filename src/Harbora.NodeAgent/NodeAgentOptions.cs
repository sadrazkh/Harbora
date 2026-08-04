using System.ComponentModel.DataAnnotations;

namespace Harbora.NodeAgent;

/// <summary>
/// Everything the agent is configured with. Bound from <c>appsettings.json</c>, the
/// <c>HARBORA_NODE_</c> environment prefix and the installer-written
/// <c>/etc/harbora-node/agent.conf</c>, in that order of increasing precedence.
/// </summary>
public sealed class NodeAgentOptions
{
    public const string SectionName = "NodeAgent";

    /// <summary>Base URL of the control plane, e.g. <c>https://panel.example.com</c>.</summary>
    [Required]
    public string ControlPlaneUrl { get; set; } = string.Empty;

    /// <summary>
    /// Short-lived single-use token, present only until the first successful enrollment. The
    /// installer writes it to a 0600 file and the agent deletes that file the moment it has a
    /// certificate — a spent token left lying around is a credential-shaped object that looks
    /// valid to anyone who finds it.
    /// </summary>
    public string? EnrollmentToken { get; set; }

    /// <summary>Path the enrollment token was read from, so it can be shredded after use.</summary>
    public string? EnrollmentTokenFile { get; set; }

    /// <summary>Operator-chosen node name. Defaults to the machine hostname.</summary>
    public string NodeName { get; set; } = System.Environment.MachineName;

    public string? Region { get; set; }

    /// <summary>Free-form environment tag, e.g. <c>production</c>. Used for placement, not for policy.</summary>
    public string? Environment { get; set; }

    /// <summary>Scheduler hints. Kept as strings so the control plane can invent new ones freely.</summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>Root for identity, state, snapshots and staged agent binaries.</summary>
    public string DataDirectory { get; set; } = "/var/lib/harbora-node";

    /// <summary>Container runtime endpoint. Defaults to the local Docker socket.</summary>
    public string DockerHost { get; set; } = "unix:///var/run/docker.sock";

    /// <summary>
    /// Image used for the agent's own helper containers — volume archiving, restores, checksums.
    ///
    /// <para>
    /// The digest-pinning rule the policy enforces applies to tenant workloads; this is the agent's
    /// own tooling and is pinned by the operator instead. Production installs should set a digest
    /// here (<c>repo@sha256:…</c>); the tagged default exists so a fresh install works before anyone
    /// has looked one up.
    /// </para>
    /// </summary>
    public string MaintenanceImage { get; set; } = "docker.io/library/busybox:1.36";

    /// <summary>
    /// TCP gateway to dial for database tunnels, as <c>host:port</c>. Normally supplied by the
    /// control plane at enrollment; this overrides it, which is what a development setup needs.
    /// </summary>
    public string? TunnelGatewayUrl { get; set; }

    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Fraction of the certificate lifetime after which renewal starts. Two thirds leaves a third
    /// of the lifetime's worth of retries before a failing renewal turns into an outage.
    /// </summary>
    public double CertificateRenewalThreshold { get; set; } = 0.66;

    public ReconnectOptions Reconnect { get; set; } = new();

    public MetricsOptions Metrics { get; set; } = new();

    public SecurityOptions Security { get; set; } = new();

    public PortAllocationOptions Ports { get; set; } = new();

    /// <summary>Caps an isolated Docker workspace runs under. Node-owned, never spec-supplied.</summary>
    public Workspaces.WorkspaceLimits Workspace { get; set; } = new();

    /// <summary>Commands executed concurrently. Beyond this they queue; nothing is dropped.</summary>
    public int MaxConcurrentCommands { get; set; } = 4;

    /// <summary>Idempotency records are kept this long, then swept.</summary>
    public int IdempotencyRetentionHours { get; set; } = 48;

    public string IdentityDirectory => Path.Combine(DataDirectory, "identity");
    public string StateDirectory => Path.Combine(DataDirectory, "state");
    public string SnapshotDirectory => Path.Combine(DataDirectory, "snapshots");
    public string StagingDirectory => Path.Combine(DataDirectory, "staging");
    public string AuditLogPath => Path.Combine(DataDirectory, "audit", "node-audit.log");

    /// <summary>
    /// Problems that must stop the process rather than be discovered halfway through a deploy.
    /// Returns an empty list when the configuration is usable.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(ControlPlaneUrl))
            problems.Add("ControlPlaneUrl is required.");
        else if (!Uri.TryCreate(ControlPlaneUrl, UriKind.Absolute, out var url))
            problems.Add($"ControlPlaneUrl '{ControlPlaneUrl}' is not an absolute URL.");
        else if (url.Scheme != Uri.UriSchemeHttps && !Security.AllowInsecureControlPlane)
            problems.Add("ControlPlaneUrl must be https. Set Security:AllowInsecureControlPlane only for local development.");

        if (string.IsNullOrWhiteSpace(NodeName))
            problems.Add("NodeName is required.");

        if (HeartbeatIntervalSeconds is < 5 or > 600)
            problems.Add("HeartbeatIntervalSeconds must be between 5 and 600.");

        if (CertificateRenewalThreshold is <= 0 or >= 1)
            problems.Add("CertificateRenewalThreshold must be between 0 and 1 (exclusive).");

        if (MaxConcurrentCommands is < 1 or > 64)
            problems.Add("MaxConcurrentCommands must be between 1 and 64.");

        if (Ports.Start >= Ports.End)
            problems.Add("Ports:Start must be below Ports:End.");

        if (Ports.Start < 1024)
            problems.Add("Ports:Start must be 1024 or above; privileged ports are not the agent's to hand out.");

        problems.AddRange(Metrics.Validate());

        return problems;
    }
}

public sealed class ReconnectOptions
{
    public int InitialDelayMs { get; set; } = 1_000;
    public int MaxDelayMs { get; set; } = 300_000;
    public double Multiplier { get; set; } = 2.0;

    /// <summary>
    /// Full jitter. Not decoration: a control-plane restart drops every node at once, and without
    /// jitter they all come back in the same instant and drop it again.
    /// </summary>
    public bool Jitter { get; set; } = true;
}

public sealed class MetricsOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Loopback by default. The metrics port is for the host, not for the network.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9701;

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!Enabled) return problems;

        if (Port is < 1 or > 65535)
            problems.Add("Metrics:Port must be a valid port.");

        if (BindAddress is not ("127.0.0.1" or "::1" or "localhost"))
            problems.Add($"Metrics:BindAddress '{BindAddress}' is not loopback. The metrics endpoint exposes node internals and must not be reachable from the network.");

        return problems;
    }
}

public sealed class SecurityOptions
{
    /// <summary>
    /// Allows a plain-http control plane. Development only, and validated as such — an agent that
    /// would accept http in production is an agent whose enrollment token travels in the clear.
    /// </summary>
    public bool AllowInsecureControlPlane { get; set; }

    /// <summary>
    /// Master switch for privileged containers, host networking and host PID namespace. Off, every
    /// such spec is refused — including one carrying node-admin scope. Turning it on is a
    /// deliberate act by the machine's owner, recorded in the audit log at startup.
    /// </summary>
    public bool AllowPrivilegedWorkloads { get; set; }

    /// <summary>
    /// Separate switch for tenant Docker workspaces, deliberately not the same one as
    /// <see cref="AllowPrivilegedWorkloads"/>.
    ///
    /// <para>
    /// An operator who enables privileged mode for one internal workload has not thereby agreed to
    /// run untrusted tenant code in a nested daemon. Two different decisions get two different
    /// flags, so neither can be granted by accident along with the other.
    /// </para>
    /// </summary>
    public bool AllowIsolatedDockerWorkspace { get; set; }

    /// <summary>
    /// Host paths that may never be mounted into a container, however the request is phrased.
    /// Checked after path normalisation, so <c>/var/run/../run/docker.sock</c> is the same string
    /// to the policy as the obvious spelling.
    /// </summary>
    public List<string> DeniedHostPaths { get; set; } =
    [
        "/", "/etc", "/root", "/home", "/boot", "/dev", "/proc", "/sys",
        "/var/run", "/run", "/var/lib/docker", "/var/lib/harbora-node",
    ];

    /// <summary>Linux capabilities a workload may never add.</summary>
    public List<string> DeniedCapabilities { get; set; } =
    [
        "SYS_ADMIN", "SYS_MODULE", "SYS_RAWIO", "SYS_PTRACE", "SYS_BOOT",
        "DAC_READ_SEARCH", "NET_ADMIN", "MAC_ADMIN", "MAC_OVERRIDE",
    ];

    /// <summary>Skip control-plane certificate validation. Development only; refuses to combine with https in production.</summary>
    public bool TrustAnyControlPlaneCertificate { get; set; }
}

public sealed class PortAllocationOptions
{
    public int Start { get; set; } = 30_000;
    public int End { get; set; } = 32_767;
}
