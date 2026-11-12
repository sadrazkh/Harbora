using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// The job queue's retention, against the provider that has to run it.
///
/// <para>
/// The fast suite reads the statements Npgsql builds (<c>JobRetentionSqlTests</c>) and walks the rule
/// over rows in memory, where <c>ExecuteDeleteAsync</c> does not exist. What only a real server can
/// show is that a <c>DELETE … WHERE "Id" IN (SELECT … ORDER BY … LIMIT …)</c> deletes exactly the
/// rows the rule means, a batch at a time: more settled rows than one batch holds, so the loop has to
/// come round at least once, beside the live and the recent rows that must survive it.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class JobRetentionPostgresTests(PostgresLane lane)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 3, 0, 0, TimeSpan.Zero);

    private sealed class Clock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    [PostgresFact]
    public async Task Settled_jobs_past_the_cutoff_go_in_batches_and_live_and_recent_ones_stay()
    {
        var connectionString = await lane.FreshlyMigratedAsync("job_retention");
        var batch = DataRetentionSweeper.DefaultJobDeleteBatchSize;
        var due = batch + 50;

        await using (var db = PostgresLane.Open(connectionString))
        {
            // More due Succeeded rows than one statement may take, seeded by the database itself so the
            // test does not spend its time on ten thousand inserts.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Jobs" ("Id", "Kind", "TargetId", "Status", "Attempts", "CancelRequested",
                                    "ClaimStamp", "CreatedAt", "UpdatedAt", "FinishedAt")
                SELECT gen_random_uuid(), 10, gen_random_uuid(), 2, 1, false, 1,
                       TIMESTAMPTZ '2026-10-01 00:00:00+00' + g * INTERVAL '1 second',
                       TIMESTAMPTZ '2026-10-01 00:00:00+00' + g * INTERVAL '1 second',
                       TIMESTAMPTZ '2026-10-01 00:00:00+00' + g * INTERVAL '1 second'
                FROM generate_series(1, {due}) AS g
                """);

            Job Row(JobStatus status, int queuedDaysAgo, int? finishedDaysAgo) => new()
            {
                Kind = JobKind.Deployment, TargetId = Guid.NewGuid(), Status = status,
                CreatedAt = Now.AddDays(-queuedDaysAgo),
                FinishedAt = finishedDaysAgo is { } f ? Now.AddDays(-f) : null
            };

            db.Jobs.AddRange(
                Row(JobStatus.Failed, 40, 40), Row(JobStatus.Failed, 41, 41), Row(JobStatus.Cancelled, 40, 40),
                // Must survive: still in the queue, however old.
                Row(JobStatus.Pending, 400, null), Row(JobStatus.Running, 400, null),
                // Must survive: settled, but not yet thirty days ago.
                Row(JobStatus.Succeeded, 5, 5), Row(JobStatus.Failed, 29, 29),
                // Must survive: queued long ago, finished last night.
                Row(JobStatus.Succeeded, 45, 1));
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => PostgresLane.Open(connectionString));
        await using var provider = services.BuildServiceProvider();
        var sweeper = new DataRetentionSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new RetentionOptions()),
            new Clock(), NullLogger<DataRetentionSweeper>.Instance);

        var result = await sweeper.SweepAsync(CancellationToken.None);

        result.Failures.Should().BeEmpty();
        result.Deleted[RetentionTables.Jobs].Should().Be(due + 3);

        await using var reader = PostgresLane.Open(connectionString);
        var left = await reader.Jobs.AsNoTracking()
            .OrderBy(j => j.CreatedAt)
            .Select(j => new { j.Status, j.FinishedAt })
            .ToListAsync();

        left.Should().HaveCount(5);
        left.Select(j => j.Status).Should().BeEquivalentTo(
            [JobStatus.Pending, JobStatus.Running, JobStatus.Succeeded, JobStatus.Failed, JobStatus.Succeeded]);
    }
}
