using System.Collections.Concurrent;
using Harbora.Application.Abstractions;
using Harbora.Domain.Ai;

namespace Harbora.Infrastructure.Ai;

/// <summary>
/// A request's place in the queue: it holds a concurrency slot until disposed, and it is where the
/// tokens that request cost are reported back.
/// </summary>
public sealed class AiRateSlot : IDisposable
{
    private readonly AiRateLimiter _limiter;
    private readonly Guid _workspaceId;
    private readonly DateTimeOffset _startedAt;
    private int _released;

    internal AiRateSlot(AiRateLimiter limiter, Guid workspaceId, DateTimeOffset startedAt)
    {
        _limiter = limiter;
        _workspaceId = workspaceId;
        _startedAt = startedAt;
    }

    /// <summary>
    /// Attaches what this request cost to the event counted when it started.
    ///
    /// Attached rather than added, because adding would count the request twice — once on the way in
    /// and once on the way out — and halve every caller's real allowance.
    /// </summary>
    public void Record(long tokens) => _limiter.RecordTokens(_workspaceId, _startedAt, tokens);

    public void Dispose()
    {
        // Exactly once. A double dispose would otherwise hand back a concurrency slot that was
        // never taken, and a caller who abandons requests would accumulate free ones.
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _limiter.Release(_workspaceId);
    }
}

/// <summary>
/// Counts what each tenant has sent, so <see cref="AiRateWindow"/> has something to decide against.
///
/// In memory, per process. That is a real limitation and worth stating plainly rather than hiding
/// behind an abstraction: run two control-plane instances and each enforces the plan's limits
/// separately, so a tenant can send twice the allowance. Harbora runs one control plane today; a
/// shared counter is what changes when that stops being true, and only this class changes.
///
/// It does not replace the period quotas in <see cref="AiPlanAccess.RefuseForQuota"/>. Those are
/// durable and survive a restart; these do not. A restart forgives the last minute of traffic, which
/// is the right trade for a limit whose job is smoothing bursts rather than counting money.
/// </summary>
public sealed class AiRateLimiter(ISystemClock clock)
{
    private sealed class Counters
    {
        public readonly List<RateEvent> Events = [];
        public int InFlight;
        public DateTimeOffset LastTouched;
    }

    private readonly ConcurrentDictionary<Guid, Counters> _byWorkspace = new();

    /// <summary>
    /// Workspaces untouched for longer than this are dropped. Without it, a control plane that has
    /// served ten thousand tenants once each holds ten thousand lists for ever.
    /// </summary>
    private static readonly TimeSpan Idle = TimeSpan.FromHours(2);

    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    /// <summary>
    /// Whether a request may start. When it may, it has been counted and the returned slot must be
    /// disposed — that is what releases the concurrency it took.
    /// </summary>
    public (RateDecision Decision, AiRateSlot? Slot) TryEnter(Guid workspaceId, AiPlan plan)
    {
        var now = clock.UtcNow;

        // Swept before this tenant's entry exists, not after. The other order sweeps the entry that
        // was just created — it has no requests and has never been touched, so it looks idle — and
        // the count is then kept on an object no longer in the dictionary. Every request would
        // start from an empty history and no limit would ever be reached.
        Sweep(now);

        var counters = _byWorkspace.GetOrAdd(workspaceId, _ => new Counters { LastTouched = now });

        lock (counters)
        {
            counters.LastTouched = now;

            // Pruned inside the lock and before deciding, so no decision is made against a list
            // another caller is halfway through rewriting.
            var kept = AiRateWindow.Prune(counters.Events, now);
            counters.Events.Clear();
            counters.Events.AddRange(kept);

            var decision = AiRateWindow.Refuse(plan, counters.Events, counters.InFlight, now);
            if (!decision.Allowed) return (decision, null);

            // Counted on the way in, not on the way out. A limiter that counts finished requests
            // lets a caller open a thousand at once: none has finished, so none is counted.
            counters.Events.Add(new RateEvent(now, 0));
            counters.InFlight++;

            return (decision, new AiRateSlot(this, workspaceId, now));
        }
    }

    /// <summary>How many requests this tenant currently has open. For tests and diagnostics.</summary>
    public int InFlight(Guid workspaceId) =>
        _byWorkspace.TryGetValue(workspaceId, out var counters) ? counters.InFlight : 0;

    /// <summary>Tokens counted in the last minute for this tenant. For tests and diagnostics.</summary>
    public long TokensInLastMinute(Guid workspaceId)
    {
        if (!_byWorkspace.TryGetValue(workspaceId, out var counters)) return 0;

        var since = clock.UtcNow - AiRateWindow.Minute;
        lock (counters) return counters.Events.Where(e => e.At > since).Sum(e => e.Tokens);
    }

    internal void RecordTokens(Guid workspaceId, DateTimeOffset startedAt, long tokens)
    {
        if (tokens <= 0) return;
        if (!_byWorkspace.TryGetValue(workspaceId, out var counters)) return;

        lock (counters)
        {
            var index = counters.Events.FindIndex(e => e.At == startedAt && e.Tokens == 0);

            // Missing means the window has already moved past it, or the tokens were reported twice.
            // Either way, appending here would count the request a second time.
            if (index >= 0) counters.Events[index] = counters.Events[index] with { Tokens = tokens };
        }
    }

    internal void Release(Guid workspaceId)
    {
        if (!_byWorkspace.TryGetValue(workspaceId, out var counters)) return;

        lock (counters)
        {
            if (counters.InFlight > 0) counters.InFlight--;
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        if (now - _lastSweep < Idle) return;
        _lastSweep = now;

        foreach (var (id, counters) in _byWorkspace)
        {
            lock (counters)
            {
                // A tenant with a request in flight is never swept: the slot is still held, and
                // dropping the entry would lose the release and leak a concurrency slot for ever.
                if (counters.InFlight > 0 || now - counters.LastTouched < Idle) continue;
            }

            _byWorkspace.TryRemove(id, out _);
        }
    }
}
