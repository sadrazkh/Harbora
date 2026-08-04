using System.Text.RegularExpressions;
using Harbora.NodeAgent.Contracts;

namespace Harbora.NodeAgent.Runtime;

/// <summary>A refusal, with the contract code the control plane will branch on.</summary>
public sealed record PolicyViolation(NodeErrorCode Code, string Message, string? Field = null);

/// <summary>
/// The rules a workload specification must satisfy before any of it reaches the runtime.
///
/// <para>
/// This is the file that decides what a control plane is allowed to do to a customer's server, so
/// it is written as a pure function over the spec: no I/O, no ambient state, everything it decides
/// from arguments. That is what makes the whole policy testable without a Docker daemon, and what
/// keeps "we checked that" from depending on which call site got there first.
/// </para>
/// </summary>
public sealed class WorkloadPolicy(SecurityOptions security, PortAllocationOptions ports)
{
    /// <summary>
    /// Volume names, strictly. This is a security control, not tidiness: Docker's bind syntax is
    /// <c>source:target</c>, and a "volume name" of <c>/var/run/docker.sock</c> would become a host
    /// bind mount of the Docker socket — handing a tenant the whole machine through a field that
    /// looks like a label.
    /// </summary>
    private static readonly Regex VolumeName = new(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,62}$", RegexOptions.Compiled);

    private static readonly Regex DnsName = new(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);

    private static readonly Regex Digest = new(@"^sha256:[a-f0-9]{64}$", RegexOptions.Compiled);

    private static readonly Regex NetworkName = new(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$", RegexOptions.Compiled);

    /// <summary>Container paths that are off limits along with everything under them.</summary>
    private static readonly string[] ProtectedContainerTrees =
        ["/", "/proc", "/sys", "/dev", "/boot"];

    /// <summary>
    /// Container paths that are off limits exactly, but whose children are fine.
    ///
    /// <para>
    /// <c>/etc</c> is the interesting one: mounting a volume over the whole directory replaces
    /// <c>passwd</c>, <c>resolv.conf</c> and the CA bundle in one move, while mounting into
    /// <c>/etc/nginx/conf.d</c> is how half the images on Docker Hub are configured. Blocking the
    /// tree would break the ordinary case to prevent the dangerous one.
    /// </para>
    /// </summary>
    private static readonly string[] ProtectedContainerFiles =
        ["/etc", "/etc/passwd", "/etc/shadow", "/etc/sudoers", "/etc/ssl/certs"];

    /// <summary>
    /// Check a spec. Returns every violation rather than the first, so an operator fixing a
    /// template sees the whole list instead of playing whack-a-mole one deploy at a time.
    /// </summary>
    public IReadOnlyList<PolicyViolation> Validate(
        WorkloadSpec spec, string hostArchitecture, string agentVersion, bool hasNodeAdminScope)
    {
        var violations = new List<PolicyViolation>();

        if (string.IsNullOrWhiteSpace(spec.WorkloadId))
            violations.Add(new(NodeErrorCode.ValidationFailed, "workloadId is required.", nameof(spec.WorkloadId)));

        if (string.IsNullOrWhiteSpace(spec.TenantId))
            violations.Add(new(NodeErrorCode.ValidationFailed, "tenantId is required; every resource is labelled with it.", nameof(spec.TenantId)));

        if (!DnsName.IsMatch(spec.Name))
            violations.Add(new(NodeErrorCode.ValidationFailed,
                $"name '{spec.Name}' must be a DNS label: lowercase letters, digits and hyphens, 1–63 characters.", nameof(spec.Name)));

        if (spec.Containers.Count == 0)
            violations.Add(new(NodeErrorCode.ValidationFailed, "a workload must declare at least one container.", nameof(spec.Containers)));

        if (spec.Containers.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != spec.Containers.Count)
            violations.Add(new(NodeErrorCode.ValidationFailed, "container names must be unique within a workload.", nameof(spec.Containers)));

        if (!AgentVersion.IsAtLeast(agentVersion, spec.MinimumAgentVersion))
            violations.Add(new(NodeErrorCode.AgentTooOld,
                $"this workload needs agent {spec.MinimumAgentVersion}; this node runs {agentVersion}."));

        if (spec.SupportedArchitectures.Count > 0 &&
            !spec.SupportedArchitectures.Contains(hostArchitecture, StringComparer.OrdinalIgnoreCase))
            violations.Add(new(NodeErrorCode.UnsupportedArchitecture,
                $"this workload supports {string.Join(", ", spec.SupportedArchitectures)}; this node is {hostArchitecture}."));

        var declaredVolumes = spec.Volumes.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var volume in spec.Volumes)
            if (!VolumeName.IsMatch(volume.Name))
                violations.Add(new(NodeErrorCode.PolicyDenied,
                    $"volume name '{volume.Name}' is not a plain name. A name containing a path separator would become a host bind mount.",
                    nameof(volume.Name)));

        foreach (var network in spec.Networks)
            if (!NetworkName.IsMatch(network.Name))
                violations.Add(new(NodeErrorCode.ValidationFailed,
                    $"network name '{network.Name}' is not a valid Docker network name.", nameof(network.Name)));

        foreach (var container in spec.Containers)
            ValidateContainer(container, declaredVolumes, hasNodeAdminScope, violations);

        foreach (var route in spec.HttpRoutes)
            ValidateHttpRoute(route, spec, violations);

        foreach (var route in spec.TcpRoutes)
            ValidateTcpRoute(route, spec, violations);

        return violations;
    }

