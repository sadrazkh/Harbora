using System.Collections.Concurrent;
using Harbora.Application.Abstractions;

namespace Harbora.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IDockerEngine"/> that records every call in order and simulates a small
/// container world (containers exist, have a state, can be removed).
///
/// The zero-downtime guarantee is a statement about **ordering** — the new container must be running
/// and healthy before the old one is touched — so a fake that only returns canned values cannot
/// verify it. This one records an ordered <see cref="Calls"/> log, which lets tests assert on the
/// sequence of operations rather than just their results.
///
/// <para>
/// <b>Every operation here refuses a cancelled token.</b> Not because a particular test needs it:
/// because the real engine is an HTTP client over the daemon's socket, and a request made with a
/// dead token is a request that never lands. This used to be true of exactly the two methods a
/// cleanup path had been caught getting wrong, which meant the guarantee held only along the routes
/// those tests walked — and the next fix touching a different method could go green against code
/// that reported work the daemon never heard about. The rule belongs to the component, so it is
/// stated once, here, and applied to all of it.
/// </para>
/// </summary>
public sealed class FakeDockerEngine : IDockerEngine
{
    public sealed record Call(string Operation, string Target);

    private readonly List<Call> _calls = [];
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, ContainerInfo> _containers = new();
    private readonly ConcurrentDictionary<string, ImageInfo> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ContainerDetail> _details = new(StringComparer.Ordinal);
    private int _idSeq;

    /// <summary>Every operation, in the order it happened.</summary>
    public IReadOnlyList<Call> Calls { get { lock (_gate) return _calls.ToList(); } }

    /// <summary>Container state reported to the health gate for newly started containers.</summary>
    public string StartedContainerState { get; set; } = "running";

    /// <summary>Status line reported for newly started containers — carries the exit code when one exits.</summary>
    public string StartedContainerStatus { get; set; } = "Up";

    /// <summary>What the container "printed". The health gate reads this to explain a failure.</summary>
    public string ContainerLogs { get; set; } = string.Empty;

