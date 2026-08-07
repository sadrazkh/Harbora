using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Proves the claim is a query <b>Postgres</b> can run, not merely one that C# can evaluate.
///
/// <para>
/// Every other test of the queue uses <c>UseInMemoryDatabase</c>, and that provider accepts things
/// Postgres will not — <c>string.Equals(a, b, StringComparison.OrdinalIgnoreCase)</c> is the classic,
/// and adding one to the claim leaves all twenty <c>JobConcurrencyTests</c> green while Npgsql
/// refuses the query outright. The worker catches that refusal so the loop survives, which means the
/// only symptom is "Job worker loop failed" repeating in the log while the panel keeps accepting
/// deployments and runs none of them.
/// </para>
///
/// <para>
/// These build a context on the real Npgsql provider — the pattern <c>MigrationConsistencyTests</c>
/// already uses — and ask the query for its SQL. Compiling the expression tree is what
/// <c>ToQueryString</c> does; no connection is opened and no database is needed.
/// </para>
/// </summary>
public class JobClaimQueryTests
{
    private static HarboraDbContext PostgresContext() => new(
        new DbContextOptionsBuilder<HarboraDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=unused;Password=unused")
            .Options);

    [Fact]
    public void What_a_job_excludes_on_is_worked_out_by_the_database()
    {
        using var db = PostgresContext();

        var sql = JobClaimQuery.Claimable(
            db.Jobs,
            DateTimeOffset.UtcNow,
            [(JobKind.Deployment, Guid.NewGuid()), (JobKind.BackupSnapshot, Guid.NewGuid())]).ToQueryString();

        // The fallback "a job that named nothing excludes on its own target" has to survive the trip
        // to SQL. If it is ever written as Job.ExcludesOn, this line is where that is caught: the
        // call above throws before it can return anything to assert on.
        sql.Should().Contain("COALESCE",
            "the exclusion key is ExclusiveWith falling back to TargetId, and the database is what applies it");
        sql.Should().Contain("ExclusiveWith").And.Contain("TargetId");

        // One term per held pair, not a single one that quietly drops the others: two jobs in flight
        // must exclude two things.
        System.Text.RegularExpressions.Regex.Matches(sql, "COALESCE").Count.Should().Be(2,
            "each thing this process is already running gets its own term");
    }

    [Fact]
    public void The_rest_of_the_claim_translates_too()
    {
        using var db = PostgresContext();

        // Nothing in flight — the shape the worker runs on an idle install, and the one every claim
        // starts from.
        var sql = JobClaimQuery.Claimable(db.Jobs, DateTimeOffset.UtcNow, []).ToQueryString();

        sql.Should().Contain("SELECT").And.Contain("\"Jobs\"");
        sql.Should().Contain("NextAttemptAt", "a job serving a retry backoff is filtered out in the database");
        sql.Should().Contain("ORDER BY", "oldest-first is the queue's fairness rule and it is the database's job");
        sql.Should().Contain("CreatedAt");
    }
}
