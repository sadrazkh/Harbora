using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Harbora.NodeAgent.Commands;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Runtime;

/// <summary>Somewhere for the agent to announce state changes the control plane did not ask about.</summary>
public interface INodeEventPublisher
{
    /// <summary>
    /// Announce an unsolicited change, durably. Returns false when the event could not even be made
    /// durable, so a caller that can say it again knows it has to. Most callers cannot and ignore it
    /// — a deploy must not fail because the news about it did.
    /// </summary>
    Task<bool> PublishAsync(NodeEvent nodeEvent, CancellationToken ct);

    /// <summary>
    /// Announce a change the caller can work out again, without spending durable outbox capacity on
    /// it.
    ///
    /// <para>
    /// The outbox is capped and evicts its oldest entries first, and what it exists to protect is
    /// the frame saying a deploy finished. An event whose condition is re-derived from the host on
    /// every heartbeat does not need that protection and must not compete for it: it is either sent
    /// now or worked out and offered again in thirty seconds. Returns false when it did not go out,
    /// and a caller must not record it as told.
    /// </para>
    /// </summary>
    Task<bool> PublishEphemeralAsync(NodeEvent nodeEvent, CancellationToken ct);
}

/// <summary>Raised when a deploy cannot proceed; carries the contract code and every violation found.</summary>
public sealed class DeploymentRefusedException(NodeErrorCode code, string message, IReadOnlyList<PolicyViolation> violations)
    : Exception(message)
{
    public NodeErrorCode Code { get; } = code;
    public IReadOnlyList<PolicyViolation> Violations { get; } = violations;
}

