using System.Collections.Concurrent;
using Harbora.Application.Abstractions;
using Harbora.NodeAgent.Contracts;
using Microsoft.Extensions.Logging;
using NodeContracts = Harbora.NodeAgent.Contracts;

namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// Runs the platform's deployment pipeline against a v1 node.
///
/// <para>
/// The pipeline speaks <see cref="IDockerEngine"/> — pull, run, list, remove — because that is what
/// the local engine and the old inbound agent both are. A v1 node speaks twenty-two named verbs and
/// deliberately does not offer a Docker API, so this class is the translation between the two.
/// </para>
///
/// <para>
/// Most of it maps cleanly. Some of it cannot, and those methods say so rather than pretending:
/// there is no build verb, no image-listing verb and no one-off-container verb, because adding them
/// would put an arbitrary-execution shaped hole in the thing the whole contract exists to prevent.
/// An app that needs one of them cannot run on a v1 node today, and the failure names which one and
/// why — which is the difference between a limitation and a bug.
/// </para>
///
/// <para>
/// One identity trick makes the rest work: the pipeline's container name doubles as the node's
/// workload id, and this engine returns that id where Docker would return a container id. So the
/// pipeline's "stop the container I just got back" and "retire the containers with this app's
/// label" both land on the right workload without the pipeline knowing anything changed.
/// </para>
/// </summary>
public sealed class NodeWorkloadEngine(
    string nodeId,
    NodeCommandService commands,
    ImageDigestResolver digests,
    NodeHostFacts host,
    ILogger logger) : IDockerEngine
{
    /// <summary>
    /// The tenant every panel-scheduled workload on a node belongs to.
    ///
    /// <para>
    /// Not the workspace, deliberately. <see cref="IDockerEngine"/> is server-scoped: the metrics
    /// sweep lists every container on a machine, a backup reaches whichever workspace owns the
    /// volume, and a cutover retires containers the current request has no workspace context for.
    /// A per-workspace tenant would make each of those a lookup the interface cannot express, and
    /// the first one to get it wrong would silently see nothing — which on the retire path means
    /// cutting traffic over and leaving the old container running.
    /// </para>
    ///
    /// <para>
    /// So the boundary the node enforces here is "Harbora put this on you" rather than "workspace X
    /// put this on you". Workspace isolation is not weakened by that: it is enforced in the panel by
    /// the query filter that decides which app a request may act on at all, and on the node by the
    /// per-workspace networks the pipeline already names. What the node's tenant check still buys is
    /// the thing it was for — a node refuses a workload it was never given, whoever asks.
    /// </para>
    /// </summary>
    public const string PlatformTenant = "harbora-platform";

    /// <summary>
    /// Digests resolved during this deployment, so the run does not re-resolve what the pull already
    /// looked up — and, more importantly, cannot resolve a moving tag to something different between
    /// the two calls.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _pinned = new(StringComparer.Ordinal);

    // --- images ---

    public async Task PullImageAsync(string image, IProgress<string> log, CancellationToken ct)
    {
        // The node pulls as part of DeployWorkload; there is no standalone pull verb, and there does
        // not need to be. What this call is really for is resolving the tag while the pipeline is
        // still in its build phase, so a bad reference fails before anything is torn down.
        log.Report($"Resolving {image} to a digest for node {nodeId} …");

        var pinned = await digests.ResolveAsync(image, ct);
        _pinned[image] = pinned;

        log.Report($"{image} is {pinned.Split('@')[^1]}");
        log.Report("The node pulls it as part of the deployment.");
    }

    public async Task<bool> ImageExistsAsync(string imageRef, CancellationToken ct)
    {
        if (_pinned.ContainsKey(imageRef)) return true;

        try
        {
            _pinned[imageRef] = await digests.ResolveAsync(imageRef, ct);
            return true;
        }
        catch (ImageDigestResolver.UnresolvableImageException)
        {
            // "Cannot be resolved" is the honest answer to "does it exist", and it is the answer
            // that makes the pipeline's rollback path refuse to promise an instant re-release.
            return false;
        }
    }

    /// <summary>
    /// Empty, always. Image retention is the node's own business, and the panel pruning images on a
    /// machine it cannot enumerate would be guessing. Returning nothing makes the pipeline's prune
    /// step a no-op rather than an error.
    /// </summary>
    public Task<IReadOnlyList<ImageInfo>> ListImagesAsync(string? tagPrefix, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ImageInfo>>([]);

    public Task RemoveImageAsync(string imageRef, CancellationToken ct)
    {
        logger.LogDebug("Ignoring an image removal for node {NodeId}; a v1 node manages its own images.", nodeId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Empty, which the pipeline already treats as "we cannot tell" rather than "it listens
    /// nowhere" — so the app's configured port is kept.
    /// </summary>
    public Task<IReadOnlyList<int>> GetImagePortsAsync(string imageRef, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<int>>([]);

    public Task<string> BuildImageAsync(DockerBuildRequest request, IProgress<string> log, CancellationToken ct) =>
        throw new NodeCapabilityException(
            nodeId,
            "build an image",
            "A v1 node has no build verb, deliberately: accepting a build context would mean accepting " +
            "arbitrary code and an arbitrary Dockerfile, which is the exact capability the node contract " +
            "exists to withhold. Deploy this app from a prebuilt image or a template, or place it on a " +
            "server running the inbound agent.");

    // --- containers ---

    public async Task<string> RunContainerAsync(DockerRunRequest request, CancellationToken ct)
    {
        var image = _pinned.TryGetValue(request.Image, out var already)
            ? already
            : await digests.ResolveAsync(request.Image, ct);

        var reference = ImageRefFrom(image);

        var spec = new WorkloadSpec
        {
            // The pipeline's container name is stable across a release and unique per deployment,
            // which is exactly what a workload id has to be.
            WorkloadId = request.ContainerName,
            Name = SanitiseName(request.ContainerName),
            TenantId = PlatformTenant,
            AppId = request.Labels.GetValueOrDefault("harbora.app"),
            AppVersion = request.Labels.GetValueOrDefault("harbora.deployment"),
            Labels = request.Labels.ToDictionary(kv => kv.Key, kv => kv.Value),
            Networks = [new NetworkSpec { Name = request.NetworkName }],
            Volumes = request.Volumes
                .Select(v => new VolumeSpec { Name = v.VolumeName })
                .DistinctBy(v => v.Name)
                .ToList(),
            Containers =
            [
                new ContainerSpec
                {
                    Name = "app",
                    Image = reference,
                    Command = request.Command?.ToList(),
                    // Everything the panel calls an environment variable is a potential secret: the
                    // panel decrypted them to get here, and the node redacts what it is told is
                    // sensitive. Sending them as secrets rather than plain env means the node
                    // registers them with its redactor before they can reach a log line.
                    Secrets = request.Env
                        .Select(kv => new SecretSpec { Name = kv.Key, Value = kv.Value })
                        .ToList(),
                    Mounts = request.Volumes
                        .Select(v => new MountSpec { VolumeName = v.VolumeName, MountPath = v.MountPath, ReadOnly = v.ReadOnly })
                        .ToList(),
                    NetworkAliases = request.NetworkAliases?.ToList() ?? [],
                    Ports = request.ContainerPort is { } port
                        ?
                        [
                            new PortMapping
                            {
                                ContainerPort = port,
                                // A node is reachable from the panel's proxy only through a published
                                // host port; there is no shared overlay between them.
                                PublishToHost = true,
                                HostPort = request.PublishToHostPort,
                            },
                        ]
                        : [],
                    Resources = new ResourceLimits
                    {
                        MemoryBytes = request.MemoryLimitBytes,
                        CpuCores = request.CpuLimit,
                    },
                    HealthCheck = request.HealthCheckPath is { Length: > 0 } path && request.ContainerPort is { } probePort
                        ? new HealthCheckSpec { Kind = HealthCheckKind.Http, Path = path, Port = probePort }
                        : null,
                },
            ],
            // Recreate rather than blue/green: the pipeline runs its own cutover, keeping the old
            // container alive until the new one is healthy. Two cutover schemes on top of each other
            // would leave twice the containers and no clear owner of either.
            Upgrade = new UpgradeStrategy { Mode = UpgradeMode.Recreate, AutoRollbackOnFailure = false },
        };

        var outcome = await commands.SendAsync(
            nodeId, NodeContracts.NodeCommands.DeployWorkload,
            new DeployWorkloadRequest { Spec = spec },
            idempotencyKey: $"deploy:{request.ContainerName}",
            reason: $"deploy {spec.AppId ?? spec.Name}",
            tenantScope: PlatformTenant,
            ct: ct);

        if (!outcome.Succeeded)
            throw new NodeCommandFailedException(nodeId, NodeContracts.NodeCommands.DeployWorkload, outcome);

        var result = outcome.ResultAs<DeployWorkloadResult>();

        logger.LogInformation(
            "Node {NodeId} deployed workload {Workload} ({Digest}).",
            nodeId, request.ContainerName, result?.ResolvedDigests.Values.FirstOrDefault() ?? "unknown digest");

        // The workload id, not a Docker container id. Everything downstream treats it as opaque and
        // hands it back to Stop/Remove/Restart, which is exactly what we want.
        return request.ContainerName;
    }

    public Task StopContainerAsync(string containerId, CancellationToken ct) =>
        SendAsync(NodeContracts.NodeCommands.StopWorkload,
            new WorkloadRequest { WorkloadId = containerId, TenantId = PlatformTenant },
            $"stop:{containerId}", ct);

    public Task RestartContainerAsync(string containerId, CancellationToken ct) =>
        SendAsync(NodeContracts.NodeCommands.RestartWorkload,
            new WorkloadRequest { WorkloadId = containerId, TenantId = PlatformTenant },
            // Not idempotent across time: "restart it again" is a different intent from "restart it",
            // so the key carries the moment rather than only the target.
            $"restart:{containerId}:{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", ct);

    public Task RemoveContainerAsync(string containerId, bool force, CancellationToken ct) =>
        SendAsync(NodeContracts.NodeCommands.DeleteWorkload,
            // Volumes survive. The pipeline removes a retired container on every cutover, and the
            // data it was serving belongs to the app, not to that release.
            new DeleteWorkloadRequest { WorkloadId = containerId, TenantId = PlatformTenant, DeleteVolumes = false, Force = force },
            $"delete:{containerId}", ct);

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(string? labelFilter, CancellationToken ct)
    {
        var outcome = await commands.SendAsync(
            nodeId, NodeContracts.NodeCommands.ListWorkloads,
            new ListWorkloadsRequest { TenantId = PlatformTenant },
            idempotencyKey: $"list:{nodeId}:{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            tenantScope: PlatformTenant,
            ct: ct);

        if (!outcome.Succeeded)
        {
            // Returning an empty list would tell the pipeline there is nothing to retire, and it
            // would cut traffic over to a new container while leaving the old one running.
            throw new NodeCommandFailedException(nodeId, NodeContracts.NodeCommands.ListWorkloads, outcome);
        }

        var result = outcome.ResultAs<ListWorkloadsResult>();
        if (result is null) return [];

        return result.Workloads
            // The filter the pipeline passes is a label key ("harbora.app"), and it means "only
            // containers this platform manages" rather than a value match.
            .Where(w => labelFilter is null || w.Labels.ContainsKey(labelFilter.Split('=')[0]))
            .Select(w => new ContainerInfo(
                w.WorkloadId,
                w.WorkloadId,
                w.Status?.Containers.FirstOrDefault()?.Image ?? string.Empty,
                w.Status?.State ?? "unknown",
                w.Status?.Healthy == true ? "healthy" : w.Status?.State ?? "unknown",
                w.Labels.ToDictionary(kv => kv.Key, kv => kv.Value)))
            .ToList();
    }

    /// <summary>
    /// Null, meaning "not measured". The node reports host pressure on its heartbeat; per-container
    /// statistics are not in the contract, and a fabricated zero would draw a flat line across a
    /// container that is actually busy.
    /// </summary>
    public Task<ContainerStats?> GetStatsAsync(string containerId, CancellationToken ct) =>
        Task.FromResult<ContainerStats?>(null);

    // --- logs ---

    public async Task<string> GetLogsAsync(string containerId, int tailLines, CancellationToken ct)
    {
        var lines = new List<string>();
        await StreamAsync(containerId, tailLines, follow: false, new Progress<string>(lines.Add), ct);
        return string.Join('\n', lines);
    }

    public Task StreamLogsAsync(string containerId, IProgress<string> sink, CancellationToken ct) =>
        StreamAsync(containerId, tailLines: 200, follow: true, sink, ct);

    private async Task StreamAsync(
        string workloadId, int tailLines, bool follow, IProgress<string> sink, CancellationToken ct)
    {
        var request = new StreamLogsRequest
        {
            WorkloadId = workloadId,
            TenantId = PlatformTenant,
            TailLines = tailLines,
            Follow = follow,
        };

        await commands.StreamLogsAsync(
            nodeId, request,
            chunk =>
            {
                if (!chunk.Final && chunk.Text.Length > 0) sink.Report(chunk.Text);
                return Task.CompletedTask;
            },
            follow ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(2),
            ct, tenantScope: PlatformTenant);
    }

    // --- networks and volumes ---

    public Task EnsureNetworkAsync(string name, CancellationToken ct) =>
        SendAsync(NodeContracts.NodeCommands.CreateNetwork,
            new NetworkRequest { TenantId = PlatformTenant, Network = new NetworkSpec { Name = name } },
            $"network:{name}", ct);

    public Task EnsureVolumeAsync(string name, CancellationToken ct) =>
        SendAsync(NodeContracts.NodeCommands.CreateVolume,
            new VolumeRequest { TenantId = PlatformTenant, Volume = new VolumeSpec { Name = name } },
            $"volume:{name}", ct);

    public Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        // The contract has no volume-removal verb on purpose: deleting a customer's data is the one
        // operation that cannot be undone, and it happens through DeleteWorkload with an explicit
        // deleteVolumes flag, where the intent is stated rather than inferred.
        logger.LogWarning(
            "Ignoring a standalone volume removal for {Volume} on node {NodeId}. " +
            "Delete the workload with deleteVolumes set instead — the node will not drop data on an inferred intent.",
            name, nodeId);

        return Task.CompletedTask;
    }

    public Task ConnectNetworkAsync(string containerNameOrId, string network, CancellationToken ct)
    {
        // The panel's proxy and the panel itself run on a different machine. Attaching them to a
        // network on the node is not something that could work; traffic reaches a v1 node through
        // the published host port instead, which RunContainerAsync always requests.
        logger.LogDebug(
            "Not attaching {Container} to {Network}: it is on this panel, not on node {NodeId}.",
            containerNameOrId, network, nodeId);

        return Task.CompletedTask;
    }

    // --- one-offs ---

    public Task<int> RunOneOffAsync(DockerOneOffRequest request, IProgress<string>? log, CancellationToken ct) =>
        throw new NodeCapabilityException(
            nodeId,
            "run a one-off container",
            "A v1 node has no verb for running an arbitrary container to completion — that is a shell " +
            "with extra steps, and the contract withholds it on purpose. Release tasks and volume " +
            "backups for an app on a v1 node are not supported yet; see docs/node-agent/merge-notes.md.");

    // --- host ---

    public async Task<HostInfo> GetHostInfoAsync(CancellationToken ct)
    {
        var facts = await host.ForAsync(nodeId, ct)
            ?? throw new NodeCommandFailedException(nodeId, "GetHostInfo", null);

        return facts;
    }

    // --- helpers ---

    private async Task SendAsync(string command, object payload, string idempotencyKey, CancellationToken ct)
    {
        var outcome = await commands.SendAsync(
            nodeId, command, payload, idempotencyKey, tenantScope: PlatformTenant, ct: ct);

        if (!outcome.Succeeded) throw new NodeCommandFailedException(nodeId, command, outcome);
    }

    private static ImageRef ImageRefFrom(string pinnedReference)
    {
        var parts = pinnedReference.Split('@');

        return new ImageRef
        {
            Repository = parts[0],
            Digest = parts.Length > 1 ? parts[1] : throw new InvalidOperationException(
                $"'{pinnedReference}' is not pinned by digest; the resolver should have made it so."),
        };
    }

    /// <summary>
    /// The node validates a workload name as a DNS label, and the pipeline's container names already
    /// are one — but a slug that ends in a digit-and-dash combination could still trip it, and a
    /// deploy failing on a name is a bad way to learn that.
    /// </summary>
    public static string SanitiseName(string containerName)
    {
        var cleaned = new string(containerName
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')
            .ToArray())
            .Trim('-');

        if (cleaned.Length == 0) return "workload";

        return cleaned.Length <= 63 ? cleaned : cleaned[..63].TrimEnd('-');
    }
}

/// <summary>
/// Something the v1 contract deliberately does not offer. Distinct from a failure so a caller can
/// tell "this node refused" from "this node cannot, by design, and here is what to do instead".
/// </summary>
public sealed class NodeCapabilityException(string nodeId, string operation, string explanation)
    : NotSupportedException($"Node {nodeId} cannot {operation}. {explanation}")
{
    public string NodeId { get; } = nodeId;
    public string Operation { get; } = operation;
}

/// <summary>A command the node answered with a failure.</summary>
public sealed class NodeCommandFailedException(string nodeId, string command, NodeCommandOutcome? outcome)
    : Exception(BuildMessage(nodeId, command, outcome))
{
    public string NodeId { get; } = nodeId;
    public string Command { get; } = command;
    public NodeErrorCode? Code { get; } = outcome?.ErrorCode;

    private static string BuildMessage(string nodeId, string command, NodeCommandOutcome? outcome) =>
        outcome is null
            ? $"Node {nodeId} did not answer {command}."
            : $"Node {nodeId} answered {command} with {outcome.Status}" +
              (outcome.ErrorCode is { } code ? $" ({code})" : string.Empty) +
              (outcome.ErrorMessage is { } message ? $": {message}" : ".");
}
