using System.Collections.Concurrent;
using System.Diagnostics;
using Harbora.NodeAgent.Auditing;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Commands;

/// <summary>
/// The gate every instruction from the control plane passes through.
///
/// <para>
/// Admission runs in a fixed order and the first failure is the answer: allowlist, freshness and
/// nonce, scope, drain, idempotency — and only then is the payload handed to a handler. The order
/// is the security property. Nothing parses a payload it has not already decided it is willing to
/// act on, so a malformed or hostile payload never reaches code that could be confused by it.
/// </para>
/// </summary>
public sealed class CommandDispatcher
{
    private readonly Dictionary<string, INodeCommandHandler> _handlers;
    private readonly CommandLedger _ledger;
    private readonly NodeAuditLog _audit;
    private readonly JsonFileStore<NodeState> _state;
    private readonly TimeProvider _clock;
    private readonly ILogger<CommandDispatcher> _log;
    private readonly SemaphoreSlim _concurrency;

    private readonly ConcurrentDictionary<string, InFlightCommand> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// A running command and why it might stop. The flag exists because the token alone cannot say:
    /// a timeout and an operator's cancel both surface as the same <see cref="OperationCanceledException"/>
    /// on the same linked token, and reporting one as the other would tell the control plane to
    /// retry something a human deliberately stopped.
    /// </summary>
    private sealed class InFlightCommand(CancellationTokenSource cts)
    {
        public CancellationTokenSource Source { get; } = cts;
        public volatile bool ExplicitlyCancelled;
    }

    public CommandDispatcher(
        IEnumerable<INodeCommandHandler> handlers,
        CommandLedger ledger,
        NodeAuditLog audit,
        JsonFileStore<NodeState> state,
        IOptions<NodeAgentOptions> options,
        TimeProvider clock,
        ILogger<CommandDispatcher> log)
    {
        _handlers = handlers.ToDictionary(h => h.Command, StringComparer.Ordinal);
        _ledger = ledger;
        _audit = audit;
        _state = state;
        _clock = clock;
        _log = log;
        _concurrency = new SemaphoreSlim(options.Value.MaxConcurrentCommands);
    }

    /// <summary>Commands this build implements — a subset of the catalog, reported in capabilities.</summary>
    public IReadOnlyCollection<string> ImplementedCommands => _handlers.Keys;

    public int InFlightCount => _inFlight.Count;

    /// <summary>
    /// Run one command to completion, emitting exactly one ack and at most one result.
    /// Never throws: a handler that faults produces a failed result, because the control plane
    /// waiting forever for a command that died is worse than being told it failed.
    /// </summary>
    public async Task ExecuteAsync(CommandEnvelope envelope, ICommandResponder responder, CancellationToken ct)
    {
        var startedAt = _clock.GetUtcNow();

        var rejection = Admit(envelope);
        if (rejection is not null)
        {
            _audit.CommandReceived(envelope, "rejected", rejection);
            _log.LogWarning(
                "Refused {Command} {CommandId}: {Code} — {Message}",
                envelope.Command, envelope.CommandId, rejection.Code, rejection.Message);

            await responder.AckAsync(
                new CommandAck { CommandId = envelope.CommandId, Rejected = rejection },
                envelope.CorrelationId, ct);

            await responder.ResultAsync(
                new CommandResult
                {
                    CommandId = envelope.CommandId,
                    Status = CommandStatus.Rejected,
                    Error = rejection,
                    StartedAt = startedAt,
                    CompletedAt = _clock.GetUtcNow(),
                },
                envelope.CorrelationId, ct);
            return;
        }

        if (_ledger.FindCompleted(envelope.IdempotencyKey) is { } previous)
        {
            _audit.CommandReceived(envelope, "deduplicated");
            _log.LogInformation(
                "{Command} {CommandId} matches idempotency key {Key} already completed by {PreviousId}; replaying its result.",
                envelope.Command, envelope.CommandId, envelope.IdempotencyKey, previous.CommandId);

            await responder.AckAsync(
                new CommandAck { CommandId = envelope.CommandId, Deduplicated = true },
                envelope.CorrelationId, ct);

            await responder.ResultAsync(
                previous.Result with { CommandId = envelope.CommandId, IdempotentReplay = true },
                envelope.CorrelationId, ct);
            return;
        }

        await responder.AckAsync(new CommandAck { CommandId = envelope.CommandId }, envelope.CorrelationId, ct);
        _audit.CommandReceived(envelope, "accepted");

        var result = await RunAsync(envelope, responder, startedAt, ct);

        // Only durable outcomes are memorable. A cancelled or timed-out command left the node in an
        // unknown state, and replaying "it was cancelled" to a retry would refuse to do the work
        // for as long as the retention window lasts.
        if (result.Status is CommandStatus.Succeeded or CommandStatus.Failed)
            _ledger.RecordCompleted(envelope, result);

        _audit.CommandCompleted(envelope, result, (long)(result.CompletedAt - result.StartedAt).TotalMilliseconds);

        await responder.ResultAsync(result, envelope.CorrelationId, ct);
    }

    /// <summary>Ask an in-flight command to stop. Best effort; some steps cannot be interrupted.</summary>
    public bool Cancel(string commandId, string? reason)
    {
        if (!_inFlight.TryGetValue(commandId, out var running)) return false;

        _log.LogInformation("Cancelling command {CommandId}{Reason}.", commandId,
            reason is null ? string.Empty : $" ({reason})");

        running.ExplicitlyCancelled = true;

        try
        {
            running.Source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // It finished between the lookup and the cancel. Nothing to do.
            return false;
        }
    }

