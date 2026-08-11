using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Runtime;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Commands.Handlers;

/// <summary>
/// Creates a tenant network. Idempotent, and labelled with the tenant so the network can be told
/// apart from whatever else the machine's owner runs on the same daemon.
/// </summary>
public sealed class CreateNetworkHandler(IContainerRuntime runtime) : INodeCommandHandler
{
    public string Command => NodeCommands.CreateNetwork;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<NetworkRequest>();
        if (request?.Network is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no network specification.");

        if (Mismatch(context, request.TenantId) is { } refusal) return refusal;

        try
        {
            await runtime.EnsureNetworkAsync(
                request.Network, NodeLabels.For(request.TenantId, request.Network.Name), ct);

            return context.Ok(new AcknowledgedResult { Applied = true });
        }
        catch (ContainerRuntimeException e)
        {
            return context.Fail(e.Code, e.Message, e.Retryable);
        }
    }

    internal static CommandResult? Mismatch(CommandContext context, string tenantId) =>
        context.TenantId is { Length: > 0 } acting && acting != tenantId
            ? context.Fail(NodeErrorCode.Unauthorized,
                $"the command acts for tenant '{acting}' but the payload names '{tenantId}'.")
            : null;
}

/// <summary>
/// Removes a tenant network — but only after checking nothing is still attached.
///
/// <para>
/// Docker would refuse anyway, with a message about endpoints. Checking first turns that into an
/// answer the control plane can act on: which workload is still on it.
/// </para>
/// </summary>
public sealed class DeleteNetworkHandler(IContainerRuntime runtime, ILogger<DeleteNetworkHandler> log) : INodeCommandHandler
{
    public string Command => NodeCommands.DeleteNetwork;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<NetworkRequest>();
        if (request?.Network is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no network specification.");

        if (CreateNetworkHandler.Mismatch(context, request.TenantId) is { } refusal) return refusal;

        var attached = (await runtime.ListContainersAsync(
                new Dictionary<string, string> { [NodeLabels.Tenant] = request.TenantId }, includeStopped: true, ct))
            .Where(c => c.NetworkIpAddresses.ContainsKey(request.Network.Name))
            .Select(c => c.Name)
            .ToList();

        if (attached.Count > 0)
            return context.Fail(NodeErrorCode.NetworkOperationFailed,
                $"Network '{request.Network.Name}' still has {attached.Count} container(s) attached: {string.Join(", ", attached)}.");

        try
        {
            await runtime.RemoveNetworkAsync(request.Network.Name, ct);
            log.LogInformation("Removed network {Network}.", request.Network.Name);
            return context.Ok(new AcknowledgedResult { Applied = true });
        }
        catch (ContainerRuntimeException e)
        {
            return context.Fail(e.Code, e.Message, e.Retryable);
        }
    }
}

public sealed class CreateVolumeHandler(IContainerRuntime runtime) : INodeCommandHandler
{
    public string Command => NodeCommands.CreateVolume;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<VolumeRequest>();
        if (request?.Volume is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no volume specification.");

        if (CreateNetworkHandler.Mismatch(context, request.TenantId) is { } refusal) return refusal;

        // The same name check the deploy path applies: a "volume name" containing a path separator
        // would become a host bind mount the moment something used it.
        if (!VolumeNames.IsPlain(request.Volume.Name))
            return context.Fail(NodeErrorCode.PolicyDenied,
                $"'{request.Volume.Name}' is not a plain volume name.");

        var existed = await runtime.VolumeExistsAsync(request.Volume.Name, ct);

        var labels = NodeLabels.For(request.TenantId, request.Volume.Name);
        foreach (var (key, value) in request.Volume.Labels) labels[key] = value;

        try
        {
            await runtime.EnsureVolumeAsync(request.Volume.Name, labels, ct);
            return context.Ok(new AcknowledgedResult { Applied = true, NoOp = existed });
        }
        catch (ContainerRuntimeException e)
        {
            return context.Fail(e.Code, e.Message, e.Retryable);
        }
    }
}

