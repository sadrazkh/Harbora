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
/// When a conversation counts as busy, and so is not deleted: a turn mark younger than its lifetime (as
/// <c>GetAsync</c> judges <c>InProgress</c>), or a plan executing whose claim is younger than
/// <see cref="AgentConversationDeletion.ExecutingPlanAbandonedAfter"/>.
/// </summary>
/// <param name="TurnMarkBefore">A turn mark at or before this was left by a process that died — the same cut
/// <c>AgentConversationService.TryClaimTurnAsync</c> makes.</param>
/// <param name="ClaimBefore">A plan claimed at or before this and still executing is a run that broke while the
/// process lived on; it no longer keeps its conversation.</param>
internal sealed record AgentBusyCut(DateTimeOffset TurnMarkBefore, DateTimeOffset ClaimBefore)
{
    public static AgentBusyCut At(DateTimeOffset now, TimeSpan turnMarkLifetime) =>
        new(now - turnMarkLifetime, now - AgentConversationDeletion.ExecutingPlanAbandonedAfter);

    /// <summary>Whether a turn mark means a turn is being answered: one younger than its lifetime. What
    /// <c>InProgress</c> reports and a refused delete names — the turn half of <see cref="AgentConversationDeletion.Idle"/>,
    /// over a mark already read.</summary>
    public bool TurnLive(DateTimeOffset? turnStartedAt) => turnStartedAt is { } started && started > TurnMarkBefore;
}

/// <summary>How many conversations a deletion has committed so far — readable after it threw, because the
/// batches before the one that failed stay deleted and have to be reported.</summary>
internal sealed class AgentDeletionTally
{
    public int Deleted { get; set; }
}

/// <summary>
/// The one way conversations are deleted — one of them by its person, all of a person's, and the owner's
/// retention sweep — so that "busy" means the same in all three (<see cref="AgentBusyCut"/>). A busy
/// conversation is never deleted. Its messages and plans go with it (the foreign keys cascade); the audit rows
/// the assistant wrote and the day's usage counter are other tables and are not touched.
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
/// <b>An executing plan does not block for ever.</b> One a restart interrupted is failed at startup
/// (<see cref="AgentPlanStartupReconciler"/>); one whose run broke while the process lived on stops counting
/// once its claim is older than <see cref="ExecutingPlanAbandonedAfter"/>. Should such a run still be going
/// after all, its end finds the plan gone and writes nothing (<c>AgentPlanService.FinishAsync</c>).
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
    /// How long a plan's claim keeps its conversation from being deleted. Far beyond any real run: a plan's
    /// steps are calls into the panel's own services that each return in seconds — a deployment is queued,
    /// not waited for.
    /// </summary>
    public static readonly TimeSpan ExecutingPlanAbandonedAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Deletes those of <paramref name="ids"/> that, at the moment of the delete, still match
    /// <paramref name="eligible"/> and are not busy. Returns how many were deleted, and keeps
    /// <paramref name="tally"/> up to date batch by batch, so a caller whose later batch failed can still say
    /// what the earlier ones did. Every read ignores the query filters and is pinned by
    /// <paramref name="eligible"/> instead: the retention sweep runs without a signed-in user, and a tenant
    /// filter there would read an empty table and report success.
    /// </summary>
    public static async Task<int> DeleteIdleAsync(
        HarboraDbContext db, IReadOnlyCollection<Guid> ids, Expression<Func<AgentConversation, bool>> eligible,
        AgentBusyCut cut, AgentDeletionTally? tally, CancellationToken ct)
    {
        tally ??= new AgentDeletionTally();
        foreach (var batch in ids.Distinct().Chunk(BatchSize))
            tally.Deleted += db.Database.IsRelational()
                ? await DeleteRelationalAsync(db, batch, eligible, cut, ct)
                : await DeleteInMemoryAsync(db, batch, eligible, cut, ct);
        return tally.Deleted;
    }

    /// <summary>Not busy: no live turn mark, and none of its plans <see cref="Running"/>.</summary>
    internal static Expression<Func<AgentConversation, bool>> Idle(HarboraDbContext db, AgentBusyCut cut)
    {
        // Inlined into the subquery by EF, so the plan half is this one rule in SQL as well.
        var running = Running(cut);
        return c => (c.TurnStartedAt == null || c.TurnStartedAt <= cut.TurnMarkBefore)
                    && !db.AgentPlans.IgnoreQueryFilters().Where(running).Any(p => p.ConversationId == c.Id);
    }

    /// <summary>A plan that keeps its conversation busy: executing under a claim younger than the cut. The plan
    /// half of <see cref="Idle"/>, and what a refused delete names as its reason.</summary>
    internal static Expression<Func<AgentPlan, bool>> Running(AgentBusyCut cut) =>
        p => p.Status == AgentPlanStatus.Executing && (p.DecidedAt ?? p.UpdatedAt) > cut.ClaimBefore;

    /// <summary>
    /// Every plan row of these conversations, locked until the transaction ends — the approval's claim
    /// writes the same rows, so it waits for this or this waits for it. In id order, so two deletes over
    /// overlapping sets take their locks in the same order and cannot deadlock each other.
    /// </summary>
    internal static IQueryable<Guid> LockPlans(HarboraDbContext db, Guid[] conversationIds) =>
        db.Database.SqlQuery<Guid>(
            $"""SELECT "Id" AS "Value" FROM "AgentPlans" WHERE "ConversationId" = ANY({conversationIds}) ORDER BY "Id" FOR UPDATE""");

    private static async Task<int> DeleteRelationalAsync(
        HarboraDbContext db, Guid[] batch, Expression<Func<AgentConversation, bool>> eligible, AgentBusyCut cut,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // The plans first, so no approval can move one of them between the check and the delete — see the
        // class doc. A row whose claim is in flight is waited for, and then read as it was committed.
        await LockPlans(db, batch).ToListAsync(ct);

        var deleted = await db.AgentConversations.IgnoreQueryFilters()
            .Where(c => batch.Contains(c.Id))
            .Where(eligible)
            .Where(Idle(db, cut))
            .ExecuteDeleteAsync(ct);

        // On no token: the delete has been decided, and a commit abandoned half-way helps nobody.
        await transaction.CommitAsync(CancellationToken.None);
        return deleted;
    }

    private static async Task<int> DeleteInMemoryAsync(
        HarboraDbContext db, Guid[] batch, Expression<Func<AgentConversation, bool>> eligible, AgentBusyCut cut,
        CancellationToken ct)
    {
        var doomed = await db.AgentConversations.IgnoreQueryFilters()
            .Where(c => batch.Contains(c.Id))
            .Where(eligible)
            .Where(Idle(db, cut))
            .ToListAsync(ct);
        if (doomed.Count == 0) return 0;

        // No database here to cascade: the messages and plans are removed by hand.
        var doomedIds = doomed.Select(c => c.Id).ToList();
        db.AgentMessages.RemoveRange(await db.AgentMessages.IgnoreQueryFilters()
            .Where(m => doomedIds.Contains(m.ConversationId)).ToListAsync(ct));
        db.AgentPlans.RemoveRange(await db.AgentPlans.IgnoreQueryFilters()
            .Where(p => doomedIds.Contains(p.ConversationId)).ToListAsync(ct));
        db.AgentConversations.RemoveRange(doomed);
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            // A batch that did not save is not deleted, and the next save on this context — the audit row
            // reporting the batches before it — must not try it again.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Deleted).ToList())
                entry.State = EntityState.Detached;
            throw;
        }
        return doomed.Count;
    }
}
