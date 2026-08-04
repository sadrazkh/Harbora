using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.State;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Commands.Handlers;

/// <summary>
/// Shared plumbing for the verbs that address an existing workload.
///
/// <para>
/// <see cref="Resolve"/> is the tenant boundary. Every lookup goes through it with the tenant from
/// the command's audit metadata, so a workload id belonging to another tenant resolves to nothing
/// rather than to their containers — even if the control plane sends the wrong pair.
/// </para>
/// </summary>
public abstract class WorkloadHandlerBase(WorkloadRegistry registry)
{
    protected WorkloadRegistry Registry { get; } = registry;

    protected WorkloadRecord? Resolve(CommandContext context, string workloadId) =>
        Registry.Find(workloadId, context.TenantId);

    protected static CommandResult NotFound(CommandContext context, string workloadId) =>
        context.Fail(NodeErrorCode.ValidationFailed, $"No workload '{workloadId}' is deployed for this tenant on this node.");
}

/// <summary>Handles <c>DeployWorkload</c> and <c>UpdateWorkload</c> — the same operation.</summary>
public sealed class DeployWorkloadHandler(
    WorkloadDeployer deployer,
    JsonFileStore<NodeState> state,
    string command,
    ILogger<DeployWorkloadHandler> log) : INodeCommandHandler
{
    public string Command { get; } = command;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<DeployWorkloadRequest>();

        if (request?.Spec is null)
            return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no workload specification.");

        // Privileged specs need a node-admin command. Deploy is not one, so this is normally false
        // — the two locks are the host flag and the scope, and neither alone opens the door.
        var hasNodeAdmin = (state.Load() ?? new NodeState()).HasScope(NodeScopes.NodeAdmin) &&
                           context.Envelope.RequiredScope == NodeScopes.NodeAdmin;

        try
        {
            var result = await deployer.DeployAsync(context, request, hasNodeAdmin, ct);
            return context.Ok(result);
        }
        catch (DeploymentRefusedException e)
        {
            log.LogWarning("Refused to deploy {Workload}: {Message}", request.Spec.Name, e.Message);
            return context.Fail(e.Code, e.Message);
        }
        catch (ContainerRuntimeException e)
        {
            return context.Fail(e.Code, e.Message, e.Retryable);
        }
        catch (PortAllocator.NoPortsAvailableException e)
        {
            return context.Fail(NodeErrorCode.InsufficientResources, e.Message, retryable: true);
        }
    }
}

public sealed class StopWorkloadHandler(WorkloadRegistry registry, WorkloadDeployer deployer)
    : WorkloadHandlerBase(registry), INodeCommandHandler
{
    public string Command => NodeCommands.StopWorkload;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<WorkloadRequest>();
        if (request is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no workload id.");

        if (Resolve(context, request.WorkloadId) is not { } record) return NotFound(context, request.WorkloadId);

        var status = await deployer.StatusAsync(record, ct);
        if (status.State == "stopped")
            return context.Ok(new AcknowledgedResult { Applied = true, NoOp = true, Detail = "already stopped" });

        await deployer.StopAsync(record, ct);
        return context.Ok(new AcknowledgedResult { Applied = true });
    }
}

public sealed class StartWorkloadHandler(WorkloadRegistry registry, WorkloadDeployer deployer)
    : WorkloadHandlerBase(registry), INodeCommandHandler
{
    public string Command => NodeCommands.StartWorkload;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<WorkloadRequest>();
        if (request is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no workload id.");

        if (Resolve(context, request.WorkloadId) is not { } record) return NotFound(context, request.WorkloadId);

        var status = await deployer.StatusAsync(record, ct);
        if (status.State == "running")
            return context.Ok(new AcknowledgedResult { Applied = true, NoOp = true, Detail = "already running" });

        await deployer.StartAsync(record, ct);
        return context.Ok(new AcknowledgedResult { Applied = true });
    }
}

public sealed class RestartWorkloadHandler(WorkloadRegistry registry, WorkloadDeployer deployer)
    : WorkloadHandlerBase(registry), INodeCommandHandler
{
    public string Command => NodeCommands.RestartWorkload;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<WorkloadRequest>();
        if (request is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no workload id.");

        if (Resolve(context, request.WorkloadId) is not { } record) return NotFound(context, request.WorkloadId);

        await deployer.RestartAsync(record, ct);
        return context.Ok(new AcknowledgedResult { Applied = true });
    }
}