    /// <summary>
    /// Per-container output, keyed by container id — what <see cref="GetLogsAsync"/> and
    /// <see cref="GetLogsSinceAsync"/> answer for a specific container, distinct from the single
    /// global <see cref="ContainerLogs"/> the health gate uses. A search across several apps needs
    /// each container to say something different, or a test proving one app's lines never leak into
    /// another's results would have nothing to tell them apart with.
    /// </summary>
    public ConcurrentDictionary<string, string> ContainerLogsById { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Container ids that refuse a time-windowed request — simulating a host whose transport
    /// (<c>RemoteDockerEngine</c>, <c>NodeWorkloadEngine</c> in production) cannot attach a real
    /// timestamp to a stream it did not capture. Mirrors those engines' reliance on the interface's
    /// default <see cref="IDockerEngine.GetLogsSinceAsync"/>, without this fake having to implement
    /// two more transports just to prove the fallback.
    /// </summary>
    public HashSet<string> TimeWindowUnsupportedFor { get; } = new(StringComparer.Ordinal);

    /// <summary>Ports the built/pulled image declares. Empty means the image says nothing.</summary>
    public List<int> ImagePorts { get; } = [];

    public Task<IReadOnlyList<int>> GetImagePortsAsync(string imageRef, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<int>>(ImagePorts.ToList());
    }

    /// <summary>When set, a started container is never listed — as if something removed it.</summary>
    public bool DropStartedContainers { get; set; }

    /// <summary>Transitions a live container to exited, the way a crash mid-health-check would.</summary>
    public void MarkExited(string containerName, string status = "Exited (1) 1 second ago")
    {
        foreach (var (id, c) in _containers)
            if (c.Name == containerName)
                _containers[id] = c with { State = "exited", Status = status };
    }

    /// <summary>When set, <see cref="BuildImageAsync"/> throws — simulates a failing build.</summary>
    public Exception? BuildFailure { get; set; }

    /// <summary>When set, <see cref="RunContainerAsync"/> throws — simulates a container that won't start.</summary>
    public Exception? RunFailure { get; set; }

    /// <summary>Container names whose removal fails — a retired container that resists cleanup.</summary>
    public HashSet<string> UnremovableContainers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// A pull that never returns — a registry that accepted the connection and then stopped talking.
    /// The counterpart of <see cref="OneOffNeverFinishes"/> for the two engines that hang here
    /// rather than in a one-off container.
    /// </summary>
    public bool PullNeverFinishes { get; set; }

    /// <summary>
    /// Cancelled the instant one of those hanging operations is entered.
    ///
    /// <para>
    /// This is a job's deadline firing, expressed as a fact rather than as a wait: the work is
    /// provably underway when the token goes, so a test about what a killed dispatch target records
    /// never races a timer against the setup around it.
    /// </para>
    /// </summary>
    public CancellationTokenSource? DeadlineFiresWhenTheWorkBegins { get; set; }

    /// <summary>
    /// Cancelled the instant a container has been started.
    ///
    /// <para>
    /// The other one fires while the deploy still owns nothing on the node. This one fires in the
    /// window that comes after — the build and the pull have eaten the budget, a container is
    /// running, and the clock runs out during the health check. That window is the only one in which
    /// giving up can LEAVE something behind, so it is the one the cleanup guarantees are about.
    /// </para>
    /// </summary>
    public CancellationTokenSource? DeadlineFiresOnceTheContainerIsUp { get; set; }

    /// <summary>
    /// Cancelled the instant image retention starts looking at the node.
    ///
    /// <para>
    /// The third window, and the only one that opens AFTER the deployment has been durably recorded
    /// as succeeded: the cutover is done, the release is live, and what is left running is
    /// housekeeping. A deadline that fires here is firing on a deployment that worked, so it is the
    /// window in which "the job ran out of time" and "the deployment failed" are different facts.
    /// </para>
    /// <para>
    /// The call itself then fails, rather than returning and leaving the next await to notice: the
    /// real engine is an HTTP request to the daemon, and a token that dies mid-request takes the
    /// request with it. Same shape as <see cref="PullNeverFinishes"/> above, without the wait.
    /// </para>
    /// </summary>
    public CancellationTokenSource? DeadlineFiresWhenImagesAreListed { get; set; }

    // ---- assertions helpers ----

    public IReadOnlyList<string> OperationsOn(string target) =>
        Calls.Where(c => c.Target == target).Select(c => c.Operation).ToList();

    /// <summary>Index of the first matching call, or -1. Used to assert relative ordering.</summary>
    public int IndexOf(string operation, string? target = null) =>
        Calls.ToList().FindIndex(c => c.Operation == operation && (target is null || c.Target == target));

    public int CountOf(string operation) => Calls.Count(c => c.Operation == operation);

    /// <summary>Names of containers still present — i.e. what would still be serving traffic.</summary>
    public IReadOnlyList<string> LiveContainerNames =>
        _containers.Values.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>Image tags still on the node — i.e. what rollback can still reach.</summary>
    public IReadOnlyList<string> StoredImageTags =>
        _images.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();

    /// <summary>Puts an image on the node as if a previous build or pull had left it there.</summary>
    public FakeDockerEngine SeedImage(params string[] tags)
    {
        foreach (var tag in tags)
            _images[tag] = new ImageInfo($"sha256:{tag.GetHashCode():x8}", tag, DateTimeOffset.UnixEpoch, 1024);
        return this;
    }

    /// <summary>
    /// Silently drops images from the node, as if something outside Harbora had reclaimed them
    /// (`docker image prune`, disk cleanup, a rebuilt host). Not recorded — nothing in the platform
    /// performed this.
    /// </summary>
    public FakeDockerEngine ForgetImage(params string[] tags)
    {
        foreach (var tag in tags) _images.TryRemove(tag, out _);
        return this;
    }

    /// <summary>Image tags whose removal fails — e.g. still referenced by a container.</summary>
    public HashSet<string> UndeletableImages { get; } = new(StringComparer.Ordinal);

    /// <summary>Seeds a container as if a previous deployment had left it running.</summary>
    /// <param name="composeService">
    /// The compose service name this container answers to, when it came from a stack. That label is
    /// the only place the name survives — ComposeFile is parsed at deploy time and never stored — so
    /// it is what the collision check reads.
    /// </param>
    /// <param name="appId">
    /// The owning app's id, carried as <c>harbora.app.id</c> — what the collision check actually
    /// matches siblings by, since the slug label alone is only unique per workspace.
    /// </param>
    /// <param name="workspaceId">
    /// Carried as <c>harbora.workspace</c> — what <c>DeploymentPlanning.ContainersToRetire</c> and
    /// <c>CurrentContainerId</c> actually match ownership on (2026-08-15-unique-app-names-design).
    /// Null simulates a container that predates that label, exercising the legacy bridge.
    /// </param>
    public string SeedContainer(string name, string slug, string state = "running",
        string image = "img:old", string? composeService = null, Guid? appId = null, Guid? workspaceId = null)
    {
        var id = $"container-{Interlocked.Increment(ref _idSeq):D4}-{name}";
        var labels = new Dictionary<string, string>
        {
            ["harbora.managed"] = "true",
            ["harbora.app"] = slug
        };
        if (appId is not null) labels["harbora.app.id"] = appId.Value.ToString();
        if (workspaceId is not null) labels["harbora.workspace"] = workspaceId.Value.ToString();
        if (composeService is not null) labels["harbora.compose.service"] = composeService;

        _containers[id] = new ContainerInfo(id, name, image, state, "Up", labels);
        return id;
    }

    /// <summary>Seeds what <see cref="InspectAsync"/> answers for a container name — tests must never
    /// construct the expected detail inside the assertion itself.</summary>
    public FakeDockerEngine SeedDetail(string name, ContainerDetail detail)
    {
        _details[name] = detail;
        return this;
    }

    private void Record(string operation, string target)
    {
        lock (_gate) _calls.Add(new Call(operation, target));
    }

    // ---- IDockerEngine ----

    public Task<string> BuildImageAsync(DockerBuildRequest request, IProgress<string> log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(BuildImageAsync), request.ImageTag);
        BuildRequests.Add(request);
        if (BuildFailure is not null) throw BuildFailure;
        SeedImage(request.ImageTag);
        log.Report($"built {request.ImageTag}");
        return Task.FromResult(request.ImageTag);
    }