    private void ValidateContainer(
        ContainerSpec container, IReadOnlySet<string> declaredVolumes, bool hasNodeAdminScope,
        List<PolicyViolation> violations)
    {
        var where = $"containers[{container.Name}]";

        if (!DnsName.IsMatch(container.Name))
            violations.Add(new(NodeErrorCode.ValidationFailed,
                $"container name '{container.Name}' must be a DNS label.", where));

        // --- image pinning ---

        if (string.IsNullOrWhiteSpace(container.Image.Repository))
            violations.Add(new(NodeErrorCode.ValidationFailed, "image repository is required.", $"{where}.image"));

        if (!Digest.IsMatch(container.Image.Digest))
            violations.Add(new(NodeErrorCode.ImageNotPinned,
                $"image '{container.Image.Repository}' must be pinned by a sha256 digest; got '{container.Image.Digest}'. " +
                "A mutable tag cannot express 'deploy what was tested'.",
                $"{where}.image.digest"));

        // --- privilege ---

        var wantsPrivilege = container.Privileged || container.HostNetwork || container.HostPidNamespace;

        if (wantsPrivilege && !security.AllowPrivilegedWorkloads)
            violations.Add(new(NodeErrorCode.PolicyDenied,
                $"{where} asks for privileged mode, host networking or the host PID namespace. " +
                "This node has Security:AllowPrivilegedWorkloads off, so it is refused — including for an admin.",
                where));

        if (wantsPrivilege && security.AllowPrivilegedWorkloads && !hasNodeAdminScope)
            // Two locks, one key each: the machine's owner enables the capability, and only a
            // node-admin command may use it. Either alone would be enough for a tenant to reach it.
            violations.Add(new(NodeErrorCode.Unauthorized,
                $"{where} asks for privileged mode, which requires a command carrying the '{NodeScopes.NodeAdmin}' scope.",
                where));

        foreach (var capability in container.CapabilitiesAdd)
            if (security.DeniedCapabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
                violations.Add(new(NodeErrorCode.PolicyDenied,
                    $"{where} asks for capability {capability}, which is on this node's deny-list.", where));

        // --- mounts ---

        foreach (var mount in container.Mounts)
        {
            if (!VolumeName.IsMatch(mount.VolumeName))
            {
                violations.Add(new(NodeErrorCode.PolicyDenied,
                    $"{where} mounts '{mount.VolumeName}', which is not a plain volume name. " +
                    "A value containing a path separator would become a host bind mount.",
                    $"{where}.mounts"));
                continue;
            }

            if (!declaredVolumes.Contains(mount.VolumeName))
                violations.Add(new(NodeErrorCode.ValidationFailed,
                    $"{where} mounts volume '{mount.VolumeName}', which the spec does not declare.",
                    $"{where}.mounts"));

            if (!IsAbsolute(mount.MountPath))
                violations.Add(new(NodeErrorCode.ValidationFailed,
                    $"{where} mount path '{mount.MountPath}' must be absolute.", $"{where}.mounts"));
            else if (IsProtectedContainerPath(mount.MountPath))
                violations.Add(new(NodeErrorCode.PolicyDenied,
                    $"{where} would mount over '{mount.MountPath}' inside the container.", $"{where}.mounts"));
        }

        // --- secrets ---

        foreach (var secret in container.Secrets)
        {
            if (string.IsNullOrWhiteSpace(secret.Name))
                violations.Add(new(NodeErrorCode.ValidationFailed, $"{where} has a secret with no name.", $"{where}.secrets"));

            if (secret.MountAs != SecretMount.File) continue;

            if (!IsAbsolute(secret.TargetPath))
                violations.Add(new(NodeErrorCode.ValidationFailed,
                    $"{where} secret '{secret.Name}' is file-mounted and needs an absolute targetPath.", $"{where}.secrets"));
            else if (IsProtectedContainerPath(secret.TargetPath!))
                violations.Add(new(NodeErrorCode.PolicyDenied,
                    $"{where} secret '{secret.Name}' would be written to '{secret.TargetPath}'.", $"{where}.secrets"));
        }

        // --- resources ---

        if (container.Resources.MemoryBytes <= 0 && !hasNodeAdminScope)
            // An unlimited container on a shared node is a single tenant's memory leak taking the
            // whole box down, including every other tenant on it.
            violations.Add(new(NodeErrorCode.ValidationFailed,
                $"{where} must declare a memory limit.", $"{where}.resources.memoryBytes"));

        if (container.Resources.CpuCores < 0)
            violations.Add(new(NodeErrorCode.ValidationFailed,
                $"{where} cpuCores cannot be negative.", $"{where}.resources.cpuCores"));

        // --- ports ---

        foreach (var port in container.Ports)
        {
            if (port.ContainerPort is < 1 or > 65535)
                violations.Add(new(NodeErrorCode.ValidationFailed,
                    $"{where} container port {port.ContainerPort} is outside 1–65535.", $"{where}.ports"));

            if (port.Protocol is not ("tcp" or "udp"))
                violations.Add(new(NodeErrorCode.ValidationFailed,
                    $"{where} port protocol '{port.Protocol}' must be tcp or udp.", $"{where}.ports"));

            if (port.HostPort is not { } hostPort) continue;

            if (hostPort < ports.Start || hostPort > ports.End)
                violations.Add(new(NodeErrorCode.PolicyDenied,
                    $"{where} asks for host port {hostPort}, outside this node's allocation range {ports.Start}–{ports.End}.",
                    $"{where}.ports"));
        }
    }

    private static void ValidateHttpRoute(HttpRouteSpec route, WorkloadSpec spec, List<PolicyViolation> violations)
    {
        if (spec.Containers.All(c => c.Name != route.TargetContainer))
            violations.Add(new(NodeErrorCode.ValidationFailed,
                $"http route '{route.RouteId}' targets container '{route.TargetContainer}', which this workload does not define.",
                nameof(spec.HttpRoutes)));

        if (string.IsNullOrWhiteSpace(route.Domain) || route.Domain.Contains('/'))
            violations.Add(new(NodeErrorCode.ValidationFailed,
                $"http route '{route.RouteId}' has an invalid domain '{route.Domain}'.", nameof(spec.HttpRoutes)));

        if (route.TargetPort is < 1 or > 65535)
            violations.Add(new(NodeErrorCode.ValidationFailed,
                $"http route '{route.RouteId}' has an invalid target port.", nameof(spec.HttpRoutes)));
    }

    private static void ValidateTcpRoute(TcpRouteSpec route, WorkloadSpec spec, List<PolicyViolation> violations)
    {
        if (spec.Containers.All(c => c.Name != route.TargetContainer))
            violations.Add(new(NodeErrorCode.ValidationFailed,
                $"tcp route '{route.RouteId}' targets container '{route.TargetContainer}', which this workload does not define.",
                nameof(spec.TcpRoutes)));

        if (route.TargetPort is < 1 or > 65535)
            violations.Add(new(NodeErrorCode.ValidationFailed,
                $"tcp route '{route.RouteId}' has an invalid target port.", nameof(spec.TcpRoutes)));
    }

    /// <summary>
    /// Whether a host path is one the agent will never expose. Kept public because the isolated
    /// Docker workspace needs the same answer for the paths it would otherwise be tempted to share.
    /// </summary>
    public bool IsDeniedHostPath(string path)
    {
        var normalised = NormalisePath(path);

        return security.DeniedHostPaths
            .Select(NormalisePath)
            .Any(denied => normalised == denied ||
                           (denied != "/" && normalised.StartsWith(denied + "/", StringComparison.Ordinal)) ||
                           denied == "/");
    }

    /// <summary>
    /// Collapse <c>.</c>, <c>..</c> and duplicate separators so <c>/var/run/../run/docker.sock</c>
    /// is the same string to the policy as the obvious spelling. Comparing raw text would let a
    /// deny-list be walked around with three extra characters.
    /// </summary>
    internal static string NormalisePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";

        var segments = new List<string>();

        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;

            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return "/" + string.Join('/', segments);
    }

    private static bool IsAbsolute(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith('/');

    private static bool IsProtectedContainerPath(string path)
    {
        var normalised = NormalisePath(path);

        if (ProtectedContainerFiles.Contains(normalised, StringComparer.Ordinal)) return true;

        return ProtectedContainerTrees.Any(tree =>
            normalised == tree ||
            (tree != "/" && normalised.StartsWith(tree + "/", StringComparison.Ordinal)));
    }
}
