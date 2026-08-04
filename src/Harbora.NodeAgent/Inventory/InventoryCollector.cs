using Harbora.NodeAgent.Commands;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Inventory;

/// <summary>
/// Assembles what the control plane is told about this node: the static inventory, the capability
/// report, and the small volatile slice that rides on every heartbeat.
/// </summary>
public sealed class InventoryCollector(
    IOptions<NodeAgentOptions> options,
    IHostFacts host,
    IContainerRuntime runtime,
    ImplementedCommands implemented,
    ILogger<InventoryCollector> log)
{
    private readonly NodeAgentOptions _options = options.Value;

    public async Task<NodeInventory> CollectAsync(CancellationToken ct)
    {
        var runtimeInfo = await SafeRuntimeInfoAsync(ct);
        var disk = host.Disk(_options.DataDirectory);

        return new NodeInventory
        {
            NodeName = _options.NodeName,
            Hostname = host.Hostname,
            OsName = host.OsName,
            OsVersion = host.OsVersion,
            KernelVersion = host.KernelVersion,
            Architecture = host.Architecture,
            ContainerRuntime = runtimeInfo.Name,
            ContainerRuntimeVersion = runtimeInfo.Version,
            CpuCores = host.CpuCores,
            TotalMemoryBytes = host.TotalMemoryBytes,
            TotalDiskBytes = disk.TotalBytes,
            FreeDiskBytes = disk.FreeBytes,
            IpAddresses = host.IpAddresses(),
            Region = _options.Region,
            Environment = _options.Environment,
            Labels = _options.Labels,
            AvailablePortRange = new PortRange(_options.Ports.Start, _options.Ports.End),
            UsedPorts = host.ListeningPorts(),
            ManagedNetworks = await ManagedNetworksAsync(ct),
            Storage = new StorageCapacity(disk.TotalBytes, disk.FreeBytes, _options.DataDirectory),
        };
    }

    /// <summary>
    /// What this build can do — answered from the code, not from a config file. A capability the
    /// operator could turn on by editing a value would be a capability the control plane could be
    /// told about and then find missing.
    /// </summary>
    public NodeCapabilities Capabilities() => new()
    {
        AgentVersion = AgentVersion.Current,
        SupportedProtocolVersions = NodeContract.SupportedProtocolVersions,
        // What is actually wired up, not what the contract names. A control plane that sends a verb
        // this build does not implement gets a refusal, and telling it in advance is cheaper.
        SupportedCommands = implemented.Names,
        SupportedDatabaseEngines = DatabaseEngines.All,
        SupportsComposeStacks = true,
        SupportsRollingUpdate = true,
        SupportsVolumeSnapshots = true,
        SupportsTcpTunnel = true,
        SupportsHttpIngressTunnel = true,
        SupportsIsolatedDockerWorkspace = true,
        PrivilegedModeEnabled = _options.Security.AllowPrivilegedWorkloads,
        SupportsSelfUpdate = OperatingSystem.IsLinux(),
    };

    /// <summary>
    /// Whether this node can run a workload at all. An unrecognised architecture is not a warning:
    /// pulling an image for the wrong platform produces a container that starts and immediately
    /// dies with an exec-format error, and the deploy looks like an application bug.
    /// </summary>
    public bool ArchitectureIsSupported() =>
        host.Architecture is "amd64" or "arm64";

    private async Task<RuntimeInfo> SafeRuntimeInfoAsync(CancellationToken ct)
    {
        try
        {
            return await runtime.GetInfoAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A node whose Docker is down still has to report in — that is how the panel learns the
            // Docker is down. Failing to build an inventory would look identical to being offline.
            log.LogWarning(e, "Container runtime is not answering; reporting the node as runtime-degraded.");
            return new RuntimeInfo("unknown", "unknown", "unknown", 0, Available: false, e.Message);
        }
    }

    private async Task<IReadOnlyList<string>> ManagedNetworksAsync(CancellationToken ct)
    {
        try
        {
            var containers = await runtime.ListContainersAsync(
                new Dictionary<string, string> { [NodeLabels.Managed] = "true" }, includeStopped: true, ct);

            return containers
                .SelectMany(c => c.NetworkIpAddresses.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogDebug(e, "Could not enumerate managed networks.");
            return [];
        }
    }
}

/// <summary>
/// Labels the agent stamps on everything it creates.
///
/// <para>
/// These are load-bearing, not decorative. <see cref="Managed"/> is how the agent tells its own
/// containers from whatever else the machine's owner runs — without it a cleanup pass would be a
/// cleanup of the host. <see cref="Tenant"/> is how cross-tenant reads are refused even when a
/// command carries the wrong workload id.
/// </para>
/// </summary>
public static class NodeLabels
{
    public const string Managed = "io.harbora.managed";
    public const string Tenant = "io.harbora.tenant";
    public const string Workload = "io.harbora.workload";
    public const string App = "io.harbora.app";
    public const string AppVersion = "io.harbora.app-version";
    public const string Container = "io.harbora.container";
    public const string Release = "io.harbora.release";
    public const string Grant = "io.harbora.grant";
    public const string Workspace = "io.harbora.workspace";

    public static Dictionary<string, string> For(string tenantId, string workloadId) => new()
    {
        [Managed] = "true",
        [Tenant] = tenantId,
        [Workload] = workloadId,
    };
}
