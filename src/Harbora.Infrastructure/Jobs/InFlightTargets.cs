using System.Collections.Concurrent;
using Harbora.Domain.Jobs;

namespace Harbora.Infrastructure.Jobs;

/// <summary>
/// The (kind, target) pairs this process is running right now, so a second job for the same target
/// is not claimed alongside the first.
///
/// <para>
/// Running jobs in parallel is only safe because of this. Ordering per target is a promise the rest
/// of the platform already relies on: two snapshots of one backup target must not write the same
/// repository at once, and a deployment and its redeploy must not race each other onto the same
/// proxy configuration. Oldest-first over one worker gave that ordering away for free; several
/// workers have to say it out loud.
/// </para>
///
/// <para>
/// Process-local, and deliberately so. It is not a distributed lock and does not pretend to be —
/// what stops a *second instance* running the same row is the <c>ClaimStamp</c> concurrency token on
/// the claim, which is unchanged. This only decides which rows this process's own claim is allowed
/// to look at.
/// </para>
/// </summary>
public sealed class InFlightTargets
{
    private readonly ConcurrentDictionary<(JobKind Kind, Guid TargetId), byte> _held = new();

    /// <summary>
    /// Takes the target if nothing here holds it. False means another job of this process already
    /// has it, and the caller must leave the row for later rather than run it.
    /// </summary>
    public bool TryReserve(JobKind kind, Guid targetId) => _held.TryAdd((kind, targetId), 0);

    /// <summary>Gives it back. Idempotent, so a release on a path that never reserved is harmless.</summary>
    public void Release(JobKind kind, Guid targetId) => _held.TryRemove((kind, targetId), out _);

    /// <summary>
    /// What is held, as of now. A copy: the claim builds a query from this while other jobs are
    /// finishing, and a pair released a moment later simply gets claimed on the next pass.
    /// </summary>
    public IReadOnlyCollection<(JobKind Kind, Guid TargetId)> Snapshot() => _held.Keys.ToArray();

    /// <summary>How many targets are held. Diagnostics and tests.</summary>
    public int Count => _held.Count;
}
