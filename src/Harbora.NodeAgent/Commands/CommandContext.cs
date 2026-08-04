using Harbora.NodeAgent.Contracts;

namespace Harbora.NodeAgent.Commands;

/// <summary>
/// Where a command's ack, progress, log and result frames go. An interface so the dispatcher can
/// be tested without a socket, and so a future transport is a new implementation rather than a
/// change to every handler.
/// </summary>
public interface ICommandResponder
{
    Task AckAsync(CommandAck ack, string correlationId, CancellationToken ct);
    Task ProgressAsync(CommandProgress progress, string correlationId, CancellationToken ct);
    Task LogAsync(LogChunk chunk, string correlationId, CancellationToken ct);
    Task ResultAsync(CommandResult result, string correlationId, CancellationToken ct);
}

/// <summary>Everything a handler is given: the envelope, the clock, and a way to report as it goes.</summary>
public sealed class CommandContext(
    CommandEnvelope envelope,
    ICommandResponder responder,
    DateTimeOffset startedAt)
{
    public CommandEnvelope Envelope { get; } = envelope;
    public DateTimeOffset StartedAt { get; } = startedAt;

    /// <summary>
    /// Tenant this command acts for. Handlers pass it to every runtime call so a workload id from
    /// one tenant cannot address another's containers even if the control plane sends the wrong one.
    /// </summary>
    public string? TenantId => Envelope.Audit?.TenantId;

    public Task ReportAsync(string phase, int? percent = null, string? message = null, CancellationToken ct = default) =>
        responder.ProgressAsync(
            new CommandProgress
            {
                CommandId = Envelope.CommandId,
                Phase = phase,
                Percent = percent,
                Message = message,
            },
            Envelope.CorrelationId,
            ct);

    public Task LogAsync(string workloadId, string text, bool final = false, CancellationToken ct = default) =>
        responder.LogAsync(
            new LogChunk
            {
                CommandId = Envelope.CommandId,
                WorkloadId = workloadId,
                Text = text,
                Final = final,
            },
            Envelope.CorrelationId,
            ct);

    /// <summary>An <see cref="IProgress{T}"/> that turns runtime output lines into progress messages.</summary>
    public IProgress<string> ProgressLines(string phase, CancellationToken ct) =>
        new Progress<string>(line => _ = ReportAsync(phase, message: line, ct: ct));

    public CommandResult Ok<T>(T result) => CommandResult.Ok(Envelope.CommandId, result, StartedAt);

    public CommandResult Fail(NodeErrorCode code, string message, bool retryable = false) =>
        CommandResult.Fail(Envelope.CommandId, NodeError.From(code, message, retryable), StartedAt);
}

/// <summary>
/// One verb. Handlers validate their own payload — the dispatcher has already established that the
/// command is allowed, fresh, in scope and not a duplicate, and knows nothing about what a valid
/// deploy spec looks like.
/// </summary>
public interface INodeCommandHandler
{
    /// <summary>The catalog name this handler implements.</summary>
    string Command { get; }

    Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct);
}