/// <summary>
/// Turns a workload specification into running containers, and puts the previous release back when
/// the new one does not become healthy.
///
/// <para>
/// Container names carry the release id, so a new release starts alongside the old one and the
/// cutover is a rename-free swap. That is what makes the rollback cheap enough to be automatic:
/// the thing being rolled back to never stopped existing.
/// </para>
/// </summary>
public sealed class WorkloadDeployer(
    IOptions<NodeAgentOptions> options,
    IContainerRuntime runtime,
    WorkloadRegistry registry,
    PortAllocator portAllocator,
    HealthProbe health,
    IHostFacts host,
    SecretRedactor redactor,
    NodeMetrics metrics,
    INodeEventPublisher events,
    Workspaces.DockerWorkspaceProvisioner workspaces,
    TimeProvider clock,
    ILogger<WorkloadDeployer> log)
{
    private readonly WorkloadPolicy _policy = new(options.Value.Security, options.Value.Ports);

    /// <summary>
    /// Deploy or update a workload. Throws <see cref="DeploymentRefusedException"/> when the spec
    /// is refused outright; a spec that is accepted but fails to come up returns a result with
    /// <c>RolledBack</c> set rather than throwing, because that is an outcome, not an error.
    /// </summary>
    public async Task<DeployWorkloadResult> DeployAsync(
        CommandContext context, DeployWorkloadRequest request, bool hasNodeAdminScope, CancellationToken ct)
    {
        var spec = request.Spec;
        var stopwatch = Stopwatch.StartNew();

        await context.ReportAsync("validating", 5, $"checking the specification for {spec.Name}", ct);

        // A Docker workspace is not an ordinary workload with extra flags: it is a separate,
        // separately-gated path, and the spec it deploys is the node's hardened version rather
        // than anything the control plane sent.
        if (Workspaces.DockerWorkspaceProvisioner.IsWorkspace(spec))
        {
            var decision = workspaces.Evaluate(spec, hasNodeAdminScope);

            if (!decision.Allowed)
                throw new DeploymentRefusedException(
                    decision.Violations[0].Code,
                    string.Join(" ", decision.Violations.Select(v => v.Message)),
                    decision.Violations);

            spec = decision.Hardened!;
        }

        var violations = _policy.Validate(spec, host.Architecture, AgentVersion.Current, hasNodeAdminScope);

        if (request.Manifest is { } manifest)
            violations = [.. violations, .. ValidateAgainstManifest(spec, manifest)];

        if (violations.Count > 0)
        {
            // The first violation's code answers the control plane; the rest are in the message so
            // an operator fixing a template sees the whole list instead of one per deploy attempt.
            var summary = string.Join(" ", violations.Select(v => v.Message));
            throw new DeploymentRefusedException(violations[0].Code, summary, violations);
        }

        if (context.TenantId is { Length: > 0 } tenant && tenant != spec.TenantId)
            throw new DeploymentRefusedException(
                NodeErrorCode.Unauthorized,
                $"the command acts for tenant '{tenant}' but the spec belongs to '{spec.TenantId}'.",
                []);

        var existing = registry.Find(spec.WorkloadId, spec.TenantId);
        var fingerprint = Fingerprint(spec);

        if (existing is not null && existing.SpecFingerprint == fingerprint && await IsHealthyAsync(existing, ct))
        {
            // Reconciliation, not repetition: the desired state already holds. Re-creating the
            // containers would be a gratuitous restart of a healthy service.
            log.LogInformation("{Workload} already matches the requested spec and is healthy; nothing to do.", spec.Name);

            return new DeployWorkloadResult
            {
                WorkloadId = spec.WorkloadId,
                Deployed = false,
                Status = await StatusAsync(existing, ct),
                AllocatedPorts = existing.AllocatedPorts,
                ResolvedDigests = existing.ResolvedDigests,
                DeployDurationMs = stopwatch.ElapsedMilliseconds,
                Warnings = ["The workload already matched this specification and was healthy; no changes were made."],
            };
        }

        if (request.DryRun)
            return new DeployWorkloadResult
            {
                WorkloadId = spec.WorkloadId,
                Deployed = false,
                Status = existing is null
                    ? new WorkloadStatus { WorkloadId = spec.WorkloadId, State = "absent" }
                    : await StatusAsync(existing, ct),
                DeployDurationMs = stopwatch.ElapsedMilliseconds,
                Warnings = ["Dry run: the specification is valid and nothing was changed."],
            };

        RegisterSecrets(spec);

        var pullMs = await PullImagesAsync(context, spec, ct);
        var digests = await ResolveDigestsAsync(spec, ct);

        await EnsureNetworksAsync(spec, ct);
        await EnsureVolumesAsync(spec, ct);

        var release = new WorkloadRecord
        {
            WorkloadId = spec.WorkloadId,
            TenantId = spec.TenantId,
            Name = spec.Name,
            Spec = spec,
            ReleaseId = NewReleaseId(),
            AppVersion = spec.AppVersion,
            ResolvedDigests = digests,
            AllocatedPorts = AllocatePorts(spec),
            SpecFingerprint = fingerprint,
            DeployedAt = clock.GetUtcNow(),
            Previous = existing?.Previous is null ? existing : existing with { Previous = null },
        };

        // Recreate frees the ports and the network aliases before the new release wants them;
        // blue/green needs the old one alive to cut over from.
        if (existing is not null && spec.Upgrade.Mode == UpgradeMode.Recreate)
        {
            await context.ReportAsync("stopping", 35, "stopping the previous release", ct);
            await StopContainersAsync(existing, ct);
        }

        var started = new List<string>();

        try
        {
            await context.ReportAsync("starting", 55, $"starting {spec.Containers.Count} container(s)", ct);
            started = await StartContainersAsync(release, ct);

            await context.ReportAsync("verifying", 80, "waiting for the new release to become healthy", ct);
            var verdict = await VerifyAsync(release, ct);

            if (!verdict.Healthy)
                return await RollBackAsync(context, release, existing, started, verdict.Detail, stopwatch, pullMs, ct);
        }
        catch (Exception e) when (e is ContainerRuntimeException or IOException)
        {
            return await RollBackAsync(context, release, existing, started, e.Message, stopwatch, pullMs, ct);
        }

        if (existing is not null && spec.Upgrade.Mode != UpgradeMode.Recreate)
        {
            await context.ReportAsync("cutover", 92, "retiring the previous release", ct);
            await StopContainersAsync(existing, ct);
            await RemoveContainersAsync(existing, ct);
        }
        else if (existing is not null)
        {
            await RemoveContainersAsync(existing, ct);
        }

        registry.Save(release);
        stopwatch.Stop();

        metrics.DeploymentCompleted(succeeded: true, rolledBack: false, stopwatch.ElapsedMilliseconds, pullMs);

        await events.PublishAsync(new NodeEvent
        {
            Kind = NodeEventKinds.DeploymentCompleted,
            Message = $"{spec.Name} {spec.AppVersion ?? release.ReleaseId} is running",
            WorkloadId = spec.WorkloadId,
            Data = new Dictionary<string, string> { ["releaseId"] = release.ReleaseId },
        }, ct);

        log.LogInformation(
            "Deployed {Workload} release {Release} in {Elapsed}ms.", spec.Name, release.ReleaseId, stopwatch.ElapsedMilliseconds);

        return new DeployWorkloadResult
        {
            WorkloadId = spec.WorkloadId,
            Deployed = true,
            Status = await StatusAsync(release, ct),
            AllocatedPorts = release.AllocatedPorts,
            ResolvedDigests = digests,
            PullDurationMs = pullMs,
            DeployDurationMs = stopwatch.ElapsedMilliseconds,
        };
    }

    /// <summary>Point-in-time view of a workload, for <c>GetWorkloadStatus</c> and deploy results.</summary>
    public async Task<WorkloadStatus> StatusAsync(WorkloadRecord record, CancellationToken ct)
    {
        var containers = new List<ContainerStatus>();
        var healthy = true;
        string? lastError = null;
        DateTimeOffset? startedAt = null;

        foreach (var spec in record.Spec.Containers)
        {
            var name = record.ContainerName(spec.Name);
            var inspected = await runtime.InspectAsync(name, ct);

            if (inspected is null)
            {
                healthy = false;
                lastError = $"container {name} is not present";
                containers.Add(new ContainerStatus(spec.Name, string.Empty, "absent", spec.Image.ToString(), false, 0));
                continue;
            }

            var containerHealthy = inspected.State == "running" && inspected.Healthy != false;
            healthy &= containerHealthy;
            startedAt ??= inspected.StartedAt;

            containers.Add(new ContainerStatus(
                spec.Name, inspected.Id, inspected.State, inspected.Image, containerHealthy, inspected.RestartCount));
        }

        return new WorkloadStatus
        {
            WorkloadId = record.WorkloadId,
            // "absent" and "stopped" are different problems with different fixes: a stopped
            // workload is started again, a workload whose containers are gone has to be redeployed
            // by the control plane. Collapsing them would make the reconciler try to start
            // containers that no longer exist.
            State = containers.Count == 0 || containers.All(c => c.State == "absent") ? "absent"
                : containers.All(c => c.State == "running") ? "running"
                : containers.Any(c => c.State is "running" or "absent") ? "degraded"
                : "stopped",
            ContainerId = containers.FirstOrDefault()?.ContainerId,
            ImageDigest = record.ResolvedDigests.Values.FirstOrDefault(),
            AppVersion = record.AppVersion,
            StartedAt = startedAt,
            RestartCount = containers.Sum(c => c.RestartCount),
            Healthy = healthy,
            LastError = lastError,
            Containers = containers,
        };
    }

    public Task StopAsync(WorkloadRecord record, CancellationToken ct) => StopContainersAsync(record, ct);

    public async Task StartAsync(WorkloadRecord record, CancellationToken ct)
    {
        foreach (var container in record.Spec.Containers)
            await runtime.StartAsync(record.ContainerName(container.Name), ct);
    }

    public async Task RestartAsync(WorkloadRecord record, CancellationToken ct)
    {
        foreach (var container in record.Spec.Containers)
            await runtime.RestartAsync(record.ContainerName(container.Name), ct);
    }

    /// <summary>
    /// Remove a workload. Volumes survive unless the command explicitly says otherwise — deleting a
    /// workload and deleting the data it held are different decisions, and only one is reversible.
    /// </summary>
    public async Task DeleteAsync(WorkloadRecord record, bool deleteVolumes, bool force, CancellationToken ct)
    {
        await StopContainersAsync(record, ct);
        await RemoveContainersAsync(record, ct);

        if (record.Previous is { } previous)
        {
            await StopContainersAsync(previous, ct);
            await RemoveContainersAsync(previous, ct);
        }

        if (deleteVolumes)
            foreach (var volume in record.Spec.Volumes)
            {
                log.LogWarning("Removing volume {Volume} for {Workload} — this destroys its data.", volume.Name, record.Name);
                await runtime.RemoveVolumeAsync(volume.Name, ct);
            }

        registry.Remove(record.WorkloadId);

        _ = force; // Containers are already removed with force; kept for contract symmetry.
    }

    // --- internals ---

    private async Task<DeployWorkloadResult> RollBackAsync(
        CommandContext context, WorkloadRecord failed, WorkloadRecord? previous,
        IReadOnlyList<string> startedContainers, string reason, Stopwatch stopwatch, long pullMs, CancellationToken ct)
    {
        stopwatch.Stop();

        log.LogError("Release {Release} of {Workload} failed health validation: {Reason}", failed.ReleaseId, failed.Name, reason);

        await context.ReportAsync("rolling-back", 95, $"the new release did not become healthy: {reason}", ct);

        foreach (var id in startedContainers)
            await SafeAsync(() => runtime.RemoveAsync(id, force: true, ct));

        var restored = false;

        if (previous is not null && failed.Spec.Upgrade.AutoRollbackOnFailure)
        {
            try
            {
                // Under blue/green the old containers were never stopped, so this is a no-op that
                // costs nothing; under recreate it is the whole point.
                await StartAsync(previous, ct);
                restored = true;
                registry.Save(previous);
            }
            catch (Exception e) when (e is ContainerRuntimeException or IOException)
            {
                // Worth shouting about: the node is now running neither release.
                log.LogCritical(e, "Could not restore the previous release of {Workload}. The workload is down.", failed.Name);
            }
        }

        metrics.DeploymentCompleted(succeeded: false, rolledBack: restored, stopwatch.ElapsedMilliseconds, pullMs);

        await events.PublishAsync(new NodeEvent
        {
            Kind = restored ? NodeEventKinds.DeploymentRolledBack : NodeEventKinds.DeploymentFailed,
            Message = $"{failed.Name}: {reason}",
            WorkloadId = failed.WorkloadId,
            Data = new Dictionary<string, string>
            {
                ["releaseId"] = failed.ReleaseId,
                ["restoredRelease"] = previous?.ReleaseId ?? "none",
            },
        }, ct);

        return new DeployWorkloadResult
        {
            WorkloadId = failed.WorkloadId,
            Deployed = false,
            RolledBack = restored,
            PreviousVersion = previous?.AppVersion,
            Status = previous is null
                ? new WorkloadStatus { WorkloadId = failed.WorkloadId, State = "failed", Healthy = false, LastError = reason }
                : await StatusAsync(previous, ct),
            PullDurationMs = pullMs,
            DeployDurationMs = stopwatch.ElapsedMilliseconds,
            Warnings = restored
                ? [$"The new release failed health validation ({reason}); the previous release was restored."]
                : [$"The new release failed health validation ({reason}) and there was no previous release to restore."],
        };
    }

    private async Task<long> PullImagesAsync(CommandContext context, WorkloadSpec spec, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        foreach (var container in spec.Containers)
        {
            await context.ReportAsync("pulling", 20, $"pulling {container.Image.Repository}", ct);
            await runtime.PullImageAsync(container.Image.PullReference, context.ProgressLines("pulling", ct), ct);
        }

        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }

    /// <summary>
    /// Confirm the digest that landed is the digest that was asked for.
    ///
    /// <para>
    /// Pulling by digest already makes substitution hard, but "hard" is not "checked". Reading back
    /// what the daemon actually has turns an assumption into an assertion, and the cost is one API
    /// call per container.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolveDigestsAsync(WorkloadSpec spec, CancellationToken ct)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var container in spec.Containers)
        {
            var resolved = await runtime.ResolveDigestAsync(container.Image.PullReference, ct);

            if (resolved is null)
                throw new ContainerRuntimeException(
                    NodeErrorCode.ImagePullFailed,
                    $"{container.Image.PullReference} is not present after the pull reported success.");

            if (!resolved.Equals(container.Image.Digest, StringComparison.OrdinalIgnoreCase))
                throw new ContainerRuntimeException(
                    NodeErrorCode.ImagePullFailed,
                    $"{container.Image.Repository} resolved to {resolved} but the spec pinned {container.Image.Digest}.");

            digests[container.Name] = resolved;
        }

        return digests;
    }

    private async Task EnsureNetworksAsync(WorkloadSpec spec, CancellationToken ct)
    {
        foreach (var network in spec.Networks)
            await runtime.EnsureNetworkAsync(network, NodeLabels.For(spec.TenantId, spec.WorkloadId), ct);
    }

    private async Task EnsureVolumesAsync(WorkloadSpec spec, CancellationToken ct)
    {
        foreach (var volume in spec.Volumes)
        {
            var labels = NodeLabels.For(spec.TenantId, spec.WorkloadId);
            foreach (var (key, value) in volume.Labels) labels[key] = value;

            await runtime.EnsureVolumeAsync(volume.Name, labels, ct);
        }
    }

    private IReadOnlyDictionary<string, int> AllocatePorts(WorkloadSpec spec)
    {
        var requests = spec.Containers
            .SelectMany(c => c.Ports.Where(p => p.PublishToHost).Select(p => (Container: c.Name, Port: p)))
            .ToList();

        if (requests.Count == 0) return new Dictionary<string, int>();

        var inUse = registry.AllocatedPorts().Concat(host.ListeningPorts()).ToHashSet();
        var allocated = new Dictionary<string, int>(StringComparer.Ordinal);
        var needed = requests.Where(r => r.Port.HostPort is null).ToList();
        var pool = new Queue<int>(portAllocator.Allocate(needed.Count, inUse));

        foreach (var request in requests)
            allocated[$"{request.Container}:{request.Port.ContainerPort}"] =
                request.Port.HostPort ?? pool.Dequeue();

        return allocated;
    }

    private async Task<List<string>> StartContainersAsync(WorkloadRecord release, CancellationToken ct)
    {
        var started = new List<string>();

        foreach (var container in release.Spec.Containers)
        {
            var labels = NodeLabels.For(release.TenantId, release.WorkloadId);
            labels[NodeLabels.Container] = container.Name;
            labels[NodeLabels.Release] = release.ReleaseId;
            if (release.Spec.AppId is { } appId) labels[NodeLabels.App] = appId;
            if (release.AppVersion is { } version) labels[NodeLabels.AppVersion] = version;
            foreach (var (key, value) in release.Spec.Labels) labels[key] = value;

            var (env, files) = InjectSecrets(container);

            var request = new ContainerCreateRequest
            {
                Name = release.ContainerName(container.Name),
                ImageReference = container.Image.PullReference,
                Command = container.Command,
                Env = env,
                TmpfsFiles = files,
                Labels = labels,
                Network = release.Spec.Networks.FirstOrDefault()?.Name,
                // The stable alias is what a sibling container connects to. Without it, a compose
                // service configured for "db:5432" breaks on every release, because the container
                // name carries a release id that changes.
                NetworkAliases = [container.Name, .. container.NetworkAliases],
                Mounts = container.Mounts.Select(m => new VolumeMount(m.VolumeName, m.MountPath, m.ReadOnly)).ToList(),
                Ports = container.Ports.Select(p => new PortPublication(
                    p.ContainerPort,
                    p.PublishToHost ? release.AllocatedPorts.GetValueOrDefault($"{container.Name}:{p.ContainerPort}") : null,
                    p.Protocol)).ToList(),
                Resources = container.Resources,
                HealthCheck = container.HealthCheck,
                RestartPolicy = container.RestartPolicy,
                User = container.User,
                ReadOnlyRootFilesystem = container.ReadOnlyRootFilesystem,
                CapabilitiesAdd = container.CapabilitiesAdd,
                CapabilitiesDrop = container.CapabilitiesDrop.Count > 0 ? container.CapabilitiesDrop : ["ALL"],
                Privileged = container.Privileged,
                HostNetwork = container.HostNetwork,
                HostPidNamespace = container.HostPidNamespace,
                NoNewPrivileges = !container.Privileged,
                StopGracePeriodSeconds = container.StopGracePeriodSeconds,
            };

            var id = await runtime.CreateAndStartAsync(request, ct);
            started.Add(id);

            metrics.ContainerStateChanged(release.WorkloadId, "started");
        }

        return started;
    }

    /// <summary>
    /// Split a container's secrets into environment entries and tmpfs files, registering every
    /// value with the redactor first so it is scrubbed from anything written afterwards.
    /// </summary>
    private (IReadOnlyDictionary<string, string> Env, IReadOnlyList<TmpfsFile> Files) InjectSecrets(ContainerSpec container)
    {
        var env = new Dictionary<string, string>(container.Env, StringComparer.Ordinal);
        var files = new List<TmpfsFile>();

        foreach (var secret in container.Secrets)
        {
            redactor.Register(secret.Value);

            if (secret.MountAs == SecretMount.File && secret.TargetPath is { Length: > 0 } path)
                files.Add(new TmpfsFile(path, secret.Value));
            else
                env[secret.Name] = secret.Value;
        }

        return (env, files);
    }

    private void RegisterSecrets(WorkloadSpec spec) =>
        redactor.RegisterAll(spec.Containers.SelectMany(c => c.Secrets).Select(s => s.Value));

    private async Task<HealthOutcome> VerifyAsync(WorkloadRecord release, CancellationToken ct)
    {
        var grace = TimeSpan.FromSeconds(Math.Max(5, release.Spec.Upgrade.HealthGraceSeconds));
        var network = release.Spec.Networks.FirstOrDefault()?.Name;

        foreach (var container in release.Spec.Containers)
        {
            var name = release.ContainerName(container.Name);
            var outcome = await health.WaitForHealthyAsync(container, name, network, grace, ct);

            if (!outcome.Healthy) return new HealthOutcome(false, $"{container.Name}: {outcome.Detail}");
        }

        return new HealthOutcome(true, "all containers are healthy");
    }

    private async Task<bool> IsHealthyAsync(WorkloadRecord record, CancellationToken ct)
    {
        var status = await StatusAsync(record, ct);
        return status is { State: "running", Healthy: true };
    }

    private async Task StopContainersAsync(WorkloadRecord record, CancellationToken ct)
    {
        foreach (var container in record.Spec.Containers)
        {
            await SafeAsync(() => runtime.StopAsync(
                record.ContainerName(container.Name), container.StopGracePeriodSeconds, ct));

            metrics.ContainerStateChanged(record.WorkloadId, "stopped");
        }
    }

    private async Task RemoveContainersAsync(WorkloadRecord record, CancellationToken ct)
    {
        foreach (var container in record.Spec.Containers)
            await SafeAsync(() => runtime.RemoveAsync(record.ContainerName(container.Name), force: true, ct));
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (e is ContainerRuntimeException or IOException)
        {
            // Cleanup failures must not mask the outcome that caused the cleanup.
            log.LogDebug(e, "A cleanup step failed and was ignored.");
        }
    }

    /// <summary>
    /// Cross-check the spec against the manifest it claims to come from. The control plane renders
    /// one into the other, so a mismatch means the two drifted — which is exactly the case where
    /// running the spec anyway would deploy something nobody described.
    /// </summary>
    private IReadOnlyList<PolicyViolation> ValidateAgainstManifest(WorkloadSpec spec, AppManifest manifest)
    {
        var violations = new List<PolicyViolation>();

        if (!AgentVersion.IsAtLeast(AgentVersion.Current, manifest.MinimumNodeVersion))
            violations.Add(new(NodeErrorCode.AgentTooOld,
                $"manifest {manifest.AppId} {manifest.ApplicationVersion} needs agent {manifest.MinimumNodeVersion}; this node runs {AgentVersion.Current}."));

        if (!manifest.SupportedArchitectures.Contains(host.Architecture, StringComparer.OrdinalIgnoreCase))
            violations.Add(new(NodeErrorCode.UnsupportedArchitecture,
                $"manifest {manifest.AppId} supports {string.Join(", ", manifest.SupportedArchitectures)}; this node is {host.Architecture}."));

        foreach (var architecture in manifest.SupportedArchitectures)
        foreach (var image in manifest.Images)
            if (!image.DigestByArchitecture.ContainsKey(architecture))
                violations.Add(new(NodeErrorCode.ValidationFailed,
                    $"manifest image '{image.Role}' claims support for {architecture} but carries no digest for it."));

        foreach (var field in manifest.EnvironmentSchema.Where(f => f.Required))
        {
            var present = spec.Containers.Any(c => c.Env.ContainsKey(field.Key));
            if (!present)
                violations.Add(new(NodeErrorCode.ValidationFailed,
                    $"manifest requires environment variable '{field.Key}', which the spec does not set."));
        }

        foreach (var field in manifest.SecretSchema.Where(f => f.Required))
        {
            var present = spec.Containers.Any(c => c.Secrets.Any(s => s.Name == field.Key));
            if (!present)
                violations.Add(new(NodeErrorCode.ValidationFailed,
                    $"manifest requires secret '{field.Key}', which the spec does not supply."));
        }

        return violations;
    }

    /// <summary>
    /// A stable hash of the spec. Two deploys of the same specification produce the same value, so
    /// a redelivered command after the idempotency window has expired is still recognised as a
    /// no-op rather than a gratuitous restart.
    /// </summary>
    internal static string Fingerprint(WorkloadSpec spec)
    {
        var canonical = NodeContract.Serialize(spec);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// A release id that is unique, not merely sortable.
    ///
    /// <para>
    /// It must be random rather than time-ordered. A truncated version-7 GUID looks like a fine id
    /// and is not: its leading hex digits are the millisecond timestamp, so two releases seconds
    /// apart share a prefix — and since the id is part of the container name, the new release would
    /// collide with the one it is supposed to run alongside. Docker refuses the duplicate name and
    /// the deploy fails for a reason that has nothing to do with the workload.
    /// </para>
    /// </summary>
    private static string NewReleaseId() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));
}
