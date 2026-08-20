using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// <c>/users</c> (platform user administration) returned HTTP 500 for everyone, because
/// <c>UsersController.Index</c> built its "how many workspaces" column with
/// <c>.GroupBy(m =&gt; m.UserId).ToDictionaryAsync(g =&gt; g.Key, g =&gt; g.Count(), ct)</c> — grouping
/// straight into a dictionary rather than projecting the count inside the query first. EF InMemory (the
/// whole HTTP test lane in <c>Harbora.Tests</c>) evaluates that shape client-side without complaint, so
/// nothing in the InMemory-backed suite could ever fail on it — no test there renders <c>/users</c> at
/// all, and even one that did would not have caught this, which is the whole reason this file exists in
/// this lane instead. Only a real relational provider's query translator can be asked the question this
/// asks: does this exact query run against the schema PostgreSQL actually has.
///
/// <para>
/// <see cref="WorkspaceMembershipCounts.ByUserAsync"/> is the fixed shape, extracted out of the
/// controller specifically so this project (which does not reference <c>Harbora.Web</c> and cannot
/// construct the controller or drive it through <c>WebApplicationFactory</c>) can run the identical
/// production code path directly against real PostgreSQL, the way <c>EnvironmentPlacementReport</c> and
/// <c>VolumeOrphanReport</c> already are in this file's neighbours.
/// </para>
///
/// <para>
/// <b>Honesty about what this proves and what it does not:</b> local reproduction of the original,
/// unfixed shape (<c>.GroupBy(m =&gt; m.UserId).ToDictionaryAsync(g =&gt; g.Key, g =&gt; g.Count(), ct)</c>),
/// against the real <c>WorkspaceMember</c> model on a real SQL-translating relational provider
/// (SQLite — this machine has no Docker and no reachable PostgreSQL; see <c>PostgresFactAttribute</c>),
/// did <b>not</b> reproduce a translation exception: EF Core's own "stream the grouping, ordered by key"
/// support covers a bare <c>GroupBy(keySelector)</c> with no intervening <c>Select</c>, which is a
/// narrower and, on the evidence gathered here, better-supported case than the
/// <c>GroupBy(...).Select(g =&gt; ...)</c> pattern EF's own issue tracker documents as failing
/// (dotnet/efcore#30173 and neighbours — "Translation of 'Select' which contains grouping parameter
/// without composition is not supported"). That leaves real doubt about whether the original shape
/// actually threw against PostgreSQL specifically, which only this lane, run in CI where a Docker daemon
/// is reachable, can settle. Regardless of that doubt, the fixed shape below is strictly better — it
/// asks PostgreSQL to do the counting with an ordinary <c>GROUP BY</c> instead of shipping every
/// <c>WorkspaceMember</c> row to the client to be counted there — so it is what ships either way, and
/// this test is real, first-time coverage of it against the actual database engine the panel runs on.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class WorkspaceMembershipCountsPostgresTests(PostgresLane lane)
{
    [PostgresFact]
    public async Task A_freshly_migrated_empty_database_counts_nobody()
    {
        var connectionString = await lane.HeadSchemaAsync();
        await using var db = PostgresLane.Open(connectionString);

        var counts = await WorkspaceMembershipCounts.ByUserAsync(db);

        counts.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Each_user_is_counted_once_per_workspace_they_belong_to_over_real_postgres()
    {
        var connectionString = await lane.FreshlyMigratedAsync("membership-counts");

        Guid multiWorkspaceUserId, singleWorkspaceUserId;
        await using (var seed = PostgresLane.Open(connectionString))
        {
            var acme = new Workspace { Name = "acme", Slug = "acme" };
            var beta = new Workspace { Name = "beta", Slug = "beta" };
            var busy = new User
            {
                Email = "busy@example.com", DisplayName = "busy", PasswordHash = "x",
                Role = SystemRole.Admin, IsActive = true
            };
            var quiet = new User
            {
                Email = "quiet@example.com", DisplayName = "quiet", PasswordHash = "x",
                Role = SystemRole.Viewer, IsActive = true
            };
            seed.Workspaces.AddRange(acme, beta);
            seed.Users.AddRange(busy, quiet);
            // "busy" belongs to both workspaces; "quiet" belongs to only one — the shape that
            // distinguishes a correct per-user count from one that merely counts distinct users.
            seed.WorkspaceMembers.AddRange(
                new WorkspaceMember { WorkspaceId = acme.Id, UserId = busy.Id, Role = WorkspaceRole.Admin },
                new WorkspaceMember { WorkspaceId = beta.Id, UserId = busy.Id, Role = WorkspaceRole.Admin },
                new WorkspaceMember { WorkspaceId = acme.Id, UserId = quiet.Id, Role = WorkspaceRole.Viewer });
            await seed.SaveChangesAsync();
            multiWorkspaceUserId = busy.Id;
            singleWorkspaceUserId = quiet.Id;
        }

        await using var db = PostgresLane.Open(connectionString);
        var counts = await WorkspaceMembershipCounts.ByUserAsync(db);

        counts.Should().ContainKey(multiWorkspaceUserId).WhoseValue.Should().Be(2);
        counts.Should().ContainKey(singleWorkspaceUserId).WhoseValue.Should().Be(1);
    }

    [PostgresFact]
    public async Task Counting_changes_no_row_over_real_postgres()
    {
        var connectionString = await lane.FreshlyMigratedAsync("membership-counts-write-guard");

        Guid membershipId;
        await using (var seed = PostgresLane.Open(connectionString))
        {
            var acme = new Workspace { Name = "acme", Slug = "acme" };
            var user = new User
            {
                Email = "owner@example.com", DisplayName = "owner", PasswordHash = "x",
                Role = SystemRole.Owner, IsActive = true
            };
            var membership = new WorkspaceMember { WorkspaceId = acme.Id, UserId = user.Id, Role = WorkspaceRole.Admin };
            seed.Workspaces.Add(acme);
            seed.Users.Add(user);
            seed.WorkspaceMembers.Add(membership);
            await seed.SaveChangesAsync();
            membershipId = membership.Id;
        }

        await using (var runner = PostgresLane.Open(connectionString))
            await WorkspaceMembershipCounts.ByUserAsync(runner);

        await using var verify = PostgresLane.Open(connectionString);
        var stillThere = await verify.WorkspaceMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.Id == membershipId);
        stillThere.Should().BeTrue("counting must not have touched it");
    }
}
