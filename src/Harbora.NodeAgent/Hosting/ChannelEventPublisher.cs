using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.Transport;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Hosting;

/// <summary>
/// Sends unsolicited node events over the control channel.
///
/// <para>
/// Queued rather than ephemeral: "the deploy rolled back" is exactly the message that must not be
/// lost to the disconnect that a failing deploy tends to come with. It also lands in the local
/// journal, so the story is reconstructable from the node alone when the panel never heard it.
/// </para>
/// </summary>
public sealed class ChannelEventPublisher(ControlChannel channel, ILogger<ChannelEventPublisher> log)
    : INodeEventPublisher
{
    public async Task<bool> PublishAsync(NodeEvent nodeEvent, CancellationToken ct)
    {
        log.LogInformation("Event {Kind}: {Message}", nodeEvent.Kind, nodeEvent.Message);

        try
        {
            await channel.SendAsync(NodeFrames.Event, nodeEvent, correlationId: null, ct);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // This used to say "queued for the next connection", and it could never have been true.
            // ControlChannel.SendAsync appends to the outbox first and then swallows a failed
            // transmission itself, so the only way one of these reaches here is that the outbox
            // write failed — a full disk, which is exactly the state a pressure event describes.
            // Nothing holds the event at that point, and saying otherwise sent an operator looking
            // for a frame that was never written.
            log.LogError(
                e,
                "Event {Kind} could not be recorded for delivery and is lost unless its source repeats it.",
                nodeEvent.Kind);

            return false;
        }
    }

    public async Task<bool> PublishEphemeralAsync(NodeEvent nodeEvent, CancellationToken ct)
    {
        bool sent;

        try
        {
            sent = await channel.SendEphemeralAsync(NodeFrames.Event, nodeEvent, ct);
        }
        // SendEphemeralAsync handles the transport's own failure modes; anything else — a disposed
        // socket, a payload that will not serialize — would otherwise escape into the heartbeat
        // loop and cost the node a heartbeat over an event nobody was waiting for.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Event {Kind} could not be sent; the node will offer it again.", nodeEvent.Kind);
            return false;
        }

        if (sent)
        {
            log.LogInformation("Event {Kind}: {Message}", nodeEvent.Kind, nodeEvent.Message);
            return true;
        }

        // Debug, not Error: a node with no connection is the ordinary case, and the caller is going
        // to work this out again and offer it on the next heartbeat. Logging it as a failure once
        // every thirty seconds for the length of an outage would bury the ones that matter.
        log.LogDebug(
            "Event {Kind} was not sent; the channel is down and the node will offer it again.",
            nodeEvent.Kind);

        return false;
    }
}

/// <summary>A publisher that only writes to the journal. Used before the channel exists.</summary>
public sealed class LocalEventPublisher(ILogger<LocalEventPublisher> log) : INodeEventPublisher
{
    /// <summary>
    /// True because the journal is all this one ever promised. It is not registered in the running
    /// agent, and a caller that needs the control plane to hear about something must not be given
    /// this publisher.
    /// </summary>
    public Task<bool> PublishAsync(NodeEvent nodeEvent, CancellationToken ct)
    {
        log.LogInformation("Event {Kind}: {Message}", nodeEvent.Kind, nodeEvent.Message);
        return Task.FromResult(true);
    }

    public Task<bool> PublishEphemeralAsync(NodeEvent nodeEvent, CancellationToken ct) =>
        PublishAsync(nodeEvent, ct);
}
