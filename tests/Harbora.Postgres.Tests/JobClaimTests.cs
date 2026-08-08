using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// The claim, against the provider that has to run it.
///
/// <para>
/// <c>JobClaimQuery</c> exists as its own class because every term in it has to be translatable by
/// Npgsql and nothing in the C# says so. The fast suite hands it a real Npgsql provider and reads
/// the SQL it produces, with no database in the room — which catches an untranslatable term. What it
/// cannot catch is a term that translates and then <i>means something else</i>: <c>COALESCE</c>
/// against a null column, a <c>&lt;&gt;</c> that EF renders with three-valued logic, a timestamp
/// comparison across time zones. Those need rows.
/// </para>
///
/// <para>
/// And the concurrency token needs two workers. <c>JobWorker</c> claims by writing
/// <c>ClaimStamp + 1</c> and letting the loser take a <c>DbUpdateConcurrencyException</c> — the only
/// thing that keeps two panel processes from running one deployment twice, and something no
/// in-memory provider can demonstrate.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class JobClaimTests(PostgresLane lane)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly (JobKind Kind, Guid ExcludesOn)[] NothingInFlight = [];

    [PostgresFact]
    public async Task Two_claimers_racing_for_one_job_leave_exactly_one_winner()
    {
        var connectionString = await lane.FreshlyMigratedAsync("claim_race");
        var target = Guid.NewGuid();
        await EnqueueAsync(connectionString, target);

        // Two contexts, two connections, one row — as close to two panel processes as one test can
        // get without starting one.
        await using var first = PostgresLane.Open(connectionString);
        await using var second = PostgresLane.Open(connectionString);

        var byFirst = await Claimable(first).FirstAsync();
        var bySecond = await Claimable(second).FirstAsync();
        byFirst.Id.Should().Be(bySecond.Id, "both read the same oldest Pending row, which is the race");

        Take(byFirst, "worker-one");
        Take(bySecond, "worker-two");

        await first.SaveChangesAsync();

        await second.Awaiting(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>(
            "the ClaimStamp in the WHERE clause no longer matches");

        await using var reader = PostgresLane.Open(connectionString);
        var settled = await reader.Jobs.AsNoTracking().SingleAsync();

        settled.ClaimedBy.Should().Be("worker-one");
        settled.ClaimStamp.Should().Be(1, "one increment, not two");
        settled.Attempts.Should().Be(1, "the loser's attempt was never recorded either");
        settled.Status.Should().Be(JobStatus.Running);
    }

    [PostgresFact]
    public async Task The_loser_of_the_race_finds_nothing_left_to_claim()
    {
        // What the worker does next matters as much as the exception: it goes round the loop, and
        // the row it lost must no longer be Pending or it would try again forever.
        var connectionString = await lane.FreshlyMigratedAsync("claim_loser");
        await EnqueueAsync(connectionString, Guid.NewGuid());

        await using var winner = PostgresLane.Open(connectionString);
        Take(await Claimable(winner).FirstAsync(), "worker-one");
        await winner.SaveChangesAsync();

        await using var loser = PostgresLane.Open(connectionString);
        (await Claimable(loser).FirstOrDefaultAsync()).Should().BeNull();
    }

    [PostgresFact]
    public async Task A_job_serving_a_backoff_is_left_alone_until_it_is_due()
    {
        var connectionString = await lane.FreshlyMigratedAsync("claim_backoff");

        var backingOff = Enqueue(Guid.NewGuid());
        backingOff.NextAttemptAt = Now.AddMinutes(5);

        var due = Enqueue(Guid.NewGuid());
        due.NextAttemptAt = Now.AddMinutes(-5);
        due.CreatedAt = Now.AddMinutes(-1);

        await using (var seed = PostgresLane.Open(connectionString))
        {
            seed.Jobs.AddRange(backingOff, due);
            await seed.SaveChangesAsync();
        }

        await using var db = PostgresLane.Open(connectionString);

        // Oldest first would otherwise have picked the backing-off row: it was queued earlier.
        var claimable = await Claimable(db).Select(j => j.Id).ToListAsync();
        claimable.Should().Equal(due.Id);

        // And it becomes claimable the moment it falls due, with nothing having written to it.
        var later = await JobClaimQuery.Claimable(db.Jobs, Now.AddMinutes(10), NothingInFlight)
            .Select(j => j.Id).ToListAsync();
        later.Should().Contain(backingOff.Id);
    }

    [PostgresFact]
    public async Task A_null_backoff_means_claimable_now()
    {
        // NextAttemptAt is null on every row an upgrade carried across, so "null is due" is not an
        // edge case — it is every job on the platform the first time this runs.
        var connectionString = await lane.FreshlyMigratedAsync("claim_null_backoff");
        await EnqueueAsync(connectionString, Guid.NewGuid());

        await using var db = PostgresLane.Open(connectionString);

        (await Claimable(db).CountAsync()).Should().Be(1);
    }

    [PostgresFact]
    public async Task What_a_running_job_holds_is_removed_from_the_search_rather_than_skipped_over()
    {
        // The COALESCE branch, in the database. A deployment in flight holds its app; the older
        // queued deployment of the same app must drop out of the search entirely, so oldest-first
        // goes on to the next row that can run instead of the worker idling behind work it may not
        // start.
        var connectionString = await lane.FreshlyMigratedAsync("claim_exclusion");
        var appBeingDeployed = Guid.NewGuid();

        var heldBack = Enqueue(Guid.NewGuid());
        heldBack.ExclusiveWith = appBeingDeployed;
        heldBack.CreatedAt = Now.AddMinutes(-10);

        var free = Enqueue(Guid.NewGuid());
        free.ExclusiveWith = Guid.NewGuid();
        free.CreatedAt = Now.AddMinutes(-5);

        await using (var seed = PostgresLane.Open(connectionString))
        {
            seed.Jobs.AddRange(heldBack, free);
            await seed.SaveChangesAsync();
        }

        await using var db = PostgresLane.Open(connectionString);

        var claimable = await JobClaimQuery
            .Claimable(db.Jobs, Now, [(JobKind.Deployment, appBeingDeployed)])
            .Select(j => j.Id).ToListAsync();

        claimable.Should().Equal(free.Id);
    }

    [PostgresFact]
    public async Task A_job_with_no_key_of_its_own_excludes_on_its_target()
    {
        // The other half of the coalesce: null falls back to TargetId. Getting this wrong in SQL —
        // by comparing the null column directly, say — would let a second backup of one target run.
        var connectionString = await lane.FreshlyMigratedAsync("claim_fallback");
        var target = Guid.NewGuid();

        var backup = Enqueue(target);
        backup.Kind = JobKind.Backup;
        backup.ExclusiveWith.Should().BeNull();

        await using (var seed = PostgresLane.Open(connectionString))
        {
            seed.Jobs.Add(backup);
            await seed.SaveChangesAsync();
        }

        await using var db = PostgresLane.Open(connectionString);

        var whileItRuns = await JobClaimQuery
            .Claimable(db.Jobs, Now, [(JobKind.Backup, target)]).ToListAsync();
        whileItRuns.Should().BeEmpty();

        // A different kind holding the same id is different work and does not exclude it.
        var underAnotherKind = await JobClaimQuery
            .Claimable(db.Jobs, Now, [(JobKind.Deployment, target)]).Select(j => j.Id).ToListAsync();
        underAnotherKind.Should().Equal(backup.Id);
    }

    private static IQueryable<Job> Claimable(HarboraDbContext db) =>
        JobClaimQuery.Claimable(db.Jobs, Now, NothingInFlight);

    /// <summary>What <c>JobWorker.ClaimNextAsync</c> writes, and nothing else.</summary>
    private static void Take(Job job, string worker)
    {
        job.Status = JobStatus.Running;
        job.StartedAt = Now;
        job.ClaimedBy = worker;
        job.Attempts++;
        job.ClaimStamp++;
    }

    private static Job Enqueue(Guid target) => new()
    {
        Kind = JobKind.Deployment,
        TargetId = target,
        Status = JobStatus.Pending,
        CreatedAt = Now.AddMinutes(-30)
    };

    private static async Task EnqueueAsync(string connectionString, Guid target)
    {
        await using var db = PostgresLane.Open(connectionString);
        db.Jobs.Add(Enqueue(target));
        await db.SaveChangesAsync();
    }
}
