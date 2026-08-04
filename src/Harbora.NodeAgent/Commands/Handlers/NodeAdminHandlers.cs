using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Updates;

namespace Harbora.NodeAgent.Commands.Handlers;

/// <summary>Takes the node out of service, or puts it back.</summary>
public sealed class DrainNodeHandler(DrainCoordinator drain) : INodeCommandHandler
{
    public string Command => NodeCommands.DrainNode;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<DrainNodeRequest>() ?? new DrainNodeRequest();

        var result = request.Drain
            ? await drain.DrainAsync(
                request.StopWorkloads,
                TimeSpan.FromSeconds(Math.Max(0, request.TimeoutSeconds)),
                request.Reason,
                ct)
            : await drain.UndrainAsync(ct);

        return context.Ok(result);
    }
}

/// <summary>
/// Updates the agent itself.
///
/// <para>
/// The result is sent before the restart takes effect, because the process is about to be replaced
/// — a command that waited for its own success would never report one. What actually confirms the
/// update is the version in the next heartbeat, and the node rolls itself back if that version is
/// wrong.
/// </para>
/// </summary>
public sealed class UpdateAgentHandler(AgentUpdater updater) : INodeCommandHandler
{
    public string Command => NodeCommands.UpdateAgent;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<AgentUpdateRequest>();
        if (request is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no update request.");

        if (string.IsNullOrWhiteSpace(request.Sha256))
            return context.Fail(NodeErrorCode.UpdateVerificationFailed,
                "An update must carry a sha256. Downloading a binary and running it as root without checking it is not something this node will do.");

        var result = await updater.ApplyAsync(request, context.ProgressLines("updating", ct), ct);

        return result.Outcome == AgentUpdateOutcome.Failed
            ? context.Fail(result.Error?.Code ?? NodeErrorCode.UpdateApplyFailed,
                result.Error?.Message ?? result.Message ?? "The update failed.")
            : context.Ok(result);
    }
}
