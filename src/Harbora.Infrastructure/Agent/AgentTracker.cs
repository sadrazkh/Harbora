using Harbora.Domain.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// The change tracker the agent shares with its tools.
///
/// <para>
/// Every tool runs on the request's one scoped <c>HarboraDbContext</c>, and the agent saves its own rows
/// (messages, the plan, the audit entry) through the same context right after each tool returns. So a
/// tool that leaves an unsaved change behind — a validation that added a row it should not have, a step
/// whose save threw on a unique-slug race and left its entity <c>Added</c> — has that change written,
/// or retried and failed again, by the agent's next save. These helpers are how the agent keeps what a
/// tool left behind out of its own writes. "Agent rows" are the types in <see cref="Harbora.Domain.Agent"/>;
/// everything else in the tracker belongs to some tool.
/// </para>
/// </summary>
internal static class AgentTracker
{
    private static readonly string? AgentNamespace = typeof(AgentPlan).Namespace;

    private static bool IsAgentRow(EntityEntry entry) => entry.Metadata.ClrType.Namespace == AgentNamespace;

    private static bool IsChange(EntityEntry entry) =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted;

    /// <summary>The entities, not the agent's own, that have unsaved changes right now.</summary>
    public static HashSet<object> ForeignChanges(DbContext db) =>
        db.ChangeTracker.Entries()
            .Where(e => IsChange(e) && !IsAgentRow(e))
            .Select(e => e.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Detaches every unsaved change that is not an agent row, except those already pending in
    /// <paramref name="before"/>. Returns the type names it dropped, for the log line that says so.
    /// </summary>
    public static IReadOnlyList<string> DetachForeignChanges(DbContext db, IReadOnlySet<object>? before = null)
    {
        var dropped = new List<string>();
        foreach (var entry in db.ChangeTracker.Entries().Where(e => IsChange(e) && !IsAgentRow(e)).ToList())
        {
            if (before is not null && before.Contains(entry.Entity)) continue;
            dropped.Add(entry.Metadata.ClrType.Name);
            entry.State = EntityState.Detached;
        }
        return dropped;
    }
}
