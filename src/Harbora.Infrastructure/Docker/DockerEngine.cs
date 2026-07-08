using Docker.DotNet;
using Docker.DotNet.Models;
using Harbora.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// Docker.DotNet-backed implementation of <see cref="IDockerEngine"/>. All arguments are
/// passed through the typed API (never string-concatenated into a shell), so container names,
/// env values and image refs cannot inject commands.
/// </summary>
public sealed class DockerEngine(IDockerClient client, ILogger<DockerEngine> logger) : IDockerEngine
{
    public async Task<string> BuildImageAsync(DockerBuildRequest request, IProgress<string> log, CancellationToken ct)
    {
        await using var tarball = DockerTar.Create(request.ContextPath);
        return await BuildImageFromTarAsync(tarball, request.Dockerfile, request.ImageTag, request.BuildArgs, log, ct);
    }

    /// <summary>
    /// Build from an already-packed context tar. Used by the agent, which receives the tar over
    /// HTTP rather than a local path.
    /// </summary>
    public async Task<string> BuildImageFromTarAsync(
        Stream tarContext, string dockerfile, string imageTag,
        IReadOnlyDictionary<string, string> buildArgs, IProgress<string> log, CancellationToken ct)
    {
        var parameters = new ImageBuildParameters
        {
            Dockerfile = dockerfile,
            Tags = [imageTag],
            BuildArgs = buildArgs.ToDictionary(kv => kv.Key, kv => kv.Value),
            Remove = true,
            ForceRemove = true
        };

        var progress = new Progress<JSONMessage>(m =>
        {
            var line = m.Stream ?? m.Status ?? m.ErrorMessage;
            if (!string.IsNullOrWhiteSpace(line)) log.Report(line.TrimEnd('\n'));
        });

        await client.Images.BuildImageFromDockerfileAsync(
            parameters, tarContext, authConfigs: null, headers: null, progress, ct);

        return imageTag;
    }