    /// <summary>
    /// Every admission check, in order. Pure apart from the ledger's nonce write, so the whole
    /// policy can be exercised in a test without running a command.
    /// </summary>
    internal NodeError? Admit(CommandEnvelope envelope)
    {
        if (!NodeCommandCatalog.TryGet(envelope.Command, out var descriptor))
            return NodeError.From(NodeErrorCode.UnknownCommand, $"'{envelope.Command}' is not a command this node accepts.");

        if (!_handlers.ContainsKey(envelope.Command))
            return NodeError.From(
                NodeErrorCode.CommandNotSupported,
                $"'{envelope.Command}' is in the contract but not implemented by agent {AgentVersion.Current}.");

        if (_ledger.AdmitEnvelope(envelope) is { } replay) return replay;

        if (!string.Equals(envelope.RequiredScope, descriptor.RequiredScope, StringComparison.Ordinal))
            return NodeError.From(
                NodeErrorCode.Unauthorized,
                $"'{envelope.Command}' requires scope '{descriptor.RequiredScope}'; the command declared '{envelope.RequiredScope}'.");

        var state = _state.Load() ?? new NodeState();

        if (!state.HasScope(descriptor.RequiredScope))
            return NodeError.From(
                NodeErrorCode.Unauthorized,
                $"This node was not enrolled with scope '{descriptor.RequiredScope}'.");

        if (state.Draining && NodeCommandCatalog.RejectedWhileDraining(envelope.Command))
            return NodeError.From(
                NodeErrorCode.NodeDraining,
                $"This node is draining{(state.DrainReason is null ? string.Empty : $" ({state.DrainReason})")} and is not accepting new work.",
                retryable: true);

        if (!AgentVersion.IsAtLeast(AgentVersion.Current, state.MinimumAgentVersion))
            return NodeError.From(
                NodeErrorCode.AgentTooOld,
                $"Agent {AgentVersion.Current} is below the minimum supported version {state.MinimumAgentVersion}. Update this node.");

        return null;
    }

    private async Task<CommandResult> RunAsync(
        CommandEnvelope envelope, ICommandResponder responder, DateTimeOffset startedAt, CancellationToken ct)
    {
        NodeCommandCatalog.TryGet(envelope.Command, out var descriptor);

        var timeout = ResolveTimeout(envelope, descriptor);
        var handler = _handlers[envelope.Command];
        var context = new CommandContext(envelope, responder, startedAt);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var running = new InFlightCommand(cts);

        if (!_inFlight.TryAdd(envelope.CommandId, running))
            return CommandResult.Fail(
                envelope.CommandId,
                NodeError.From(NodeErrorCode.ValidationFailed, $"Command {envelope.CommandId} is already running."),
                startedAt);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _concurrency.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _inFlight.TryRemove(envelope.CommandId, out _);
            return Interrupted(envelope, startedAt, ct, timeout, running);
        }

        try
        {
            using (_log.BeginScope(new Dictionary<string, object?>
            {
                ["commandId"] = envelope.CommandId,
                ["correlationId"] = envelope.CorrelationId,
                ["command"] = envelope.Command,
                ["tenantId"] = envelope.Audit?.TenantId,
            }))
            {
                _log.LogInformation("Running {Command} (timeout {Timeout}s).", envelope.Command, timeout.TotalSeconds);
                return await handler.HandleAsync(context, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return Interrupted(envelope, startedAt, ct, timeout, running);
        }
        catch (Exception e)
        {
            // The message is redacted on its way to the log and to the control plane, but the
            // stack trace stays local: it names paths and internals the panel has no use for.
            _log.LogError(e, "{Command} {CommandId} faulted.", envelope.Command, envelope.CommandId);

            return CommandResult.Fail(
                envelope.CommandId,
                NodeError.From(NodeErrorCode.Internal, $"{envelope.Command} failed: {e.Message}"),
                startedAt);
        }
        finally
        {
            stopwatch.Stop();
            _concurrency.Release();
            _inFlight.TryRemove(envelope.CommandId, out _);
        }
    }

    private CommandResult Interrupted(
        CommandEnvelope envelope, DateTimeOffset startedAt, CancellationToken outer, TimeSpan timeout,
        InFlightCommand running)
    {
        // Distinguishing the two matters to the caller: a timeout may be worth retrying with a
        // longer bound, a cancellation was someone's decision and should not be retried at all.
        // The agent shutting down counts as a cancellation for the same reason.
        var timedOut = !running.ExplicitlyCancelled && !outer.IsCancellationRequested;

        return new CommandResult
        {
            CommandId = envelope.CommandId,
            Status = timedOut ? CommandStatus.TimedOut : CommandStatus.Cancelled,
            Error = timedOut
                ? NodeError.From(NodeErrorCode.Timeout, $"{envelope.Command} exceeded its {timeout.TotalSeconds:0}s timeout.", retryable: true)
                : NodeError.From(NodeErrorCode.Cancelled, $"{envelope.Command} was cancelled."),
            StartedAt = startedAt,
            CompletedAt = _clock.GetUtcNow(),
        };
    }

    /// <summary>
    /// The envelope may shorten the catalog's timeout but not extend it past a day. A control
    /// plane asking for an unbounded deploy would pin a slot in the concurrency limiter forever.
    /// </summary>
    private static TimeSpan ResolveTimeout(CommandEnvelope envelope, NodeCommandDescriptor descriptor)
    {
        var seconds = envelope.TimeoutSeconds is > 0 and <= 86_400
            ? envelope.TimeoutSeconds.Value
            : descriptor.DefaultTimeoutSeconds;

        return TimeSpan.FromSeconds(seconds);
    }
}
