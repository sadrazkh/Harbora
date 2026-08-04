using Harbora.NodeAgent.State;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Transport;

public sealed record OutboxEntry(long Sequence, string Json, DateTimeOffset QueuedAt);

public sealed record OutboxState
{
    public IReadOnlyList<OutboxEntry> Entries { get; init; } = [];
    public long LastSequence { get; init; }
}

/// <summary>
/// Frames the node has sent but the control plane has not acknowledged, held on disk.
///
/// <para>
/// On disk, not in memory, because the frame that matters most is the one that says a deploy
/// finished. If the agent restarts between finishing the work and the panel hearing about it, an
/// in-memory outbox loses the only record that the work happened — the panel retries, the
/// idempotency store says "already done", and the two sides disagree about what is running. A
/// durable outbox turns that into a replay after reconnect.
/// </para>
/// </summary>
public sealed class ChannelOutbox(JsonFileStore<OutboxState> store, ILogger<ChannelOutbox> log, int maxEntries = 500)
{
    private readonly Lock _gate = new();

    /// <summary>Queue a frame and return the sequence assigned to it.</summary>
    public long Append(Func<long, string> render)
    {
        lock (_gate)
        {
            var state = store.Load() ?? new OutboxState();
            var sequence = state.LastSequence + 1;

            var entries = state.Entries.ToList();
            entries.Add(new OutboxEntry(sequence, render(sequence), DateTimeOffset.UtcNow));

            if (entries.Count > maxEntries)
            {
                var dropped = entries.Count - maxEntries;
                // Never silent: an operator reading the journal must be able to tell that the panel
                // is missing results rather than that nothing happened.
                log.LogWarning(
                    "Outbox is full ({Max} frames); dropping the {Dropped} oldest unacknowledged frame(s). The control plane will not receive them.",
                    maxEntries, dropped);
                entries.RemoveRange(0, dropped);
            }

            store.Save(new OutboxState { Entries = entries, LastSequence = sequence });
            return sequence;
        }
    }

    /// <summary>Drop everything the control plane has confirmed it holds.</summary>
    public void AcknowledgeThrough(long sequence)
    {
        lock (_gate)
        {
            var state = store.Load();
            if (state is null || state.Entries.Count == 0) return;

            var remaining = state.Entries.Where(e => e.Sequence > sequence).ToList();
            if (remaining.Count == state.Entries.Count) return;

            store.Save(state with { Entries = remaining });
        }
    }

    /// <summary>Unacknowledged frames, oldest first — what a reconnect replays.</summary>
    public IReadOnlyList<OutboxEntry> Pending()
    {
        lock (_gate)
        {
            return (store.Load()?.Entries ?? []).OrderBy(e => e.Sequence).ToList();
        }
    }

    public long LastSequence
    {
        get { lock (_gate) { return store.Load()?.LastSequence ?? 0; } }
    }

    /// <summary>
    /// Forget everything. Used when the control plane rejects a resume: replaying frames into a
    /// session that has no memory of them would deliver results for commands it never issued.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            var last = store.Load()?.LastSequence ?? 0;
            store.Save(new OutboxState { Entries = [], LastSequence = last });
        }
    }
}