    /// <summary>Every build request, so a test can assert on build args as well as the image tag.</summary>
    public List<DockerBuildRequest> BuildRequests { get; } = [];

    public async Task PullImageAsync(string image, IProgress<string> log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(PullImageAsync), image);

        if (PullNeverFinishes)
        {
            DeadlineFiresWhenTheWorkBegins?.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
        }

        SeedImage(image);
        log.Report($"pulled {image}");
    }

    public Task<IReadOnlyList<ImageInfo>> ListImagesAsync(string? tagPrefix, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (DeadlineFiresWhenImagesAreListed is { } retentionDeadline)
        {
            retentionDeadline.Cancel();
            ct.ThrowIfCancellationRequested();
        }

        // Not recorded: like ListContainersAsync this is a query, and recording it would bury the
        // ordering assertions in noise.
        IReadOnlyList<ImageInfo> snapshot = _images.Values
            .Where(i => tagPrefix is null || i.Tag.StartsWith(tagPrefix, StringComparison.Ordinal))
            .ToList();
        return Task.FromResult(snapshot);
    }

    public Task<bool> ImageExistsAsync(string imageRef, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_images.ContainsKey(imageRef));
    }

    public Task RemoveImageAsync(string imageRef, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(RemoveImageAsync), imageRef);
        if (UndeletableImages.Contains(imageRef))
            throw new InvalidOperationException($"image {imageRef} is in use by a container");
        _images.TryRemove(imageRef, out _);
        return Task.CompletedTask;
    }

    public Task<string> RunContainerAsync(DockerRunRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(RunContainerAsync), request.ContainerName);
        if (RunFailure is not null) throw RunFailure;

        var id = $"container-{Interlocked.Increment(ref _idSeq):D4}-{request.ContainerName}";
        if (!DropStartedContainers)
            _containers[id] = new ContainerInfo(
                id, request.ContainerName, request.Image, StartedContainerState, StartedContainerStatus,
                request.Labels.ToDictionary(kv => kv.Key, kv => kv.Value));
        RunRequests.Add(request);

        // After the container exists, not before: a deadline that fires here leaves something on the
        // node, which is the whole point of firing it here.
        DeadlineFiresOnceTheContainerIsUp?.Cancel();

        return Task.FromResult(id);
    }

    /// <summary>Every run request, so tests can assert on ports, env, labels and volumes.</summary>
    public List<DockerRunRequest> RunRequests { get; } = [];

    public Task StopContainerAsync(string containerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(StopContainerAsync), NameOf(containerId));
        return Task.CompletedTask;
    }

    public Task RemoveContainerAsync(string containerId, bool force, CancellationToken ct)
    {
        // The class rule. This is the method where it was first missing, and where the cost of
        // missing it was a cleanup path passing its dead token to the engine and still looking like
        // it had cleaned up.
        ct.ThrowIfCancellationRequested();

        var name = NameOf(containerId);
        Record(nameof(RemoveContainerAsync), name);
        if (UnremovableContainers.Contains(name))
            throw new InvalidOperationException($"container {name} is in use");
        _containers.TryRemove(containerId, out _);
        return Task.CompletedTask;
    }

    public Task RestartContainerAsync(string containerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(RestartContainerAsync), NameOf(containerId));
        return Task.CompletedTask;
    }

    public Task StreamLogsAsync(string containerId, IProgress<string> sink, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<string> GetLogsAsync(string containerId, int tailLines, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ContainerLogsById.TryGetValue(containerId, out var logs) ? logs : ContainerLogs);
    }

    public Task<IReadOnlyList<TimedLogLine>> GetLogsSinceAsync(
        string containerId, DateTimeOffset since, int maxLines, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (TimeWindowUnsupportedFor.Contains(containerId))
            throw new NotSupportedException(
                "This fake container's host cannot attach real timestamps (simulated).");

        var raw = ContainerLogsById.TryGetValue(containerId, out var logs) ? logs : ContainerLogs;
        var parsed = Harbora.Infrastructure.Docker.DockerTimestampedLog.Parse(raw);
        return Task.FromResult<IReadOnlyList<TimedLogLine>>(
            parsed.Where(l => l.Timestamp >= since).Take(maxLines).ToList());
    }

    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(string? labelFilter, CancellationToken ct)
    {
        // The class rule again — cleanup that lists before it removes has to be watched failing.
        ct.ThrowIfCancellationRequested();

        // Deliberately NOT recorded: listing is a query the pipeline makes repeatedly while polling
        // for health, and it would drown the ordering assertions in noise.
        IReadOnlyList<ContainerInfo> snapshot = _containers.Values
            .Where(c => labelFilter is null || c.Labels.ContainsKey(labelFilter))
            .ToList();
        return Task.FromResult(snapshot);
    }

    public Task<ContainerStats?> GetStatsAsync(string containerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<ContainerStats?>(null);
    }

    /// <summary>Whatever was seeded for this name with <see cref="SeedDetail"/>, or null — a
    /// container the fake has never heard of answers the same way the real engines do.</summary>
    public Task<ContainerDetail?> InspectAsync(string containerNameOrId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_details.TryGetValue(containerNameOrId, out var detail) ? detail : null);
    }

    /// <summary>Projects whatever <see cref="SeedDetail"/> seeded — a test that wants "the engine
    /// declined to answer" for lifecycle purposes just seeds no detail, same as for InspectAsync.</summary>
    public Task<ContainerLifecycle?> GetLifecycleAsync(string containerNameOrId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_details.TryGetValue(containerNameOrId, out var detail)
            ? new ContainerLifecycle(detail.RestartCount, detail.StartedAt)
            : null);
    }

    public Task EnsureNetworkAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(EnsureNetworkAsync), name);
        return Task.CompletedTask;
    }

    public Task ConnectNetworkAsync(string containerNameOrId, string network, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(ConnectNetworkAsync), containerNameOrId);
        lock (_gate) _attachments.Add((containerNameOrId, network));
        return Task.CompletedTask;
    }

    private readonly List<(string Container, string Network)> _attachments = [];

    /// <summary>Networks a container was attached to after it was created — the dual-attach path.</summary>
    public IReadOnlyList<string> ConnectedNetworks(string containerName)
    {
        lock (_gate) return _attachments.Where(a => a.Container == containerName).Select(a => a.Network).ToList();
    }

    public Task EnsureVolumeAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(EnsureVolumeAsync), name);
        return Task.CompletedTask;
    }

    /// <summary>Volume names whose removal fails — a daemon that refuses, or a volume still in use.
    /// Mirrors <see cref="UnremovableContainers"/> and <see cref="UndeletableImages"/> for the third
    /// kind of thing this engine can be told to resist removing.</summary>
    public HashSet<string> UnremovableVolumes { get; } = new(StringComparer.Ordinal);

    public Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(RemoveVolumeAsync), name);
        if (UnremovableVolumes.Contains(name))
            throw new InvalidOperationException($"volume {name} could not be removed");
        return Task.CompletedTask;
    }

    public async Task<int> RunOneOffAsync(DockerOneOffRequest request, IProgress<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(nameof(RunOneOffAsync), request.Image);
        OneOffCommands.Add(string.Join(' ', request.Command));
        OneOffRequests.Add(request);

        foreach (var line in OneOffOutput) log?.Report(line);

        if (OneOffThrows is not null) throw OneOffThrows;
        // A container that never exits — the caller is expected to bound its own wait.
        if (OneOffNeverFinishes)
        {
            DeadlineFiresWhenTheWorkBegins?.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
        }

        return OneOffExitCode;
    }

    /// <summary>Every one-off command line, so destructive operations can be asserted on.</summary>
    public List<string> OneOffCommands { get; } = [];

    /// <summary>The full requests, for asserting on environment and network as well as the command.</summary>
    public List<DockerOneOffRequest> OneOffRequests { get; } = [];

    public int OneOffExitCode { get; set; }

    /// <summary>A one-off that runs for ever — a command waiting for input, or one that was never
    /// actually started because the image's entrypoint swallowed it.</summary>
    public bool OneOffNeverFinishes { get; set; }

    /// <summary>Raised instead of running, for the failures Docker itself reports.</summary>
    public Exception? OneOffThrows { get; set; }

    /// <summary>What the one-off prints, exactly as the engine hands it over — framing bytes and all.</summary>
    public List<string> OneOffOutput { get; } = [];

    /// <summary>A fake offers no shell — a test that reaches here meant something else.</summary>
    public Task<IContainerExec> ExecAsync(
        string containerId, IReadOnlyList<string> command, int columns, int rows, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>Total disk <see cref="GetHostInfoAsync"/> reports. Defaults to the original fixed 100 GB.</summary>
    public long TotalDiskBytes { get; set; } = 100L << 30;

    /// <summary>Free disk <see cref="GetHostInfoAsync"/> reports. Defaults to the original fixed 50 GB
    /// (50% used) — settable so a test can drive the disk-warning threshold either side of a
    /// configured ratio without inventing a second fake.</summary>
    public long FreeDiskBytes { get; set; } = 50L << 30;

    public Task<HostInfo> GetHostInfoAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new HostInfo(4, 8L << 30, TotalDiskBytes, FreeDiskBytes, "fake", _containers.Count));
    }

    private string NameOf(string containerId) =>
        _containers.TryGetValue(containerId, out var c) ? c.Name : containerId;
}
