using Harbora.Data;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Security;

/// <summary>
/// How many workspace memberships each user holds, across every workspace on the installation —
/// the platform Users list's "workspaces" column.
///
/// <para>
/// Extracted out of <c>UsersController.Index</c> so the exact query PostgreSQL has to run can be
/// exercised by <c>tests/Harbora.Postgres.Tests</c> directly, the way <c>EnvironmentPlacementReport</c>
/// and <c>VolumeOrphanReport</c> already are, without constructing the whole controller (that project
/// does not reference <c>Harbora.Web</c>).
/// </para>
///
/// <para>
/// The query used to read <c>.GroupBy(m =&gt; m.UserId).ToDictionaryAsync(g =&gt; g.Key, g =&gt; g.Count(), ct)</c>
/// — grouping straight into a dictionary without projecting the count inside the query first. That is
/// the shape EF Core's own issue tracker documents as fragile the moment anything downstream of a bare
/// <c>GroupBy</c> touches the grouping rather than an aggregate already computed inside the query (see
/// dotnet/efcore#30173 and its neighbours: "Translation of 'Select' which contains grouping parameter
/// without composition is not supported"). Projecting the count inside the query —
/// <c>Select(g =&gt; new { g.Key, Count = g.Count() })</c> — turns this into an ordinary SQL
/// <c>GROUP BY</c> that every relational provider translates the same, well-trodden way, rather than
/// leaning on GroupBy's "stream the whole grouping to the client" code path, which is what a bare
/// <c>GroupBy(...).ToDictionaryAsync(...)</c> depends on and is a needless, easy-to-regress way to ask
/// PostgreSQL to hand back one row per <c>WorkspaceMember</c> instead of one row per user.
/// </para>
/// </summary>
public static class WorkspaceMembershipCounts
{
    /// <summary>
    /// <c>IgnoreQueryFilters()</c> because this is a platform-wide read (the Users list), the same
    /// reasoning <c>UsersController.Index</c> already documents for the sibling queries beside it.
    /// </summary>
    public static async Task<Dictionary<Guid, int>> ByUserAsync(HarboraDbContext db, CancellationToken ct = default) =>
        await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(m => m.UserId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
}