    public async Task PullImageAsync(string image, IProgress<string> log, CancellationToken ct)
    {
        var (repo, tag) = SplitImage(image);
        var progress = new Progress<JSONMessage>(m =>
        {
            var line = m.Status ?? m.ProgressMessage ?? m.ErrorMessage;
            if (!string.IsNullOrWhiteSpace(line)) log.Report(line);
        });
        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = repo, Tag = tag }, authConfig: null, progress, ct);
    }

    public async Task<string> RunContainerAsync(DockerRunRequest r, CancellationToken ct)
    {
        var hostConfig = new HostConfig
        {
            RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
            Binds = r.Volumes.Select(v => $"{v.VolumeName}:{v.MountPath}{(v.ReadOnly ? ":ro" : "")}").ToList(),
            Memory = r.MemoryLimitBytes > 0 ? r.MemoryLimitBytes : 0,
            NanoCPUs = r.CpuLimit > 0 ? (long)(r.CpuLimit * 1_000_000_000) : 0
        };

        var create = new CreateContainerParameters
        {
            Image = r.Image,
            Name = r.ContainerName,
            Env = r.Env.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
            Labels = r.Labels.ToDictionary(kv => kv.Key, kv => kv.Value),
            HostConfig = hostConfig,
            NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings> { [r.NetworkName] = new() }
            }
        };

        if (r.ContainerPort is { } port)
        {
            create.ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{port}/tcp"] = default };

            // Publish to a host port so a remote node's container is reachable across the network
            // (used for cross-node routing where there is no shared overlay network).
            if (r.PublishToHostPort is { } hostPort)
                hostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [$"{port}/tcp"] = [new PortBinding { HostPort = hostPort.ToString() }]
                };
        }

        if (r.Command is { Count: > 0 })
            create.Cmd = r.Command.ToList();

        var response = await client.Containers.CreateContainerAsync(create, ct);
        await client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters(), ct);
        logger.LogInformation("Started container {Name} ({Id})", r.ContainerName, response.ID[..12]);
        return response.ID;
    }

    public Task StopContainerAsync(string containerId, CancellationToken ct) =>
        client.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);

    public Task RemoveContainerAsync(string containerId, bool force, CancellationToken ct) =>
        client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = force }, ct);

    public Task RestartContainerAsync(string containerId, CancellationToken ct) =>
        client.Containers.RestartContainerAsync(containerId, new ContainerRestartParameters { WaitBeforeKillSeconds = 10 }, ct);

    public async Task StreamLogsAsync(string containerId, IProgress<string> sink, CancellationToken ct)
    {
        var parameters = new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = true, Tail = "200" };
        await client.Containers.GetContainerLogsAsync(containerId, parameters, ct, new Progress<string>(sink.Report));
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(string? labelFilter, CancellationToken ct)
    {
        var parameters = new ContainersListParameters { All = true };
        if (!string.IsNullOrWhiteSpace(labelFilter))
            parameters.Filters = new Dictionary<string, IDictionary<string, bool>> { ["label"] = new Dictionary<string, bool> { [labelFilter] = true } };

        var list = await client.Containers.ListContainersAsync(parameters, ct);
        return list.Select(c => new ContainerInfo(
            c.ID,
            c.Names.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12],
            c.Image,
            c.State,
            c.Status,
            new Dictionary<string, string>(c.Labels ?? new Dictionary<string, string>()))).ToList();
    }

    public async Task<ContainerStats?> GetStatsAsync(string containerId, CancellationToken ct)
    {
        ContainerStatsResponse? snapshot = null;
        await client.Containers.GetContainerStatsAsync(
            containerId, new ContainerStatsParameters { Stream = false },
            new Progress<ContainerStatsResponse>(s => snapshot = s), ct);

        if (snapshot is null) return null;

        var cpuDelta = snapshot.CPUStats.CPUUsage.TotalUsage - snapshot.PreCPUStats.CPUUsage.TotalUsage;
        var systemDelta = snapshot.CPUStats.SystemUsage - snapshot.PreCPUStats.SystemUsage;
        var cpuCount = snapshot.CPUStats.OnlineCPUs == 0 ? 1 : snapshot.CPUStats.OnlineCPUs;
        var cpuPercent = systemDelta > 0 ? (double)cpuDelta / systemDelta * cpuCount * 100.0 : 0;

        return new ContainerStats(
            Math.Round(cpuPercent, 2),
            (long)snapshot.MemoryStats.Usage,
            (long)snapshot.MemoryStats.Limit,
            (long)(snapshot.Networks?.Values.Sum(n => (decimal)n.RxBytes) ?? 0),
            (long)(snapshot.Networks?.Values.Sum(n => (decimal)n.TxBytes) ?? 0));
    }

    public async Task EnsureNetworkAsync(string name, CancellationToken ct)
    {
        var existing = await client.Networks.ListNetworksAsync(
            new NetworksListParameters { Filters = new Dictionary<string, IDictionary<string, bool>> { ["name"] = new Dictionary<string, bool> { [name] = true } } }, ct);
        if (existing.Any(n => n.Name == name)) return;
        await client.Networks.CreateNetworkAsync(new NetworksCreateParameters { Name = name, Driver = "bridge" }, ct);
    }

    public async Task EnsureVolumeAsync(string name, CancellationToken ct)
    {
        var existing = await client.Volumes.ListAsync(ct);
        if (existing.Volumes.Any(v => v.Name == name)) return;
        await client.Volumes.CreateAsync(new VolumesCreateParameters { Name = name }, ct);
    }

    public async Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        try { await client.Volumes.RemoveAsync(name, force: true, ct); }
        catch (DockerApiException ex) { logger.LogWarning("Volume {Name} not removed: {Msg}", name, ex.Message); }
    }

    public async Task<int> RunOneOffAsync(DockerOneOffRequest request, IProgress<string>? log, CancellationToken ct)
    {
        await PullImageAsync(request.Image, new Progress<string>(l => log?.Report(l)), ct);

        var create = new CreateContainerParameters
        {
            Image = request.Image,
            Cmd = request.Command.ToList(),
            HostConfig = new HostConfig
            {
                Binds = request.Binds.Select(b => $"{b.Source}:{b.Target}{(b.ReadOnly ? ":ro" : "")}").ToList(),
                AutoRemove = false
            }
        };

        var container = await client.Containers.CreateContainerAsync(create, ct);
        try
        {
            await client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct);
            if (log is not null)
                await client.Containers.GetContainerLogsAsync(container.ID,
                    new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = true }, ct, new Progress<string>(log.Report));
            var wait = await client.Containers.WaitContainerAsync(container.ID, ct);
            return (int)wait.StatusCode;
        }
        finally
        {
            try { await client.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true }, ct); }
            catch { /* best effort */ }
        }
    }

    public async Task<HostInfo> GetHostInfoAsync(CancellationToken ct)
    {
        var info = await client.System.GetSystemInfoAsync(ct);
        long totalDisk = 0, freeDisk = 0;
        try
        {
            var root = OperatingSystem.IsWindows() ? "C:\\" : "/";
            var drive = new DriveInfo(root);
            totalDisk = drive.TotalSize;
            freeDisk = drive.AvailableFreeSpace;
        }
        catch { /* best effort */ }

        return new HostInfo(
            (int)info.NCPU,
            info.MemTotal,
            totalDisk,
            freeDisk,
            info.ServerVersion,
            (int)info.ContainersRunning);
    }

    // --- helpers ---

    private static (string Repo, string Tag) SplitImage(string image)
    {
        var idx = image.LastIndexOf(':');
        // treat "host:port/repo" (colon before a slash) as untagged
        if (idx < 0 || image.LastIndexOf('/') > idx) return (image, "latest");
        return (image[..idx], image[(idx + 1)..]);
    }
}
