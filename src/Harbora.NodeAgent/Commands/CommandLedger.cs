using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Commands;

public sealed record SeenNonce(string Nonce, DateTimeOffset SeenAt);

public sealed record CompletedCommand(
    string IdempotencyKey,
    string CommandId,
    string Command,
    CommandResult Result,
    DateTimeOffset CompletedAt);

public sealed record CommandLedgerState
{
    public IReadOnlyList<SeenNonce> Nonces { get; init; } = [];
    public IReadOnlyList<CompletedCommand> Completed { get; init; } = [];
}

/// <summary>
/// The node's memory of what it has been asked to do: which envelopes it has already seen, and
/// what each completed command produced.
///
/// <para>
/// Both live on disk and in one file, written atomically together. That is deliberate — a replay
/// check that survives a restart but an idempotency record that does not would let a redelivered
/// deploy execute a second time on exactly the reboot where the operator is least watching.
/// </para>
/// </summary>
public sealed class CommandLedger(
    JsonFileStore<CommandLedgerState> store,
    IOptions<NodeAgentOptions> options,
    TimeProvider clock,
    ILogger<CommandLedger> log)
{
    private readonly Lock _gate = new();
    private readonly NodeAgentOptions _options = options.Value;

    /// <summary>
    /// Nonces are kept for twice the freshness window. Once an envelope is too old to be accepted
    /// on its timestamp alone, remembering its nonce adds nothing.
    /// </summary>
    private TimeSpan NonceRetention => NodeContract.CommandFreshnessWindow * 2;

    private TimeSpan CompletionRetention => TimeSpan.FromHours(_options.IdempotencyRetentionHours);

    /// <summary>
    /// Admit an envelope, or say why not. Checks freshness before uniqueness: an envelope from last
    /// week is stale whether or not its nonce happens to be new, and saying so is the more useful
    /// answer.
    /// </summary>
    public NodeError? AdmitEnvelope(CommandEnvelope envelope)
    {
        var now = clock.GetUtcNow();
        var age = now - envelope.IssuedAt;

        if (age > NodeContract.CommandFreshnessWindow)
            return NodeError.From(
                NodeErrorCode.ReplayRejected,
                $"Command was issued {age.TotalSeconds:0}s ago, outside the {NodeContract.CommandFreshnessWindow.TotalSeconds:0}s freshness window.");

        if (age < -NodeContract.CommandFreshnessWindow)
            return NodeError.From(
                NodeErrorCode.ReplayRejected,
                $"Command is dated {(-age).TotalSeconds:0}s in the future; check the clock on the control plane or this node.");

        lock (_gate)
        {
            var state = store.Load() ?? new CommandLedgerState();

            if (state.Nonces.Any(n => n.Nonce == envelope.Nonce))
                return NodeError.From(NodeErrorCode.ReplayRejected, "This command envelope has already been seen.");

            var nonces = state.Nonces
                .Where(n => now - n.SeenAt < NonceRetention)
                .Append(new SeenNonce(envelope.Nonce, now))
                .ToList();

            store.Save(state with { Nonces = nonces });
        }

        return null;
    }

    /// <summary>The stored outcome for an idempotency key, or null when this is genuinely new work.</summary>
    public CompletedCommand? FindCompleted(string idempotencyKey)
    {
        lock (_gate)
        {
            var completed = (store.Load()?.Completed ?? [])
                .FirstOrDefault(c => c.IdempotencyKey == idempotencyKey);

            if (completed is null) return null;

            if (clock.GetUtcNow() - completed.CompletedAt > CompletionRetention) return null;

            return completed;
        }
    }

    /// <summary>Remember an outcome so a redelivery replays it rather than repeating the work.</summary>
    public void RecordCompleted(CommandEnvelope envelope, CommandResult result)
    {
        lock (_gate)
        {
            var state = store.Load() ?? new CommandLedgerState();
            var now = clock.GetUtcNow();

            var completed = state.Completed
                .Where(c => c.IdempotencyKey != envelope.IdempotencyKey)
                .Where(c => now - c.CompletedAt < CompletionRetention)
                .Append(new CompletedCommand(envelope.IdempotencyKey, envelope.CommandId, envelope.Command, result, now))
                .ToList();

            store.Save(state with { Completed = completed });
        }
    }

    /// <summary>Drop expired nonces and outcomes. Called on a timer and at startup.</summary>
    public void Sweep()
    {
        lock (_gate)
        {
            var state = store.Load();
            if (state is null) return;

            var now = clock.GetUtcNow();

            var nonces = state.Nonces.Where(n => now - n.SeenAt < NonceRetention).ToList();
            var completed = state.Completed.Where(c => now - c.CompletedAt < CompletionRetention).ToList();

            if (nonces.Count == state.Nonces.Count && completed.Count == state.Completed.Count) return;

            log.LogDebug(
                "Swept the command ledger: {Nonces} nonce(s) and {Completed} completion(s) expired.",
                state.Nonces.Count - nonces.Count, state.Completed.Count - completed.Count);

            store.Save(new CommandLedgerState { Nonces = nonces, Completed = completed });
        }
    }
}
