using System.Linq.Expressions;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Auditing;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;
using Harbora.Domain.Nodes;
using Harbora.Infrastructure.Maintenance;
using Harbora.NodeAgent.Contracts;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The retention decisions themselves, exercised without a database.
///
/// <para>
/// This is where "which rows go" is actually proven. The sweeper only carries these predicates to
/// <c>ExecuteDeleteAsync</c>; every judgement that could delete something a running operation still
/// needs is made here, so this is the file that has to be convincing.
/// </para>
/// </summary>
public class RetentionRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 3, 0, 0, TimeSpan.Zero);

    private static List<T> Selected<T>(Expression<Func<T, bool>> rule, params T[] rows) =>
        rows.AsQueryable().Where(rule).ToList();

    // ---------- cutoffs ----------

    [Fact]
    public void A_configured_number_of_days_becomes_a_cutoff_that_far_back()
    {
        RetentionRule.CutoffFor(90, Now).Should().Be(Now.AddDays(-90));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Zero_or_less_means_keep_forever_rather_than_delete_everything(int days)
    {
        // The difference between "no cutoff" and "a cutoff of now" is the whole table. An operator
        // who blanks a setting must get the safe reading of it.
        RetentionRule.CutoffFor(days, Now).Should().BeNull();
    }

    [Theory]
    [InlineData(int.MaxValue)]      // TimeSpan.FromDays gives up first, past ~10.7 million days
    [InlineData(2_000_000)]         // ~5,500 years: the subtraction underflows instead
    public void A_span_of_days_too_long_to_be_a_date_means_keep_forever_rather_than_throwing(int days)
    {
        // An operator reaching for a very large integer to mean "keep this for a very long time"
        // is the ordinary way to arrive here, and the two readings agree: nothing in a database is
        // older than the beginning of representable time.
        //
        // This is not a nicety. CutoffFor is called from the sweeper OUTSIDE the per-table
        // try/catch, so one such value threw past every table's guard and out of SweepAsync — and
        // because deployment logs are swept first, a bad value on that one key meant no table was
        // ever swept, every night, behind a log line that named neither the table nor the key.
        RetentionRule.CutoffFor(days, Now).Should().BeNull();
    }

    [Fact]
    public void A_long_but_representable_retention_is_still_an_ordinary_cutoff()
    {
        // The clamp above must not swallow values an operator could plausibly mean: a century of
        // audit history is a real answer to a real obligation.
        RetentionRule.CutoffFor(36_500, Now).Should().Be(Now.AddDays(-36_500));
    }

    [Fact]
    public void Keep_forever_begins_exactly_where_a_date_stops_being_representable()
    {
        // Written from the clock rather than as a magic number, because the boundary moves with
        // "now": how far back a cutoff can reach is how far "now" is from the start of time.
        var reach = (int)(Now - DateTimeOffset.MinValue).TotalDays;

        RetentionRule.CutoffFor(reach - 2, Now).Should().NotBeNull("this one still lands on a real date");
        RetentionRule.CutoffFor(reach, Now).Should().BeNull("this one cannot, so it means keep forever");
    }

    // ---------- when the sweep runs ----------

    private static DateTimeOffset At(int hour, int minute) => new(2026, 8, 8, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(23, 30, 3, 210)]       // late-evening boot: tonight's window is three and a half hours off
    [InlineData(2, 0, 3, 60)]          // an hour short of it
    [InlineData(3, 0, 3, 1440)]        // standing exactly on it: the next one, not one right now
    [InlineData(2, 55, 3, 1445)]       // five minutes short of it is still the boot path; wait a day
    public void The_sweep_is_scheduled_for_a_chosen_hour_rather_than_a_day_after_whenever_the_panel_booted(
        int bootHour, int bootMinute, int sweepHourUtc, int expectedMinutes)
    {
        // A fixed period measured from start-up runs the sweep at whatever time the panel happened
        // to be restarted — the one time of day nobody chose. "Nightly" has to mean a night.
        RetentionRule.DelayUntilNextSweep(At(bootHour, bootMinute), sweepHourUtc)
            .Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(24, 23)]
    [InlineData(int.MaxValue, 23)]
    public void An_hour_outside_the_clock_is_clamped_rather_than_throwing(int configured, int effective)
    {
        // Same reasoning as the cutoff: a mistyped hour must cost an operator a sweep at an odd
        // time, not the sweeper.
        RetentionRule.DelayUntilNextSweep(Now, configured)
            .Should().Be(RetentionRule.DelayUntilNextSweep(Now, effective));
    }

    // ---------- deployment logs ----------

    private static DeploymentLog Line(Guid deploymentId, DateTimeOffset at) =>
        new() { DeploymentId = deploymentId, Timestamp = at, Message = "line" };

    [Fact]
    public void Deployment_log_lines_older_than_the_cutoff_go_and_newer_ones_stay()
    {
        var cutoff = Now.AddDays(-90);
        var old = Line(Guid.NewGuid(), cutoff.AddSeconds(-1));
        var fresh = Line(Guid.NewGuid(), cutoff.AddSeconds(1));

        var selected = Selected(RetentionRule.DeploymentLogsToDelete(cutoff, []), old, fresh);

        selected.Should().ContainSingle().Which.Should().BeSameAs(old);
    }

    [Fact]
    public void The_logs_of_a_protected_deployment_are_never_swept_however_old_they_are()
    {
        // A deployment that is still building, or that is the release an app is currently running.
        // Both are cases where the lines are either still being appended or still the only account
        // of what is running right now.
        var cutoff = Now.AddDays(-90);
        var running = Guid.NewGuid();
        var ancient = Line(running, cutoff.AddYears(-1));

        var selected = Selected(RetentionRule.DeploymentLogsToDelete(cutoff, [running]), ancient);

        selected.Should().BeEmpty();
    }

    // ---------- audit ----------

    [Fact]
    public void Audit_entries_older_than_the_cutoff_go_and_newer_ones_stay()
    {
        var cutoff = Now.AddDays(-365);
        var old = new AuditLog { Action = "user.login", CreatedAt = cutoff.AddSeconds(-1) };
        var fresh = new AuditLog { Action = "user.login", CreatedAt = cutoff.AddSeconds(1) };

        Selected(RetentionRule.AuditLogsToDelete(cutoff), old, fresh)
            .Should().ContainSingle().Which.Should().BeSameAs(old);
    }

    // ---------- cron runs ----------

    [Fact]
    public void Cron_runs_that_finished_before_the_cutoff_go_and_later_ones_stay()
    {
        var cutoff = Now.AddDays(-90);
        var old = new CronRun { StartedAt = cutoff.AddDays(-1), FinishedAt = cutoff.AddSeconds(-1) };
        var fresh = new CronRun { StartedAt = cutoff.AddDays(-1), FinishedAt = cutoff.AddSeconds(1) };

        Selected(RetentionRule.CronRunsToDelete(cutoff), old, fresh)
            .Should().ContainSingle().Which.Should().BeSameAs(old);
    }

    [Fact]
    public void A_cron_run_that_has_not_finished_is_never_swept()
    {
        // An unfinished row IS the "one at a time" lock CronJobRunner takes before starting a
        // container. Deleting it would let a second container start alongside the first.
        var cutoff = Now.AddDays(-90);
        var stuck = new CronRun { StartedAt = cutoff.AddYears(-1), FinishedAt = null };

        Selected(RetentionRule.CronRunsToDelete(cutoff), stuck).Should().BeEmpty();
    }

    // ---------- node commands ----------

    private static NodeCommandRecord Command(
        string verb, NodeCommandStatus status, DateTimeOffset issuedAt) =>
        new() { Command = verb, Status = status, IssuedAt = issuedAt, NodeId = "node-1" };

    [Fact]
    public void Finished_node_commands_older_than_the_cutoff_go_and_newer_ones_stay()
    {
        var cutoff = Now.AddDays(-90);
        var old = Command(NodeCommands.DeployWorkload, NodeCommandStatus.Succeeded, cutoff.AddSeconds(-1));
        var fresh = Command(NodeCommands.DeployWorkload, NodeCommandStatus.Succeeded, cutoff.AddSeconds(1));

        Selected(RetentionRule.NodeCommandsToDelete(cutoff), old, fresh)
            .Should().ContainSingle().Which.Should().BeSameAs(old);
    }

    [Theory]
    [InlineData(NodeCommandStatus.Queued)]
    [InlineData(NodeCommandStatus.Sent)]
    [InlineData(NodeCommandStatus.Acknowledged)]
    public void A_node_command_still_in_flight_is_never_swept(NodeCommandStatus status)
    {
        // The ack and result frames find their row by CommandId. A command whose row has been
        // deleted comes back to a panel that has no record of asking, and the answer is dropped.
        var cutoff = Now.AddDays(-90);
        var inFlight = Command(NodeCommands.DeployWorkload, status, cutoff.AddYears(-1));

        Selected(RetentionRule.NodeCommandsToDelete(cutoff), inFlight).Should().BeEmpty();
    }

    [Theory]
    [InlineData(NodeCommands.CreateDatabaseAccessGrant)]
    [InlineData(NodeCommands.RevokeDatabaseAccessGrant)]
    public void The_database_access_grant_ledger_is_never_swept(string verb)
    {
        // NodeTunnelGateway authorises a live tunnel by finding the issuing command, and refuses it
        // by finding a later revocation. These rows are an authorisation ledger, not history.
        var cutoff = Now.AddDays(-90);
        var ancient = Command(verb, NodeCommandStatus.Succeeded, cutoff.AddYears(-1));

        Selected(RetentionRule.NodeCommandsToDelete(cutoff), ancient).Should().BeEmpty();
    }

    [Fact]
    public void A_command_issued_later_is_never_swept_while_an_earlier_one_survives()
    {
        // This is the ordering that makes "a revocation can never be swept before the grant it
        // revokes" true structurally, behind the outright exclusion above. A revoke is always
        // issued after its grant, so any cutoff that reaches the revoke has already reached the
        // grant: the pair can only disappear grant-first, and a grant with no row is denied.
        // Losing a revocation while its grant survived would re-authorise access somebody withdrew.
        //
        // The property has to be asserted on the predicate that actually deletes, not on a parallel
        // helper — the two are separate declarations, and a change of basis in the predicate is
        // exactly the change that would matter. The verbs here are ordinary ones because the ledger
        // verbs are excluded outright, which would make the ordering unobservable.
        //
        // The completion times are deliberately inverted: a slow command issued first that finished
        // after the cutoff, and a fast one issued later that finished before it. That is the
        // ordinary shape of a grant and a revoke, since creating a credential does more work than
        // dropping one — and under a CompletedAt basis it takes the later row and leaves the
        // earlier one, which is the inversion this test exists to forbid.
        var cutoff = Now.AddDays(-90);

        var earlierButSlow = Command(NodeCommands.DeployWorkload, NodeCommandStatus.Succeeded, cutoff.AddHours(-2));
        earlierButSlow.CompletedAt = cutoff.AddHours(1);
        var laterButInstant = Command(NodeCommands.DeployWorkload, NodeCommandStatus.Succeeded, cutoff.AddMinutes(-30));
        laterButInstant.CompletedAt = cutoff.AddMinutes(-29);

        var selected = Selected(RetentionRule.NodeCommandsToDelete(cutoff), earlierButSlow, laterButInstant);

        selected.Should().Contain(laterButInstant)
            .And.Contain(earlierButSlow, "the later row can never go while the earlier one stays");
    }

    [Fact]
    public void The_predicate_and_the_entity_agree_about_which_statuses_are_finished()
    {
        // NodeCommandsToDelete spells the terminal statuses out because it has to become SQL, while
        // NodeCommandRecord.IsTerminal is C#. Nothing in the type system keeps the two lists in
        // step, so this does: a status appended to the enum and to only one of them fails here.
        var cutoff = Now.AddDays(-90);

        foreach (var status in Enum.GetValues<NodeCommandStatus>())
        {
            var record = Command(NodeCommands.DeployWorkload, status, cutoff.AddDays(-1));

            Selected(RetentionRule.NodeCommandsToDelete(cutoff), record).Should()
                .HaveCount(record.IsTerminal ? 1 : 0,
                    "the sweep's idea of a finished {0} command must match the record's own", status);
        }
    }

    // ---------- node events ----------

    [Fact]
    public void Node_events_older_than_the_cutoff_go_and_newer_ones_stay()
    {
        var cutoff = Now.AddDays(-90);
        var old = new NodeEventRecord { NodeId = "node-1", Kind = "DiskPressure", At = cutoff.AddSeconds(-1) };
        var fresh = new NodeEventRecord { NodeId = "node-1", Kind = "DiskPressure", At = cutoff.AddSeconds(1) };

        Selected(RetentionRule.NodeEventsToDelete(cutoff), old, fresh)
            .Should().ContainSingle().Which.Should().BeSameAs(old);
    }

    // ---------- idempotency ----------

    [Fact]
    public void An_idempotency_record_goes_once_its_own_expiry_has_passed()
    {
        // No configured cutoff: the row carries its deadline, and IdempotencyStore already treats an
        // expired row as absent, so deleting it changes no answer anyone can still get.
        var expired = new IdempotencyRecord { Key = "a", Endpoint = "e", ExpiresAt = Now.AddSeconds(-1) };
        var live = new IdempotencyRecord { Key = "b", Endpoint = "e", ExpiresAt = Now.AddSeconds(1) };

        Selected(RetentionRule.IdempotencyRecordsToDelete(Now), expired, live)
            .Should().ContainSingle().Which.Should().BeSameAs(expired);
    }

    // ---------- password reset tokens ----------

    [Fact]
    public void A_token_used_longer_ago_than_the_cutoff_goes()
    {
        var cutoff = Now.AddDays(-7);
        var old = new PasswordResetToken { TokenHash = "a", ExpiresAt = Now.AddDays(1), UsedAt = cutoff.AddSeconds(-1) };
        var recent = new PasswordResetToken { TokenHash = "b", ExpiresAt = Now.AddDays(1), UsedAt = cutoff.AddSeconds(1) };

        Selected(RetentionRule.PasswordResetTokensToDelete(cutoff), old, recent)
            .Should().ContainSingle().Which.Should().BeSameAs(old);
    }

    [Fact]
    public void A_token_that_expired_longer_ago_than_the_cutoff_goes()
    {
        var cutoff = Now.AddDays(-7);
        var old = new PasswordResetToken { TokenHash = "a", ExpiresAt = cutoff.AddSeconds(-1) };
        var recent = new PasswordResetToken { TokenHash = "b", ExpiresAt = cutoff.AddSeconds(1) };

        Selected(RetentionRule.PasswordResetTokensToDelete(cutoff), old, recent)
            .Should().ContainSingle().Which.Should().BeSameAs(old);
    }

    [Fact]
    public void A_reset_link_someone_could_still_be_holding_is_never_swept()
    {
        // Unused and not yet expired: this is a live link in somebody's inbox. Deleting it turns a
        // working reset into "this link is invalid" with no explanation.
        var cutoff = Now.AddDays(-7);
        var live = new PasswordResetToken { TokenHash = "a", ExpiresAt = Now.AddMinutes(30), UsedAt = null };

        Selected(RetentionRule.PasswordResetTokensToDelete(cutoff), live).Should().BeEmpty();
    }
}
