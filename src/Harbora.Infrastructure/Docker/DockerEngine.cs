using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using Harbora.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// Thrown when the Docker daemon's build stream itself reports the build failed — a Dockerfile
/// <c>RUN</c> step that returned non-zero, for example. The daemon answers 200 OK for this and lets
/// the stream end normally, so nothing else in the Docker.DotNet call chain would otherwise raise it:
/// see <see cref="DockerEngine.BuildImageFromTarAsync"/> for where this is actually detected.
/// </summary>
public sealed class DockerBuildException(string message) : Exception(message);

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

        // The daemon reports a build failure as one more message inside this same stream — never as
        // an HTTP error, and Docker.DotNet does not inspect the stream for one on its own (confirmed
        // by decompiling StreamUtil.MonitorStreamForMessagesAsync: it deserializes and forwards every
        // message, then simply returns when the stream ends). Without tracking this, a Dockerfile
        // step that fails partway through — "Step 6/23 : RUN npm run build" with a non-zero exit —
        // leaves BuildImageFromDockerfileAsync completing normally, so the caller believes imageTag
        // was actually built. The failure then only surfaces two steps later, as a confusing
        // "No such image" from whatever tries to run the image that was never produced.
        string? lastStep = null;
        JSONMessage? failure = null;

        var progress = new Progress<JSONMessage>(m =>
        {
            var line = m.Stream ?? m.Status ?? m.ErrorMessage;
            if (!string.IsNullOrWhiteSpace(line))
            {
                var trimmed = line.TrimEnd('\n');
                log.Report(trimmed);
                if (IsStepLine(trimmed)) lastStep = trimmed.Trim();
            }
            if (DescribesBuildFailure(m)) failure ??= m;
        });

        await client.Images.BuildImageFromDockerfileAsync(
            parameters, tarContext, authConfigs: null, headers: null, progress, ct);

        if (failure is not null)
            throw new DockerBuildException(BuildFailureMessage(imageTag, lastStep, failure));

        return imageTag;
    }

    /// <summary>Whether a build-progress line is Docker announcing which Dockerfile instruction is
    /// now running — tracked so a failure can name the step it happened at, not just the image.</summary>
    internal static bool IsStepLine(string line) =>
        line.TrimStart().StartsWith("Step ", StringComparison.Ordinal);

    /// <summary>Whether a build-progress message is the daemon reporting the build itself failed.
    /// Docker.DotNet surfaces this two ways depending on daemon/API version — the free-text
    /// <c>ErrorMessage</c> and the structured <c>Error.Message</c> — so both are checked; a daemon
    /// that only ever fills in one of them must not be read as "the build succeeded".</summary>
    internal static bool DescribesBuildFailure(JSONMessage message) =>
        !string.IsNullOrWhiteSpace(message.ErrorMessage) ||
        !string.IsNullOrWhiteSpace(message.Error?.Message);

    /// <summary>
    /// The message a caller actually needs: which image, which Dockerfile step (when one was seen
    /// before the failure arrived), and the daemon's own words — not a downstream symptom like
    /// "No such image" from whatever tries to run what was never built.
    /// </summary>
    internal static string BuildFailureMessage(string imageTag, string? lastStep, JSONMessage failure)
    {
        var detail = failure.Error?.Message ?? failure.ErrorMessage ?? "the daemon reported no detail";
        return lastStep is null
            ? $"Build of {imageTag} failed: {detail}"
            : $"Build of {imageTag} failed at {lastStep}: {detail}";
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

    public async Task<IReadOnlyList<ImageInfo>> ListImagesAsync(string? tagPrefix, CancellationToken ct)
    {
        var images = await client.Images.ListImagesAsync(new ImagesListParameters { All = false }, ct);

        // One Docker image can carry several tags; retention reasons about tags, so flatten them.
        return images
            .Where(i => i.RepoTags is not null)
            .SelectMany(i => i.RepoTags
                .Where(t => !string.IsNullOrWhiteSpace(t) && t != "<none>:<none>")
                .Where(t => tagPrefix is null || t.StartsWith(tagPrefix, StringComparison.Ordinal))
                .Select(t => new ImageInfo(i.ID, t, i.Created, i.Size)))
            .ToList();
    }

    public async Task<bool> ImageExistsAsync(string imageRef, CancellationToken ct)
    {
        try
        {
            await client.Images.InspectImageAsync(imageRef, ct);
            return true;
        }
        catch (DockerImageNotFoundException) { return false; }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404) { return false; }
    }

    public async Task<IReadOnlyList<int>> GetImagePortsAsync(string imageRef, CancellationToken ct)
    {
        try
        {
            var image = await client.Images.InspectImageAsync(imageRef, ct);
            var exposed = image.Config?.ExposedPorts;
            if (exposed is null) return [];

            // Keys look like "8080/tcp". UDP ports are not somewhere HTTP traffic could be served.
            return exposed.Keys
                .Where(k => !k.Contains("udp", StringComparison.OrdinalIgnoreCase))
                .Select(k => int.TryParse(k.Split('/')[0], out var p) ? p : 0)
                .Where(p => p > 0)
                .Distinct()
                .ToList();
        }
        catch (Exception)
        {
            // Not knowing is a normal answer here; the caller keeps the configured port.
            return [];
        }
    }

    public async Task RemoveImageAsync(string imageRef, CancellationToken ct)
    {
        try
        {
            // Force = false on purpose: an image a container still references must survive, even if
            // our bookkeeping thinks it is prunable.
            await client.Images.DeleteImageAsync(imageRef, new ImageDeleteParameters { Force = false }, ct);
        }
        catch (DockerApiException ex)
        {
            logger.LogWarning("Image {Image} not removed: {Msg}", imageRef, ex.Message);
        }
    }

    public async Task<string> RunContainerAsync(DockerRunRequest r, CancellationToken ct)
    {
        var id = await CreateContainerAsync(r, ct);
        await StartContainerAsync(id, ct);
        logger.LogInformation("Started container {Name} ({Id})", r.ContainerName, id[..12]);
        return id;
    }

    public async Task<string> CreateContainerAsync(DockerRunRequest r, CancellationToken ct)
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
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [r.NetworkName] = new()
                    {
                        // Aliases are how a compose service reaches "db" rather than the versioned
                        // container name — without them, every inter-service connection string breaks.
                        Aliases = r.NetworkAliases?.ToList()
                    }
                }
            }
        };

        var published = new Dictionary<int, int>();
        if (r.ContainerPort is { } primary && r.PublishToHostPort is { } primaryHost)
            published[primary] = primaryHost;
        if (r.AdditionalPublishedPorts is not null)
            foreach (var pair in r.AdditionalPublishedPorts) published[pair.Key] = pair.Value;

        if (r.ContainerPort is { } port)
        {
            create.ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{port}/tcp"] = default };
        }

        if (published.Count > 0)
        {
            create.ExposedPorts ??= new Dictionary<string, EmptyStruct>();
            hostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>();
            foreach (var pair in published)
            {
                create.ExposedPorts[$"{pair.Key}/tcp"] = default;
                hostConfig.PortBindings[$"{pair.Key}/tcp"] = [new PortBinding { HostPort = pair.Value.ToString() }];
            }
        }

        if (r.Command is { Count: > 0 })
            create.Cmd = r.Command.ToList();

        var response = await client.Containers.CreateContainerAsync(create, ct);
        return response.ID;
    }

    public Task StartContainerAsync(string containerId, CancellationToken ct) =>
        client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);

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

    public async Task<string> GetLogsAsync(string containerId, int tailLines, CancellationToken ct)
    {
        var parameters = new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = false, Tail = tailLines.ToString() };
        using var stream = await client.Containers.GetContainerLogsAsync(containerId, tty: false, parameters, ct);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct);
        return string.Concat(stdout, stderr);
    }

    /// <summary>
    /// The one engine that can honor a time window honestly: Docker's own log API takes
    /// <c>Since</c>/<c>Timestamps</c> as first-class parameters, so this asks the daemon directly
    /// rather than guessing from an untimed tail. <paramref name="maxLines"/> stays as a hard cap
    /// alongside <c>Since</c> — a container that has written for days must not turn "last 15 minutes"
    /// into an unbounded read.
    /// </summary>
    public async Task<IReadOnlyList<TimedLogLine>> GetLogsSinceAsync(
        string containerId, DateTimeOffset since, int maxLines, CancellationToken ct)
    {
        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = false,
            Tail = maxLines.ToString(),
            Timestamps = true,
            Since = since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        };
        using var stream = await client.Containers.GetContainerLogsAsync(containerId, tty: false, parameters, ct);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct);
        return DockerTimestampedLog.Parse(string.Concat(stdout, stderr));
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

        // The shared formula, so a container reads the same here as it does on a node. It was
        // written out here and would have been written out again in the agent — two copies of this
        // arithmetic means the same container reads differently depending on where it is running,
        // and the difference gets blamed on the node.
        var cpuPercent = Harbora.NodeAgent.Contracts.ContainerCpu.Percent(
            snapshot.CPUStats.CPUUsage.TotalUsage - snapshot.PreCPUStats.CPUUsage.TotalUsage,
            snapshot.CPUStats.SystemUsage - snapshot.PreCPUStats.SystemUsage,
            snapshot.CPUStats.OnlineCPUs);

        return new ContainerStats(
            cpuPercent ?? 0,
            (long)snapshot.MemoryStats.Usage,
            (long)snapshot.MemoryStats.Limit,
            (long)(snapshot.Networks?.Values.Sum(n => (decimal)n.RxBytes) ?? 0),
            (long)(snapshot.Networks?.Values.Sum(n => (decimal)n.TxBytes) ?? 0));
    }

    public async Task<ContainerDetail?> InspectAsync(string containerNameOrId, CancellationToken ct)
    {
        try
        {
            var c = await client.Containers.InspectContainerAsync(containerNameOrId, ct);
            return MapDetail(c, containerNameOrId);
        }
        catch (DockerContainerNotFoundException) { return null; }
        catch (DockerApiException e) when ((int)e.StatusCode == 404) { return null; }
    }

    /// <summary>
    /// The two lifecycle figures, read the same way <see cref="InspectAsync"/> reads the rest of
    /// them — there is no cheaper call on the local engine, so this is a thin projection rather than
    /// a second Docker request.
    /// </summary>
    public async Task<ContainerLifecycle?> GetLifecycleAsync(string containerNameOrId, CancellationToken ct)
    {
        var detail = await InspectAsync(containerNameOrId, ct);
        return detail is null ? null : new ContainerLifecycle(detail.RestartCount, detail.StartedAt);
    }

    /// <summary>
    /// Turns Docker's inspect response into <see cref="ContainerDetail"/>. Pulled out of
    /// <see cref="InspectAsync"/> so the mapping is reachable from a test without a Docker daemon —
    /// every field on <see cref="ContainerInspectResponse"/> used here is a plain settable property,
    /// so a test builds one directly rather than standing up a real container.
    /// </summary>
    internal static ContainerDetail MapDetail(ContainerInspectResponse c, string fallbackName)
    {
        var health = c.State?.Health?.Status;

        return new ContainerDetail(
            c.ID,
            c.Name?.TrimStart('/') ?? fallbackName,
            c.Config?.Image ?? c.Image,
            c.Image,
            c.State?.Status ?? "unknown",
            // The inspect API has no formatted status line ("Up 3 hours") the way the list API
            // does — ContainerState carries only the short state word, in Status. So this really is
            // the same value as State above, not a slip: there is nothing else to put here without
            // computing an uptime string ourselves, which Dates.Ago already does for the view from
            // StartedAt. Left duplicated rather than invented.
            c.State?.Status ?? "unknown",
            // No health check configured is not "unhealthy": it is "we were not told how to
            // ask". Callers distinguish the two, so null must survive to them. (Unlike the node
            // runtime's identical-looking line, this does NOT fall back to c.State.Running — that
            // would turn "nobody checked" into an affirmative "healthy" for the common case of a
            // container with no HEALTHCHECK. The node's Healthy feeds deployer placement decisions,
            // where "running with no health check" is arguably "fit to serve"; this one feeds a
            // health badge on a page a person reads, where it would just be wrong. The two are
            // meant to differ now.)
            Healthy: health is null ? null : health.Equals("healthy", StringComparison.OrdinalIgnoreCase),
            RestartCount: (int)c.RestartCount,
            StartedAt: ParseTimestamp(c.State?.StartedAt));
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

    public async Task ConnectNetworkAsync(string containerNameOrId, string network, CancellationToken ct)
    {
        try
        {
            await client.Networks.ConnectNetworkAsync(network, new NetworkConnectParameters { Container = containerNameOrId }, ct);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode is 403 or 404 or 500)
        {
            // Already attached, or the proxy container isn't present (e.g. local dev) — safe to ignore.
            logger.LogDebug("Connect {Container}→{Network}: {Msg}", containerNameOrId, network, ex.Message);
        }
    }

    public async Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        try { await client.Volumes.RemoveAsync(name, force: true, ct); }
        catch (DockerApiException ex) { logger.LogWarning("Volume {Name} not removed: {Msg}", name, ex.Message); }
    }

    /// <summary>
    /// Every volume the daemon itself lists — real Docker.DotNet call, the same one
    /// <see cref="EnsureVolumeAsync"/> already makes to check whether a volume exists. This runs both
    /// in-process on the local node and inside <c>Harbora.Agent</c> on an older remote one, since both
    /// host this exact class against their own daemon.
    /// </summary>
    public async Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct)
    {
        var response = await client.Volumes.ListAsync(ct);
        return response.Volumes
            .Select(v => new VolumeInfo(v.Name, ParseVolumeCreatedAt(v.CreatedAt)))
            .ToList();
    }

    /// <summary>
    /// The daemon reports a volume's creation moment as an RFC3339 string, not a parsed value — the
    /// same reason <see cref="ParseTimestamp"/> exists a few methods up. Unparsable or absent stays
    /// null rather than becoming "now", which would tell an operator a leftover volume is fresh when
    /// it might be a year old.
    /// </summary>
    private static DateTimeOffset? ParseVolumeCreatedAt(string? createdAt) =>
        !string.IsNullOrWhiteSpace(createdAt) &&
        DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    public async Task<int> RunOneOffAsync(DockerOneOffRequest request, IProgress<string>? log, CancellationToken ct)
    {
        await PullImageAsync(request.Image, new Progress<string>(l => log?.Report(l)), ct);

        // The image's entrypoint is replaced rather than argued with — see OneOffLaunch for why
        // leaving it in place silently turns the command into arguments the image ignores.
        var (entrypoint, arguments) = OneOffLaunch.From(request.Command);

        var create = new CreateContainerParameters
        {
            Image = request.Image,
            Entrypoint = entrypoint,
            Cmd = arguments,
            Env = request.Env?.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
            HostConfig = new HostConfig
            {
                Binds = request.Binds.Select(b => $"{b.Source}:{b.Target}{(b.ReadOnly ? ":ro" : "")}").ToList(),
                NetworkMode = request.NetworkMode,
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

    public async Task<IContainerExec> ExecAsync(
        string containerId, IReadOnlyList<string> command, int columns, int rows, CancellationToken ct)
    {
        var (cols, lines) = Terminals.TerminalAccess.Size(columns, rows);

        var exec = await client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            // A real tty, which is what makes this a terminal rather than a pipe: the shell prints a
            // prompt, line editing works, and -- the part that matters here -- the stream comes back
            // raw instead of carrying docker's eight-byte frame header on every chunk. Six parsers in
            // this codebase have been written against that header; none is needed on this path.
            Tty = true,
            Cmd = command.ToList()
        }, ct);

        var stream = await client.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: true, ct);

        // Asked for once at the start as well as on every browser resize: a shell that thinks the
        // window is 80x24 when it is not draws its full-screen programs over the wrong area, and
        // that looks like the terminal being broken rather than being mis-sized.
        try { await client.Exec.ResizeContainerExecTtyAsync(exec.ID, new ContainerResizeParameters
              { Width = (long)cols, Height = (long)lines }, ct); }
        catch (DockerApiException) { /* the shell still runs at its default size */ }

        return new DockerContainerExec(client, exec.ID, stream);
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
            (int)info.ContainersRunning,
            // Docker reports the kernel's name; image manifests use Go's. Normalised here so the
            // version compatibility check has something it can actually compare against.
            Templates.HostArchitecture.Normalise(info.Architecture));
    }

    // --- helpers ---

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return null;

        // Docker reports "0001-01-01T00:00:00Z" (Go's zero time) for a container that was created
        // but never started — TryParse accepts that string just fine, so without this check a
        // container sitting in "created" would report a real year-1 StartedAt and the view would
        // render an uptime of hundreds of thousands of days instead of "start time unknown".
        return parsed.Year <= 1 ? null : parsed;
    }

    /// <summary>
    /// Splits an image reference into what Docker's pull API wants: a repository and a tag *or*
    /// a digest.
    ///
    /// The digest case is the one that mattered and was missing. This platform pins by digest
    /// everywhere — <c>VersionSelection.PinnedImage</c> produces <c>repo@sha256:…</c>, and it is
    /// what every ready-made application is deployed from. Splitting on the last colon put the
    /// digest's own separator in the middle, produced a repository of <c>repo@sha256</c>, and
    /// Docker refused the whole pull with "invalid reference format" — so the pinning that the
    /// version model exists for was the thing that made an image unpullable.
    /// </summary>
    private static (string Repo, string Tag) SplitImage(string image)
    {
        // Everything before the @ is the repository; the digest goes where a tag would, which is
        // what the API accepts for a pull by digest.
        var at = image.IndexOf('@');
        if (at > 0) return (image[..at], image[(at + 1)..]);

        var idx = image.LastIndexOf(':');
        // treat "host:port/repo" (colon before a slash) as untagged
        if (idx < 0 || image.LastIndexOf('/') > idx) return (image, "latest");
        return (image[..idx], image[(idx + 1)..]);
    }

    /// <summary>
    /// The same split, reachable from a test. Public because the alternative is trusting by
    /// inspection the one line whose failure made every digest-pinned image unpullable.
    /// </summary>
    public static (string Repo, string Tag) SplitImageReference(string image) => SplitImage(image);
}