public sealed class DeleteWorkloadHandler(
    WorkloadRegistry registry, WorkloadDeployer deployer, ILogger<DeleteWorkloadHandler> log)
    : WorkloadHandlerBase(registry), INodeCommandHandler
{
    public string Command => NodeCommands.DeleteWorkload;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<DeleteWorkloadRequest>();
        if (request is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no workload id.");

        if (Resolve(context, request.WorkloadId) is not { } record)
            // Deleting something that is not there is the desired end state, so this is success.
            // Reporting failure would make a retried delete look like a problem to investigate.
            return context.Ok(new AcknowledgedResult { Applied = true, NoOp = true, Detail = "not deployed on this node" });

        if (request.DeleteVolumes)
            log.LogWarning(
                "Deleting {Workload} including {Count} volume(s) — requested by {Actor}.",
                record.Name, record.Spec.Volumes.Count, context.Envelope.Audit?.ActorName ?? "unknown");

        await deployer.DeleteAsync(record, request.DeleteVolumes, request.Force, ct);

        return context.Ok(new AcknowledgedResult
        {
            Applied = true,
            Detail = request.DeleteVolumes ? "workload and volumes removed" : "workload removed; volumes kept",
        });
    }
}

public sealed class GetWorkloadStatusHandler(WorkloadRegistry registry, WorkloadDeployer deployer)
    : WorkloadHandlerBase(registry), INodeCommandHandler
{
    public string Command => NodeCommands.GetWorkloadStatus;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<WorkloadRequest>();
        if (request is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no workload id.");

        if (Resolve(context, request.WorkloadId) is not { } record)
            return context.Ok(new WorkloadStatus { WorkloadId = request.WorkloadId, State = "absent", Healthy = false });

        return context.Ok(await deployer.StatusAsync(record, ct));
    }
}

/// <summary>
/// Streams container output back as <c>log.chunk</c> frames.
///
/// <para>
/// The chunks are ephemeral rather than queued: a log line that could not be delivered is worth
/// nothing by the time a reconnect could replay it, and queueing them would fill the outbox that
/// exists to protect command results.
/// </para>
/// </summary>
public sealed class StreamLogsHandler(WorkloadRegistry registry, IContainerRuntime runtime)
    : WorkloadHandlerBase(registry), INodeCommandHandler
{
    public string Command => NodeCommands.StreamLogs;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<StreamLogsRequest>();
        if (request is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no workload id.");

        if (Resolve(context, request.WorkloadId) is not { } record) return NotFound(context, request.WorkloadId);

        var containerSpec = request.ContainerName is { Length: > 0 } name
            ? record.Spec.Containers.FirstOrDefault(c => c.Name == name)
            : record.Spec.Containers.FirstOrDefault();

        if (containerSpec is null)
            return context.Fail(NodeErrorCode.ValidationFailed,
                $"Workload '{record.Name}' has no container named '{request.ContainerName}'.");

        var containerName = record.ContainerName(containerSpec.Name);
        var lines = 0;

        if (request.Follow)
        {
            // Lines go through a channel rather than straight out of the runtime's callback: the
            // send is async, the callback is not, and a fire-and-forget send would let chunks
            // overtake each other — and overtake the final marker that says the stream ended.
            var pipe = System.Threading.Channels.Channel.CreateUnbounded<string>(
                new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

            var pump = Task.Run(async () =>
            {
                await foreach (var line in pipe.Reader.ReadAllAsync(ct))
                    await context.LogAsync(record.WorkloadId, line, final: false, ct);
            }, ct);

            try
            {
                await runtime.StreamLogsAsync(
                    containerName, request.TailLines,
                    new InlineProgress<string>(line =>
                    {
                        Interlocked.Increment(ref lines);
                        pipe.Writer.TryWrite(line);
                    }),
                    ct);
            }
            finally
            {
                pipe.Writer.TryComplete();
                await pump.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
        else
        {
            var snapshot = await runtime.GetLogsAsync(containerName, request.TailLines, ct);
            lines = snapshot.Count(c => c == '\n');
            await context.LogAsync(record.WorkloadId, snapshot, final: false, ct);
        }

        await context.LogAsync(record.WorkloadId, string.Empty, final: true, ct);

        return context.Ok(new AcknowledgedResult { Applied = true, Detail = $"{lines} line(s) streamed" });
    }
}
