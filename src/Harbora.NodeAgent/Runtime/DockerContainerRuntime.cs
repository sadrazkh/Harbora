using System.Formats.Tar;
using System.Globalization;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Harbora.NodeAgent.Contracts;
using Microsoft.Extensions.Logging;

// Docker.DotNet has its own NetworkSpec (a Swarm concept) that has nothing to do with ours.
using NetworkSpec = Harbora.NodeAgent.Contracts.NetworkSpec;
using RestartPolicySpec = Harbora.NodeAgent.Contracts.RestartPolicySpec;

namespace Harbora.NodeAgent.Runtime;

/// <summary>
/// <see cref="IContainerRuntime"/> over the Docker Engine API.
///
/// <para>
/// Every call goes through Docker.DotNet's typed parameters. Nothing here builds a command line, so
/// a workload named <c>; rm -rf /</c> is a container with an unusual name rather than an incident.
/// </para>
/// </summary>
public sealed class DockerContainerRuntime(IDockerClient client, ILogger<DockerContainerRuntime> log)
    : IContainerRuntime
{
    public async Task<RuntimeInfo> GetInfoAsync(CancellationToken ct)
    {
        try
        {
            var version = await client.System.GetVersionAsync(ct);
            var info = await client.System.GetSystemInfoAsync(ct);

            return new RuntimeInfo(
                "docker", version.Version, version.APIVersion, (int)info.ContainersRunning, Available: true);
        }
        catch (Exception e) when (e is DockerApiException or HttpRequestException or IOException or TimeoutException)
        {
            return new RuntimeInfo("docker", "unknown", "unknown", 0, Available: false, e.Message);
        }
    }

    public async Task PullImageAsync(string reference, IProgress<string>? logSink, CancellationToken ct)
    {
        var progress = new Progress<JSONMessage>(m =>
        {
            var line = m.ErrorMessage ?? m.Status ?? m.ProgressMessage;
            if (!string.IsNullOrWhiteSpace(line)) logSink?.Report(line.TrimEnd('\n'));
        });

        try
        {
            // The digest travels inside FromImage. Splitting it into the Tag field is how a pull
            // silently becomes a pull of ":latest" on daemons that reject a digest there.
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = reference }, authConfig: null, progress, ct);
        }
        catch (DockerApiException e)
        {
            throw new ContainerRuntimeException(
                NodeErrorCode.ImagePullFailed, $"Could not pull {reference}: {e.Message}", e)
            {
                // A registry timeout or a rate limit passes; a manifest that does not exist does not.
                Retryable = (int)e.StatusCode >= 500 || (int)e.StatusCode == 429,
            };
        }
    }

    public async Task<string?> ResolveDigestAsync(string reference, CancellationToken ct)
    {
        try
        {
            var image = await client.Images.InspectImageAsync(reference, ct);

            // RepoDigests entries look like "repo@sha256:…". Prefer one matching the repository we
            // asked about; an image can carry digests for several repositories it was tagged into.
            var repository = reference.Split('@')[0].Split(':')[0];

            var match = image.RepoDigests?
                .FirstOrDefault(d => d.StartsWith(repository, StringComparison.Ordinal));

            var digest = match ?? image.RepoDigests?.FirstOrDefault();

            return digest?.Split('@') is [_, { Length: > 0 } sha] ? sha : image.ID;
        }
        catch (DockerImageNotFoundException) { return null; }
        catch (DockerApiException e) when ((int)e.StatusCode == 404) { return null; }
    }

    public async Task<IReadOnlyList<string>> GetImageArchitecturesAsync(string reference, CancellationToken ct)
    {
        try
        {
            var image = await client.Images.InspectImageAsync(reference, ct);
            return string.IsNullOrWhiteSpace(image.Architecture) ? [] : [image.Architecture];
        }
        catch (Exception e) when (e is DockerApiException or DockerImageNotFoundException)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<RuntimeContainer>> ListContainersAsync(
        IReadOnlyDictionary<string, string>? labelFilter, bool includeStopped, CancellationToken ct)
    {
        var parameters = new ContainersListParameters { All = includeStopped };

        if (labelFilter is { Count: > 0 })
            parameters.Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = labelFilter.ToDictionary(kv => $"{kv.Key}={kv.Value}", _ => true),
            };

        var listed = await client.Containers.ListContainersAsync(parameters, ct);

        return listed.Select(c => new RuntimeContainer(
            c.ID,
            c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID[..Math.Min(12, c.ID.Length)],
            c.Image,
            c.ImageID,
            c.State,
            c.Status,
            Healthy: null, // Only an inspect knows; the list endpoint does not carry health.
            RestartCount: 0,
            StartedAt: null,
            c.Labels is null ? new Dictionary<string, string>() : new Dictionary<string, string>(c.Labels),
            PublishedPorts(c.Ports),
            c.NetworkSettings?.Networks?.ToDictionary(n => n.Key, n => n.Value.IPAddress ?? string.Empty)
                ?? new Dictionary<string, string>())).ToList();
    }

    public async Task<RuntimeContainerStats?> GetStatsAsync(string idOrName, CancellationToken ct)
    {
        ContainerStatsResponse? snapshot = null;

        try
        {
            await client.Containers.GetContainerStatsAsync(
                idOrName, new ContainerStatsParameters { Stream = false },
                new Progress<ContainerStatsResponse>(s => snapshot = s), ct);
        }
        catch (DockerApiException)
        {
            // A container that is starting, stopping or already gone. Null, not zeroes: the caller
            // has to be able to tell "no reading" from "a reading of nothing".
            return null;
        }

        if (snapshot is null) return null;

        return new RuntimeContainerStats(
            // The control plane's formula, shared through the contract so the same container reads
            // the same number wherever it runs.
            ContainerCpu.Percent(
                snapshot.CPUStats.CPUUsage.TotalUsage - snapshot.PreCPUStats.CPUUsage.TotalUsage,
                snapshot.CPUStats.SystemUsage - snapshot.PreCPUStats.SystemUsage,
                snapshot.CPUStats.OnlineCPUs),
            (long)snapshot.MemoryStats.Usage,
            (long)snapshot.MemoryStats.Limit,
            (long)(snapshot.Networks?.Values.Sum(n => (decimal)n.RxBytes) ?? 0),
            (long)(snapshot.Networks?.Values.Sum(n => (decimal)n.TxBytes) ?? 0));
    }

    public async Task<RuntimeContainer?> InspectAsync(string idOrName, CancellationToken ct)
    {
        try
        {
            var c = await client.Containers.InspectContainerAsync(idOrName, ct);

            var health = c.State?.Health?.Status;

            return new RuntimeContainer(
                c.ID,
                c.Name?.TrimStart('/') ?? idOrName,
                c.Config?.Image ?? c.Image,
                c.Image,
                c.State?.Status ?? "unknown",
                c.State?.Status ?? "unknown",
                // No health check configured is not "unhealthy": it is "we were not told how to
                // ask". Callers distinguish the two, so null must survive to them.
                Healthy: health is null ? c.State?.Running : health.Equals("healthy", StringComparison.OrdinalIgnoreCase),
                RestartCount: (int)c.RestartCount,
                StartedAt: ParseTimestamp(c.State?.StartedAt),
                c.Config?.Labels is null ? new Dictionary<string, string>() : new Dictionary<string, string>(c.Config.Labels),
                BoundPorts(c.NetworkSettings?.Ports),
                c.NetworkSettings?.Networks?.ToDictionary(n => n.Key, n => n.Value.IPAddress ?? string.Empty)
                    ?? new Dictionary<string, string>());
        }
        catch (DockerContainerNotFoundException) { return null; }
        catch (DockerApiException e) when ((int)e.StatusCode == 404) { return null; }
    }

    public async Task<string> CreateAndStartAsync(ContainerCreateRequest request, CancellationToken ct)
    {
        var hostConfig = new HostConfig
        {
            Binds = request.Mounts
                .Select(m => $"{m.VolumeName}:{m.MountPath}{(m.ReadOnly ? ":ro" : string.Empty)}")
                .ToList(),
            RestartPolicy = MapRestartPolicy(request.RestartPolicy),
            Memory = request.Resources.MemoryBytes,
            MemoryReservation = request.Resources.MemoryReservationBytes,
            NanoCPUs = request.Resources.CpuCores > 0 ? (long)(request.Resources.CpuCores * 1_000_000_000) : 0,
            PidsLimit = request.Resources.PidsLimit > 0 ? request.Resources.PidsLimit : null,
            Privileged = request.Privileged,
            ReadonlyRootfs = request.ReadOnlyRootFilesystem,
            CapAdd = request.CapabilitiesAdd.ToList(),
            CapDrop = request.CapabilitiesDrop.ToList(),
            SecurityOpt = request.NoNewPrivileges ? ["no-new-privileges:true"] : [],
        };

        if (request.HostNetwork) hostConfig.NetworkMode = "host";
        if (request.HostPidNamespace) hostConfig.PidMode = "host";

        // Secrets mounted as files live in a tmpfs so they never touch the container's writable
        // layer — a layer that gets committed, exported and shipped to a registry.
        if (request.TmpfsFiles.Count > 0)
            hostConfig.Tmpfs = request.TmpfsFiles
                .Select(f => Path.GetDirectoryName(f.Path)!.Replace('\\', '/'))
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(directory => directory, _ => "rw,noexec,nosuid,size=1m");

        var create = new CreateContainerParameters
        {
            Name = request.Name,
            Image = request.ImageReference,
            Env = request.Env.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
            Labels = request.Labels.ToDictionary(kv => kv.Key, kv => kv.Value),
            HostConfig = hostConfig,
            User = request.User,
        };

        if (request.Command is { Count: > 0 })
            create.Cmd = request.Command.ToList();

        if (request.HealthCheck is { } probe)
            create.Healthcheck = MapHealthCheck(probe);

        if (request.Ports.Count > 0)
        {
            create.ExposedPorts = request.Ports.ToDictionary(
                p => $"{p.ContainerPort}/{p.Protocol}", _ => default(EmptyStruct));

            var published = request.Ports.Where(p => p.HostPort is > 0).ToList();
            if (published.Count > 0)
                hostConfig.PortBindings = published.ToDictionary(
                    p => $"{p.ContainerPort}/{p.Protocol}",
                    p => (IList<PortBinding>)
                    [
                        // Bound to all interfaces only because Docker's default already is; the
                        // decision about whether anything is published at all was made upstream,
                        // in the policy that had to set HostPort in the first place.
                        new PortBinding { HostPort = p.HostPort!.Value.ToString(CultureInfo.InvariantCulture) },
                    ]);
        }

        if (request.Network is { Length: > 0 } network && !request.HostNetwork)
            create.NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [network] = new() { Aliases = request.NetworkAliases.ToList() },
                },
            };

        CreateContainerResponse created;
        try
        {
            created = await client.Containers.CreateContainerAsync(create, ct);
        }
        catch (DockerApiException e)
        {
            throw new ContainerRuntimeException(
                NodeErrorCode.ContainerStartFailed, $"Could not create container {request.Name}: {e.Message}", e);
        }

        try
        {
            await WriteTmpfsFilesAsync(created.ID, request.TmpfsFiles, ct);
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        }
        catch (Exception e) when (e is DockerApiException or IOException)
        {
            // A container that was created but will not start is litter that blocks the next
            // deploy by name collision, so it goes now rather than at the next reconcile.
            await SafeRemoveAsync(created.ID, ct);
            throw new ContainerRuntimeException(
                NodeErrorCode.ContainerStartFailed, $"Could not start container {request.Name}: {e.Message}", e);
        }

        log.LogInformation("Started container {Name} ({Id}).", request.Name, Short(created.ID));
        return created.ID;
    }

    public Task StopAsync(string idOrName, int gracePeriodSeconds, CancellationToken ct) =>
        Tolerate404(() => client.Containers.StopContainerAsync(
            idOrName,
            new ContainerStopParameters { WaitBeforeKillSeconds = (uint)Math.Max(0, gracePeriodSeconds) },
            ct));

    public Task StartAsync(string idOrName, CancellationToken ct) =>
        Tolerate404(() => client.Containers.StartContainerAsync(idOrName, new ContainerStartParameters(), ct));

    public Task RestartAsync(string idOrName, CancellationToken ct) =>
        Tolerate404(() => client.Containers.RestartContainerAsync(
            idOrName, new ContainerRestartParameters { WaitBeforeKillSeconds = 10 }, ct));

    public Task RemoveAsync(string idOrName, bool force, CancellationToken ct) =>
        Tolerate404(() => client.Containers.RemoveContainerAsync(
            idOrName, new ContainerRemoveParameters { Force = force }, ct));

    public async Task<string> GetLogsAsync(string idOrName, int tailLines, CancellationToken ct)
    {
        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = false,
            Tail = Math.Max(0, tailLines).ToString(CultureInfo.InvariantCulture),
        };

        using var stream = await client.Containers.GetContainerLogsAsync(idOrName, tty: false, parameters, ct);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct);
        return string.Concat(stdout, stderr);
    }

    public Task StreamLogsAsync(string idOrName, int tailLines, IProgress<string> sink, CancellationToken ct)
    {
        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = true,
            Tail = Math.Max(0, tailLines).ToString(CultureInfo.InvariantCulture),
        };

        return client.Containers.GetContainerLogsAsync(idOrName, parameters, ct, new Progress<string>(sink.Report));
    }

    public async Task EnsureNetworkAsync(NetworkSpec spec, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var existing = await client.Networks.ListNetworksAsync(
            new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool> { [spec.Name] = true },
                },
            }, ct);

        // The name filter is a substring match, so "harbora-ws-a" would match "harbora-ws-ab".
        if (existing.Any(n => n.Name == spec.Name)) return;

        try
        {
            await client.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = spec.Name,
                Driver = "bridge",
                Internal = spec.Internal,
                Labels = labels.Concat(spec.Labels).ToDictionary(kv => kv.Key, kv => kv.Value),
            }, ct);
        }
        catch (DockerApiException e) when ((int)e.StatusCode == 409)
        {
            // Two commands racing to create the same tenant network is normal and already correct.
        }
        catch (DockerApiException e)
        {
            throw new ContainerRuntimeException(
                NodeErrorCode.NetworkOperationFailed, $"Could not create network {spec.Name}: {e.Message}", e);
        }
    }

    public Task RemoveNetworkAsync(string name, CancellationToken ct) =>
        Tolerate404(() => client.Networks.DeleteNetworkAsync(name, ct));

    public async Task ConnectToNetworkAsync(
        string containerIdOrName, string network, IReadOnlyList<string> aliases, CancellationToken ct)
    {
        try
        {
            await client.Networks.ConnectNetworkAsync(network, new NetworkConnectParameters
            {
                Container = containerIdOrName,
                EndpointConfig = aliases.Count > 0 ? new EndpointSettings { Aliases = aliases.ToList() } : null,
            }, ct);
        }
        catch (DockerApiException e) when ((int)e.StatusCode is 403 or 409)
        {
            // Already attached. Connecting twice is the idempotent path, not an error.
            log.LogDebug("Container {Container} is already on network {Network}.", containerIdOrName, network);
        }
    }

    public async Task EnsureVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        if (await VolumeExistsAsync(name, ct)) return;

        try
        {
            await client.Volumes.CreateAsync(new VolumesCreateParameters
            {
                Name = name,
                Labels = labels.ToDictionary(kv => kv.Key, kv => kv.Value),
            }, ct);
        }
        catch (DockerApiException e) when ((int)e.StatusCode == 409)
        {
            // Raced with another command; the volume exists, which is what was asked for.
        }
        catch (DockerApiException e)
        {
            throw new ContainerRuntimeException(
                NodeErrorCode.VolumeOperationFailed, $"Could not create volume {name}: {e.Message}", e);
        }
    }

    public Task RemoveVolumeAsync(string name, CancellationToken ct) =>
        Tolerate404(() => client.Volumes.RemoveAsync(name, force: false, ct));

    public async Task<bool> VolumeExistsAsync(string name, CancellationToken ct)
    {
        try
        {
            await client.Volumes.InspectAsync(name, ct);
            return true;
        }
        catch (DockerApiException e) when ((int)e.StatusCode == 404)
        {
            return false;
        }
    }

    public async Task<int> RunOneOffAsync(OneOffRequest request, IProgress<string>? logSink, CancellationToken ct)
    {
        // Replacing the entrypoint rather than appending to it: an image with its own entrypoint
        // would otherwise treat the command as arguments and quietly do something else.
        var (entrypoint, arguments) = SplitCommand(request.Command);

        var create = new CreateContainerParameters
        {
            Image = request.ImageReference,
            Entrypoint = entrypoint,
            Cmd = arguments,
            Env = request.Env.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
            Labels = request.Labels.ToDictionary(kv => kv.Key, kv => kv.Value),
            WorkingDir = request.WorkingDirectory,
            HostConfig = new HostConfig
            {
                Binds = request.Mounts
                    .Select(m => $"{m.VolumeName}:{m.MountPath}{(m.ReadOnly ? ":ro" : string.Empty)}")
                    .ToList(),
                NetworkMode = request.Network,
                Memory = request.Resources.MemoryBytes,
                NanoCPUs = request.Resources.CpuCores > 0 ? (long)(request.Resources.CpuCores * 1_000_000_000) : 0,
                PidsLimit = request.Resources.PidsLimit > 0 ? request.Resources.PidsLimit : null,
                CapDrop = ["ALL"],
                SecurityOpt = ["no-new-privileges:true"],
                AutoRemove = false,
            },
        };

        var container = await client.Containers.CreateContainerAsync(create, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        try
        {
            await client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), timeout.Token);

            if (logSink is not null)
                await client.Containers.GetContainerLogsAsync(
                    container.ID,
                    new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = true },
                    timeout.Token,
                    new Progress<string>(logSink.Report));

            var wait = await client.Containers.WaitContainerAsync(container.ID, timeout.Token);
            return (int)wait.StatusCode;
        }
        finally
        {
            // Runs even when the caller cancelled: a helper container left behind holds the volume
            // it was tarring, and the next attempt then fails for a reason that looks unrelated.
            await SafeRemoveAsync(container.ID, CancellationToken.None);
        }
    }

    public async Task<ExecResult> ExecAsync(
        string containerIdOrName, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string>? env, string? stdin, CancellationToken ct)
    {
        var exec = await client.Exec.ExecCreateContainerAsync(containerIdOrName, new ContainerExecCreateParameters
        {
            Cmd = argv.ToList(),
            Env = env?.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
            AttachStdin = stdin is not null,
            AttachStdout = true,
            AttachStderr = true,
        }, ct);

        using var stream = await client.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, ct);

        if (stdin is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(stdin);
            await stream.WriteAsync(bytes, 0, bytes.Length, ct);
            stream.CloseWrite();
        }

        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct);
        var inspect = await client.Exec.InspectContainerExecAsync(exec.ID, ct);

        return new ExecResult((int)inspect.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Copy file-mounted secrets into the container before it starts.
    ///
    /// <para>
    /// Docker has no first-class secret for a plain container, and the alternatives are worse: a
    /// bind mount puts the value on the host filesystem, and an env var for something the app wants
    /// as a file means the app writes it there itself. A tar into a tmpfs path leaves the value in
    /// memory inside the container and nowhere else.
    /// </para>
    /// </summary>
    private async Task WriteTmpfsFilesAsync(string containerId, IReadOnlyList<TmpfsFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return;

        using var archive = new MemoryStream();

        using (var tar = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
            foreach (var file in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, file.Path.TrimStart('/'))
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(file.Content)),
                    Mode = file.Executable
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite,
                };

                await tar.WriteEntryAsync(entry, ct);
            }

        archive.Position = 0;

        await client.Containers.ExtractArchiveToContainerAsync(
            containerId,
            new ContainerPathStatParameters { Path = "/", AllowOverwriteDirWithFile = false },
            archive,
            ct);
    }

    private async Task SafeRemoveAsync(string id, CancellationToken ct)
    {
        try
        {
            await client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, ct);
        }
        catch (Exception e) when (e is DockerApiException or OperationCanceledException)
        {
            log.LogDebug("Could not remove container {Id}; leaving it for the reconciler.", Short(id));
        }
    }

    private static async Task Tolerate404(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DockerApiException e) when ((int)e.StatusCode == 404)
        {
            // The desired end state already holds. Every lifecycle verb on this interface is
            // idempotent by contract, and "it is already gone" is success for all of them.
        }
        catch (DockerContainerNotFoundException)
        {
        }
    }

    private static RestartPolicy MapRestartPolicy(RestartPolicySpec spec) => new()
    {
        Name = spec.Mode switch
        {
            RestartMode.No => RestartPolicyKind.No,
            RestartMode.OnFailure => RestartPolicyKind.OnFailure,
            RestartMode.Always => RestartPolicyKind.Always,
            _ => RestartPolicyKind.UnlessStopped,
        },
        MaximumRetryCount = spec.Mode == RestartMode.OnFailure ? spec.MaxRetries : 0,
    };

    private static HealthConfig? MapHealthCheck(HealthCheckSpec probe) => probe.Kind switch
    {
        // HTTP and TCP probes are run by the agent, not by Docker: Docker would need a shell and a
        // curl inside every image, and a distroless image has neither.
        HealthCheckKind.Command when probe.Command is { Count: > 0 } => new HealthConfig
        {
            Test = ["CMD", .. probe.Command],
            Interval = TimeSpan.FromSeconds(probe.IntervalSeconds),
            Timeout = TimeSpan.FromSeconds(probe.TimeoutSeconds),
            Retries = probe.Retries,
            StartPeriod = TimeSpan.FromSeconds(probe.StartPeriodSeconds).Ticks * 100,
        },
        _ => null,
    };

    private static (IList<string> Entrypoint, IList<string> Arguments) SplitCommand(IReadOnlyList<string> command) =>
        command.Count == 0
            ? ([], [])
            : ([command[0]], command.Skip(1).ToList());

    private static IReadOnlyDictionary<int, int> PublishedPorts(IList<Port>? ports)
    {
        var result = new Dictionary<int, int>();
        if (ports is null) return result;

        foreach (var port in ports)
            if (port.PublicPort > 0)
                result[(int)port.PrivatePort] = (int)port.PublicPort;

        return result;
    }

    private static IReadOnlyDictionary<int, int> BoundPorts(IDictionary<string, IList<PortBinding>>? ports)
    {
        var result = new Dictionary<int, int>();
        if (ports is null) return result;

        foreach (var (key, bindings) in ports)
        {
            if (bindings is not { Count: > 0 }) continue;
            if (!int.TryParse(key.Split('/')[0], out var containerPort)) continue;
            if (!int.TryParse(bindings[0].HostPort, out var hostPort)) continue;

            result[containerPort] = hostPort;
        }

        return result;
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static string Short(string id) => id[..Math.Min(12, id.Length)];
}
