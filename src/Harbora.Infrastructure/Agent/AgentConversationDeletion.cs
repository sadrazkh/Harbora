using System.Linq.Expressions;
using Harbora.Data;
using Harbora.Domain.Agent;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent;

/// <summary>Why <see cref="AgentConversationService.DeleteAsync"/> deleted nothing, or that it did.</summary>
public enum AgentDeleteStatus
{
    Deleted,

    /// <summary>No such conversation for this person in this workspace.</summary>
    NotFound,

    /// <summary>A turn is being answered in it, or one of its plans is executing.</summary>
    Busy
}

/// <summary>The outcome of deleting one conversation; the messages are set when it was refused.</summary>
public sealed record AgentDeleteOutcome(AgentDeleteStatus Status, string? MessageEn = null, string? MessageFa = null)
{
    public static readonly AgentDeleteOutcome Deleted = new(AgentDeleteStatus.Deleted);
}

/// <summary>
/// The one way conversations are deleted — one of them by its person, all of a person's, and the owner's
/// retention sweep — so that "busy" means the same in all three: a turn mark still younger than its lifetime
/// (as <c>GetAsync</c> judges <c>InProgress</c>), or a plan that is <see cref="AgentPlanStatus.Executing"/>.
/// A busy conversation is never deleted. Its messages and plans go with it (the foreign keys cascade); the
/// audit rows the assistant wrote and the day's usage counter are other tables and are not touched.
///
/// <para>
/// <b>The race that matters is a delete against an approval.</b> An approval claims its plan with one
/// conditional <c>UPDATE</c> on the plan's row (<c>AgentPlanService.TryMoveAsync</c>) and never touches the
/// conversation's, so a <c>DELETE … WHERE NOT EXISTS (an executing plan)</c> on its own is not enough under
/// read committed: the subquery reads the statement's snapshot, an approval can commit after it, and the
/// cascade — which reads a fresh one — then deletes a plan that is already running. So on a relational
/// provider each batch is one transaction that first locks every plan row of its conversations
/// (<c>SELECT … FOR UPDATE</c>, in id order so two deletes cannot deadlock), and only then deletes, with the
/// whole check in the <c>DELETE</c>'s own <c>WHERE</c>. An approval that got its claim in first is waited for
/// and then seen as executing, and the conversation is kept; one that comes second waits for the lock, finds
/// the row gone, and claims nothing ("not_found"). A message and a delete race on the conversation's own row,
/// which both statements write: the turn's claim either commits first — the delete then sees a live mark —
/// or finds no row to claim.
/// </para>
///
/// <para>
/// EF InMemory has no <c>ExecuteDeleteAsync</c> and no row locks: there it is a read-then-write that is not
/// atomic, as in <c>AgentPlanService.TryMoveAsync</c>, and the race is proven only in the Postgres lane
/// (<c>AgentConversationDeleteRaceTests</c>).
/// </para>
/// </summary>
internal static class AgentConversationDeletion
{
    /// <summary>How many conversations one transaction deletes, so a first retention pass over years of
    /// conversations is many short transactions rather than one long one.</summary>
    internal const int BatchSize = 500;

    /// <summary>
    /// Deletes those of <paramref name="ids"/> that, at the moment of the delete, still match
    /// <paramref name="eligible"/> and are not busy. Returns how many were deleted. Every read ignores the
    /// query filters and is pinned by <paramref name="eligible"/> instead: the retention sweep runs without a
    /// signed-in user, and a tenant filter there would read an empty table and report success.
    /// </summary>
    /// <param name="staleBefore">A turn mark at or before this is a dead process's, not a turn — the same
    /// cut <c>AgentConversationService.TryClaimTurnAsync</c> makes.</param>
    public static async Task<int> DeleteIdleAsync(
        HarboraDbContext db, IReadOnlyCollection<Guid> ids, Expression<Func<AgentConversation, bool>> eligible,
        DateTimeOffset staleBefore, CancellationToken ct)
    {
        var deleted = 0;
        foreach (var batch in ids.Distinct().Chunk(BatchSize))
            deleted += db.Database.IsRelational()
                ? await DeleteRelationalAsync(db, batch, eligible, staleBefore, ct)
                : await DeleteInMemoryAsync(db, batch, eligible, staleBefore, ct);
        return deleted;
    }

    /// <summary>Not busy: no live turn mark, and no plan executing.</summary>
    internal static Expression<Func<AgentConversation, bool>> Idle(HarboraDbContext db, DateTimeOffset staleBefore) =>
        c => (c.TurnStartedAt == null || c.TurnStartedAt <= staleBefore)
             && !db.AgentPlans.IgnoreQueryFilters().Any(p => p.ConversationId == c.Id && p.Status == AgentPlanStatus.Executing);

    /// <summary>
    /// Every plan row of these conversations, locked until the transaction ends — the approval's claim
    /// writes the same rows, so it waits for this or this waits for it. In id order, so two deletes over
    /// overlapping sets take their locks in the same order and cannot deadlock each other.
    /// </summary>
    internal static IQueryable<Guid> LockPlans(HarboraDbContext db, Guid[] conversationIds) =>
        db.Database.SqlQuery<Guid>(
            $"""SELECT "Id" AS "Value" FROM "AgentPlans" WHERE "ConversationId" = ANY({conversationIds}) ORDER BY "Id" FOR UPDATE""");

    private static async Task<int> DeleteRelationalAsync(
        HarboraDbContext db, Guid[] batch, Expression<Func<AgentConversation, bool>> eligible, DateTimeOffset staleBefore,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // The plans first, so no approval can move one of them between the check and the delete — see the
        // class doc. A row whose claim is in flight is waited for, and then read as it was committed.
        await LockPlans(db, batch).ToListAsync(ct);

        var deleted = await db.AgentConversations.IgnoreQueryFilters()
            .Where(c => batch.Contains(c.Id))
            .Where(eligible)
            .Where(Idle(db, staleBefore))
            .ExecuteDeleteAsync(ct);

        // On no token: the delete has been decided, and a commit abandoned half-way helps nobody.
        await transaction.CommitAsync(CancellationToken.None);
        return deleted;
    }

    private static async Task<int> DeleteInMemoryAsync(
        HarboraDbContext db, Guid[] batch, Expression<Func<AgentConversation, bool>> eligible, DateTimeOffset staleBefore,
        CancellationToken ct)
    {
        var doomed = await db.AgentConversations.IgnoreQueryFilters()
            .Where(c => batch.Contains(c.Id))
            .Where(eligible)
            .Where(Idle(db, staleBefore))
            .ToListAsync(ct);
        if (doomed.Count == 0) return 0;

        // No database here to cascade: the messages and plans are removed by hand.
        var doomedIds = doomed.Select(c => c.Id).ToList();
        db.AgentMessages.RemoveRange(await db.AgentMessages.IgnoreQueryFilters()
            .Where(m => doomedIds.Contains(m.ConversationId)).ToListAsync(ct));
        db.AgentPlans.RemoveRange(await db.AgentPlans.IgnoreQueryFilters()
            .Where(p => doomedIds.Contains(p.ConversationId)).ToListAsync(ct));
        db.AgentConversations.RemoveRange(doomed);
        await db.SaveChangesAsync(CancellationToken.None);
        return doomed.Count;
    }
}