/// <summary>Archives a volume, optionally stopping the workload that writes to it first.</summary>
public sealed class SnapshotVolumeHandler(
    WorkloadRegistry registry, WorkloadDeployer deployer, VolumeArchiver archiver)
    : WorkloadHandlerBase(registry), INodeCommandHandler
{
    public string Command => NodeCommands.SnapshotVolume;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<SnapshotVolumeRequest>();
        if (request is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no snapshot request.");

        if (CreateNetworkHandler.Mismatch(context, request.TenantId) is { } refusal) return refusal;

        var quiesced = request.QuiesceWorkloadId is { Length: > 0 } id ? Resolve(context, id) : null;

        if (request.QuiesceWorkloadId is { Length: > 0 } wanted && quiesced is null)
            return NotFound(context, wanted);

        try
        {
            if (quiesced is not null)
            {
                await context.ReportAsync("quiescing", 10, $"stopping {quiesced.Name} for a consistent copy", ct);
                await deployer.StopAsync(quiesced, ct);
            }

            try
            {
                await context.ReportAsync("archiving", 40, $"archiving volume {request.VolumeName}", ct);

                var snapshot = await archiver.SnapshotAsync(
                    request.VolumeName, request.SnapshotId, request.Compress,
                    context.ProgressLines("archiving", ct), ct);

                return context.Ok(new SnapshotVolumeResult
                {
                    SnapshotId = snapshot.SnapshotId,
                    Path = snapshot.Path,
                    SizeBytes = snapshot.SizeBytes,
                    Sha256 = snapshot.Sha256,
                    DurationMs = snapshot.DurationMs,
                });
            }
            finally
            {
                // Restart even when the archive failed, and even when the command was cancelled.
                // A backup that leaves the application down is worse than no backup.
                if (quiesced is not null)
                    await deployer.StartAsync(quiesced, CancellationToken.None);
            }
        }
        catch (VolumeArchiver.ArchiveException e)
        {
            return context.Fail(e.Code, e.Message);
        }
    }
}

/// <summary>Relays a staged snapshot only to or from this node's configured control plane.</summary>
public sealed class TransferSnapshotHandler(ArtifactRelayTransfer transfer) : INodeCommandHandler
{
    public string Command => NodeCommands.TransferSnapshot;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<TransferSnapshotRequest>();
        if (request is null)
            return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no transfer request.");
        if (CreateNetworkHandler.Mismatch(context, request.TenantId) is { } refusal) return refusal;

        try
        {
            await context.ReportAsync("transferring", 50, $"transferring snapshot {request.SnapshotId}", ct);
            return context.Ok(await transfer.TransferAsync(request, ct));
        }
        catch (VolumeArchiver.ArchiveException e)
        {
            return context.Fail(e.Code, e.Message, retryable: e.Code == NodeErrorCode.VolumeOperationFailed);
        }
        catch (ContainerRuntimeException e)
        {
            return context.Fail(e.Code, e.Message, e.Retryable);
        }
    }
}

/// <summary>Restores a volume from an archive, verifying the checksum before writing anything.</summary>
public sealed class RestoreVolumeHandler(
    WorkloadRegistry registry, WorkloadDeployer deployer, VolumeArchiver archiver, ILogger<RestoreVolumeHandler> log)
    : WorkloadHandlerBase(registry), INodeCommandHandler
{
    public string Command => NodeCommands.RestoreVolume;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<RestoreVolumeRequest>();
        if (request is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no restore request.");

        if (CreateNetworkHandler.Mismatch(context, request.TenantId) is { } refusal) return refusal;

        var quiesced = request.QuiesceWorkloadId is { Length: > 0 } id ? Resolve(context, id) : null;

        if (request.QuiesceWorkloadId is { Length: > 0 } wanted && quiesced is null)
            return NotFound(context, wanted);

        log.LogWarning(
            "Restoring volume {Volume} from snapshot {Snapshot} — this replaces its contents. Requested by {Actor}.",
            request.VolumeName, request.SnapshotId, context.Envelope.Audit?.ActorName ?? "unknown");

        try
        {
            if (quiesced is not null)
            {
                await context.ReportAsync("quiescing", 10, $"stopping {quiesced.Name}", ct);
                await deployer.StopAsync(quiesced, ct);
            }

            try
            {
                await context.ReportAsync("restoring", 40, $"restoring volume {request.VolumeName}", ct);

                await archiver.RestoreAsync(
                    request.VolumeName, request.SnapshotId, request.ExpectedSha256,
                    compressed: true, context.ProgressLines("restoring", ct), ct);

                await archiver.DeleteSnapshotAsync(request.SnapshotId, compress: true, CancellationToken.None);

                return context.Ok(new AcknowledgedResult { Applied = true, Detail = $"restored from {request.SnapshotId}" });
            }
            finally
            {
                if (quiesced is not null)
                    await deployer.StartAsync(quiesced, CancellationToken.None);
            }
        }
        catch (VolumeArchiver.ArchiveException e)
        {
            return context.Fail(e.Code, e.Message);
        }
    }
}

/// <summary>Shared volume-name rule, so the deploy path and the create path cannot disagree.</summary>
public static class VolumeNames
{
    public static bool IsPlain(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,62}$");
}
