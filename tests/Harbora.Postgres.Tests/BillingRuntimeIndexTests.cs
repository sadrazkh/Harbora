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
    public async Task A_workspace_owner_can_only_ever_receive_one_trial_credit_voucher()
    {
        // Sub-project 1.9's whole idempotency guarantee: SignupTrialCreditService's fast-path read
        // cannot win a real race between two concurrent grants for the same brand-new owner — this
        // is the constraint that actually settles it, live, against Postgres rather than InMemory.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("trial_credit_once"));
        var owner = Guid.CreateVersion7();
        db.BillingVouchers.Add(Voucher(owner, isTrialCredit: true, codeHash: new string('B', 64)));
        await db.SaveChangesAsync();

        db.BillingVouchers.Add(Voucher(owner, isTrialCredit: true, codeHash: new string('C', 64)));

        (await Refusal(db)).ConstraintName.Should().Be("IX_BillingVouchers_TrialCreditOwner");
    }

    [PostgresFact]
    public async Task An_administrators_own_name_can_still_head_many_ordinary_vouchers()
    {
        // The index is partial — WHERE "IsTrialCredit" — so it says nothing about an administrator's
        // support vouchers, which all repeat the same CreatedByUserId (the admin) by design. Proves
        // the index does not accidentally cap every operator at one voucher for life.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("admin_vouchers_repeat"));
        var admin = Guid.CreateVersion7();
        db.BillingVouchers.Add(Voucher(admin, isTrialCredit: false, codeHash: new string('D', 64)));
        db.BillingVouchers.Add(Voucher(admin, isTrialCredit: false, codeHash: new string('E', 64)));

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync();
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

    private static BillingVoucher Voucher(
        Guid? createdByUserId = null, bool isTrialCredit = false, string? codeHash = null) => new()
    {
        CodeHash = codeHash ?? new string('A', 64),
        CodeHint = "AAAA",
        AmountMinor = 100_000,
        Currency = "IRR",
        Note = "test",
        CreatedByUserId = createdByUserId ?? Guid.CreateVersion7(),
        IsTrialCredit = isTrialCredit
    };

    private static async Task<PostgresException> Refusal(HarboraDbContext db)
    {
        var thrown = await db.Awaiting(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        var inner = thrown.Which.InnerException.Should().BeOfType<PostgresException>().Which;
        inner.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        return inner;
    }
}
