using Harbora.NodeAgent.Auditing;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Workspaces;

/// <summary>
/// The Docker Ready App: giving a tenant a Docker daemon without giving them the node's.
///
/// <para>
/// The host's <c>/var/run/docker.sock</c> is never shared. Handing it to a tenant is handing them
/// root on the machine and every other tenant's containers with it — the socket has no notion of
/// who is asking. Instead the tenant gets their own daemon in a container, on their own network,
/// with hard caps on what it can consume.
/// </para>
///
/// <para>
/// A nested daemon still needs privileges the ordinary policy refuses, so this path is gated
/// separately: its own feature flag (not the general privileged switch), a node-admin command, a
/// warning in the journal and an entry in the audit log. If any of the three is missing, the
/// workspace is refused rather than downgraded — a half-isolated Docker workspace is worse than
/// none, because it looks like the safe thing.
/// </para>
/// </summary>
public sealed class DockerWorkspaceProvisioner(
    IOptions<NodeAgentOptions> options,
    NodeAuditLog audit,
    ILogger<DockerWorkspaceProvisioner> log)
{
    /// <summary>App id the control plane uses for a tenant Docker workspace.</summary>
    public const string AppId = "harbora.docker-workspace";

    /// <summary>Mount path the tenant's own data lives on inside the workspace.</summary>
    public const string WorkspacePath = "/workspace";

    /// <summary>Host paths that must never appear in a workspace, whatever the request says.</summary>
    public static readonly string[] ForbiddenMounts =
    [
        "/var/run/docker.sock", "/run/docker.sock", "/var/lib/docker",
        "/", "/etc", "/root", "/home", "/proc", "/sys", "/dev", "/boot", "/var/lib/harbora-node",
    ];

    private readonly NodeAgentOptions _options = options.Value;

    public sealed record WorkspaceDecision(bool Allowed, WorkloadSpec? Hardened, IReadOnlyList<PolicyViolation> Violations);

    public static bool IsWorkspace(WorkloadSpec spec) =>
        string.Equals(spec.AppId, AppId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decide whether a workspace may be created here and, if so, return the spec with the
    /// isolation applied. The returned spec is what gets deployed — the caller does not get to
    /// merge it with the original.
    /// </summary>
    public WorkspaceDecision Evaluate(WorkloadSpec spec, bool hasNodeAdminScope)
    {
        var violations = new List<PolicyViolation>();

        if (!_options.Security.AllowIsolatedDockerWorkspace)
            violations.Add(new(NodeErrorCode.PolicyDenied,
                "Isolated Docker workspaces are disabled on this node. Turn on Security:AllowIsolatedDockerWorkspace to permit them; " +
                "they require a nested daemon with privileges an ordinary workload is never given."));

        if (!hasNodeAdminScope)
            violations.Add(new(NodeErrorCode.Unauthorized,
                $"Creating a Docker workspace needs a command carrying the '{NodeScopes.NodeAdmin}' scope."));

        foreach (var container in spec.Containers)
        {
            if (container.HostNetwork)
                violations.Add(new(NodeErrorCode.PolicyDenied,
                    $"A Docker workspace may not use host networking ({container.Name})."));

            if (container.HostPidNamespace)
                violations.Add(new(NodeErrorCode.PolicyDenied,
                    $"A Docker workspace may not use the host PID namespace ({container.Name})."));

            foreach (var mount in container.Mounts)
                if (IsForbidden(mount.VolumeName) || IsForbidden(mount.MountPath))
                    violations.Add(new(NodeErrorCode.PolicyDenied,
                        $"A Docker workspace may not mount '{mount.VolumeName}' at '{mount.MountPath}'. " +
                        "The host's Docker socket and system paths are never shared with a workspace."));
        }

        foreach (var volume in spec.Volumes)
            if (IsForbidden(volume.Name))
                violations.Add(new(NodeErrorCode.PolicyDenied,
                    $"'{volume.Name}' is a host path, not a volume name, and a workspace may not reference it."));

        if (violations.Count > 0) return new WorkspaceDecision(false, null, violations);

        var hardened = Harden(spec);

        log.LogWarning(
            "Provisioning an isolated Docker workspace for tenant {Tenant}. It runs a nested daemon with elevated privileges, " +
            "capped at {Cpu} CPU / {MemoryMiB} MiB / {Pids} processes, on its own network, with no access to this node's Docker socket.",
            spec.TenantId, _options.Workspace.CpuCores, _options.Workspace.MemoryBytes / (1024 * 1024), _options.Workspace.PidsLimit);

        audit.Write(new NodeAuditEntry
        {
            Action = "workspace.provision",
            Outcome = "allowed",
            TargetType = "workload",
            TargetId = spec.WorkloadId,
            TenantId = spec.TenantId,
            Detail = "isolated Docker workspace with a nested privileged daemon",
        });

        return new WorkspaceDecision(true, hardened, []);
    }

    /// <summary>
    /// Apply the isolation. Limits are taken from this node's configuration, not from the spec —
    /// a workspace that could ask for its own caps could ask for none.
    /// </summary>
    private WorkloadSpec Harden(WorkloadSpec spec)
    {
        var limits = _options.Workspace;
        var network = new NetworkSpec { Name = $"harbora-workspace-{spec.TenantId}", Internal = limits.NetworkIsolated };
        var volume = new VolumeSpec { Name = $"harbora-workspace-{spec.WorkloadId}", Persistent = true };

        var containers = spec.Containers.Select(container => container with
        {
            // Nested Docker needs this. Everything else about the container is locked down to
            // compensate, and the whole path is gated behind a flag an operator had to set.
            Privileged = true,
            HostNetwork = false,
            HostPidNamespace = false,

            Resources = new ResourceLimits
            {
                CpuCores = limits.CpuCores,
                MemoryBytes = limits.MemoryBytes,
                MemoryReservationBytes = limits.MemoryBytes / 4,
                PidsLimit = limits.PidsLimit,
                DiskBytes = limits.DiskBytes,
            },

            // Exactly one mount, and it is the workspace's own volume.
            Mounts = [new MountSpec { VolumeName = volume.Name, MountPath = WorkspacePath }],

            // Nothing is published to the host: a tenant's nested daemon must not be able to open a
            // port on the customer's server.
            Ports = container.Ports.Select(p => p with { PublishToHost = false, HostPort = null }).ToList(),

            ReadOnlyRootFilesystem = false,
            CapabilitiesAdd = [],
            CapabilitiesDrop = [],
        }).ToList();

        return spec with
        {
            Containers = containers,
            Networks = [network],
            Volumes = [volume],
            // Routing into a workspace goes through the control plane's proxy, not through a host
            // port the workspace opened for itself.
            HttpRoutes = [],
            TcpRoutes = [],
            Labels = new Dictionary<string, string>(spec.Labels) { [NodeLabels.Workspace] = "true" },
        };
    }

    private static bool IsForbidden(string value)
    {
        var normalised = WorkloadPolicy.NormalisePath(value);

        return ForbiddenMounts.Any(forbidden =>
            normalised == forbidden ||
            (forbidden != "/" && normalised.StartsWith(forbidden + "/", StringComparison.Ordinal)) ||
            value.Contains("docker.sock", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Caps a tenant Docker workspace runs under. Node-owned, never spec-supplied.</summary>
public sealed class WorkspaceLimits
{
    public double CpuCores { get; set; } = 2.0;
    public long MemoryBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int PidsLimit { get; set; } = 2048;
    public long DiskBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    /// <summary>
    /// No egress by default. A nested daemon that can reach the internet can pull anything, and a
    /// workspace is the one place where "anything" includes a miner.
    /// </summary>
    public bool NetworkIsolated { get; set; } = true;
}
