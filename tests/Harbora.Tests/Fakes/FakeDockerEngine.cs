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
/// </summary>
public sealed class FakeDockerEngine : IDockerEngine
{
    public sealed record Call(string Operation, string Target);

    private readonly List<Call> _calls = [];
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, ContainerInfo> _containers = new();
    private int _idSeq;

    /// <summary>Every operation, in the order it happened.</summary>
    public IReadOnlyList<Call> Calls { get { lock (_gate) return _calls.ToList(); } }

    /// <summary>Container state reported to the health gate for newly started containers.</summary>
    public string StartedContainerState { get; set; } = "running";

    /// <summary>When set, <see cref="BuildImageAsync"/> throws — simulates a failing build.</summary>
    public Exception? BuildFailure { get; set; }

    /// <summary>When set, <see cref="RunContainerAsync"/> throws — simulates a container that won't start.</summary>
    public Exception? RunFailure { get; set; }

    /// <summary>Container names whose removal fails — a retired container that resists cleanup.</summary>
    public HashSet<string> UnremovableContainers { get; } = new(StringComparer.Ordinal);

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

    /// <summary>Seeds a container as if a previous deployment had left it running.</summary>
    public string SeedContainer(string name, string slug, string state = "running", string image = "img:old")
    {
        var id = $"container-{Interlocked.Increment(ref _idSeq):D4}-{name}";
        _containers[id] = new ContainerInfo(id, name, image, state, "Up", new Dictionary<string, string>
        {
            ["harbora.managed"] = "true",
            ["harbora.app"] = slug
        });
        return id;
    }

    private void Record(string operation, string target)
    {
        lock (_gate) _calls.Add(new Call(operation, target));
    }

    // ---- IDockerEngine ----

    public Task<string> BuildImageAsync(DockerBuildRequest request, IProgress<string> log, CancellationToken ct)
    {
        Record(nameof(BuildImageAsync), request.ImageTag);
        if (BuildFailure is not null) throw BuildFailure;
        log.Report($"built {request.ImageTag}");
        return Task.FromResult(request.ImageTag);
    }

    public Task PullImageAsync(string image, IProgress<string> log, CancellationToken ct)
    {
        Record(nameof(PullImageAsync), image);
        log.Report($"pulled {image}");
        return Task.CompletedTask;
    }

    public Task<string> RunContainerAsync(DockerRunRequest request, CancellationToken ct)
    {
        Record(nameof(RunContainerAsync), request.ContainerName);
        if (RunFailure is not null) throw RunFailure;

        var id = $"container-{Interlocked.Increment(ref _idSeq):D4}-{request.ContainerName}";
        _containers[id] = new ContainerInfo(
            id, request.ContainerName, request.Image, StartedContainerState, "Up",
            request.Labels.ToDictionary(kv => kv.Key, kv => kv.Value));
        RunRequests.Add(request);
        return Task.FromResult(id);
    }

    /// <summary>Every run request, so tests can assert on ports, env, labels and volumes.</summary>
    public List<DockerRunRequest> RunRequests { get; } = [];

    public Task StopContainerAsync(string containerId, CancellationToken ct)
    {
        Record(nameof(StopContainerAsync), NameOf(containerId));
        return Task.CompletedTask;
    }

    public Task RemoveContainerAsync(string containerId, bool force, CancellationToken ct)
    {
        var name = NameOf(containerId);
        Record(nameof(RemoveContainerAsync), name);
        if (UnremovableContainers.Contains(name))
            throw new InvalidOperationException($"container {name} is in use");
        _containers.TryRemove(containerId, out _);
        return Task.CompletedTask;
    }

    public Task RestartContainerAsync(string containerId, CancellationToken ct)
    {
        Record(nameof(RestartContainerAsync), NameOf(containerId));
        return Task.CompletedTask;
    }

    public Task StreamLogsAsync(string containerId, IProgress<string> sink, CancellationToken ct)
        => Task.CompletedTask;

    public Task<string> GetLogsAsync(string containerId, int tailLines, CancellationToken ct)
        => Task.FromResult(string.Empty);

    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(string? labelFilter, CancellationToken ct)
    {
        // Deliberately NOT recorded: listing is a query the pipeline makes repeatedly while polling
        // for health, and it would drown the ordering assertions in noise.
        IReadOnlyList<ContainerInfo> snapshot = _containers.Values
            .Where(c => labelFilter is null || c.Labels.ContainsKey(labelFilter))
            .ToList();
        return Task.FromResult(snapshot);
    }

    public Task<ContainerStats?> GetStatsAsync(string containerId, CancellationToken ct)
        => Task.FromResult<ContainerStats?>(null);

    public Task EnsureNetworkAsync(string name, CancellationToken ct)
    {
        Record(nameof(EnsureNetworkAsync), name);
        return Task.CompletedTask;
    }

    public Task ConnectNetworkAsync(string containerNameOrId, string network, CancellationToken ct)
    {
        Record(nameof(ConnectNetworkAsync), containerNameOrId);
        return Task.CompletedTask;
    }

    public Task EnsureVolumeAsync(string name, CancellationToken ct)
    {
        Record(nameof(EnsureVolumeAsync), name);
        return Task.CompletedTask;
    }

    public Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        Record(nameof(RemoveVolumeAsync), name);
        return Task.CompletedTask;
    }

    public Task<int> RunOneOffAsync(DockerOneOffRequest request, IProgress<string>? log, CancellationToken ct)
        => Task.FromResult(0);

    public Task<HostInfo> GetHostInfoAsync(CancellationToken ct)
        => Task.FromResult(new HostInfo(4, 8L << 30, 100L << 30, 50L << 30, "fake", _containers.Count));

    private string NameOf(string containerId) =>
        _containers.TryGetValue(containerId, out var c) ? c.Name : containerId;
}
