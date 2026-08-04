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
    public async Task PublishAsync(NodeEvent nodeEvent, CancellationToken ct)
    {
        log.LogInformation("Event {Kind}: {Message}", nodeEvent.Kind, nodeEvent.Message);

        try
        {
            await channel.SendAsync(NodeFrames.Event, nodeEvent, correlationId: null, ct);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // The outbox already holds it; a send failure here is not worth failing the caller for.
            log.LogDebug(e, "Event {Kind} is queued for the next connection.", nodeEvent.Kind);
        }
    }
}

/// <summary>A publisher that only writes to the journal. Used before the channel exists.</summary>
public sealed class LocalEventPublisher(ILogger<LocalEventPublisher> log) : INodeEventPublisher
{
    public Task PublishAsync(NodeEvent nodeEvent, CancellationToken ct)
    {
        log.LogInformation("Event {Kind}: {Message}", nodeEvent.Kind, nodeEvent.Message);
        return Task.CompletedTask;
    }
}
