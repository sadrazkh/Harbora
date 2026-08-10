using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Harbora.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>The runtime's exactly-once boundaries, exercised by PostgreSQL rather than InMemory.</summary>
[Collection(PostgresLane.Collection)]
public sealed class BillingRuntimeIndexTests(PostgresLane lane)
{
    private static readonly DateTimeOffset Hour = new(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);

    [PostgresFact]
    public async Task An_ended_hour_has_one_durable_billing_run()
    {
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("billing_run_once"));
        db.BillingRuns.Add(new BillingRun { BillingHour = Hour });
        await db.SaveChangesAsync();

        db.BillingRuns.Add(new BillingRun { BillingHour = Hour });

        (await Refusal(db)).ConstraintName.Should().Be("IX_BillingRuns_BillingHour");
    }

    [PostgresFact]
    public async Task A_voucher_digest_can_only_be_issued_once()
    {
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("voucher_digest_once"));
        db.BillingVouchers.Add(Voucher());
        await db.SaveChangesAsync();

        db.BillingVouchers.Add(Voucher());

        (await Refusal(db)).ConstraintName.Should().Be("IX_BillingVouchers_CodeHash");
    }

    [PostgresFact]
    public async Task One_billing_run_cannot_have_two_live_dispatches()
    {
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("billing_job_once"));
        var run = Guid.CreateVersion7();
        db.Jobs.Add(new Job { Kind = JobKind.BillingHour, TargetId = run, Status = JobStatus.Pending });
        await db.SaveChangesAsync();

        db.Jobs.Add(new Job { Kind = JobKind.BillingHour, TargetId = run, Status = JobStatus.Running });

        (await Refusal(db)).ConstraintName.Should().Be("IX_Jobs_Kind_TargetId");
    }

    [PostgresFact]
    public async Task A_completed_dispatch_does_not_block_a_later_retry()
    {
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("billing_job_retry"));
        var run = Guid.CreateVersion7();
        db.Jobs.Add(new Job { Kind = JobKind.BillingHour, TargetId = run, Status = JobStatus.Succeeded });
        db.Jobs.Add(new Job { Kind = JobKind.BillingHour, TargetId = run, Status = JobStatus.Pending });

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    private static BillingVoucher Voucher() => new()
    {
        CodeHash = new string('A', 64),
        CodeHint = "AAAA",
        AmountMinor = 100_000,
        Currency = "IRR",
        Note = "test",
        CreatedByUserId = Guid.CreateVersion7()
    };

    private static async Task<PostgresException> Refusal(HarboraDbContext db)
    {
        var thrown = await db.Awaiting(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        var inner = thrown.Which.InnerException.Should().BeOfType<PostgresException>().Which;
        inner.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        return inner;
    }
}
