using Harbora.NodeAgent.Commands;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Transport;

namespace Harbora.NodeAgent.Hosting;

/// <summary>
/// Sends a command's ack, progress, logs and result over the control channel.
///
/// <para>
/// Acks and results are queued durably; progress and log chunks are not. That asymmetry is the
/// point: losing "the deploy finished" desynchronises the panel from the node, while losing
/// "pulling layer 3 of 7" costs a progress bar a frame. Queueing the chatty ones would fill the
/// outbox with information that is stale by the time it could be replayed.
/// </para>
/// </summary>
public sealed class ChannelResponder(ControlChannel channel) : ICommandResponder
{
    public Task AckAsync(CommandAck ack, string correlationId, CancellationToken ct) =>
        channel.SendAsync(NodeFrames.CommandAck, ack, correlationId, ct);

    public Task ProgressAsync(CommandProgress progress, string correlationId, CancellationToken ct) =>
        channel.SendEphemeralAsync(NodeFrames.CommandProgress, progress, ct);

    public Task LogAsync(LogChunk chunk, string correlationId, CancellationToken ct) =>
        channel.SendEphemeralAsync(NodeFrames.LogChunk, chunk, ct);

    public Task ResultAsync(CommandResult result, string correlationId, CancellationToken ct) =>
        channel.SendAsync(NodeFrames.CommandResult, result, correlationId, ct);
}
