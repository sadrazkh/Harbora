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
    public void A_revocation_can_never_be_swept_before_the_grant_it_revokes()
    {
        // Defence in depth behind sweeping node commands by IssuedAt rather than CompletedAt. A
        // revoke is always issued after its grant, so any cutoff that reaches the revoke has already
        // reached the grant: the pair can only disappear grant-first, which fails closed. Losing a
        // revocation while its grant survived would re-authorise access somebody withdrew.
        //
        // The completion times below are deliberately inverted — a slow grant and an instant revoke,
        // which is the ordinary case, since creating a credential does more work than dropping one.
        // Judging age by CompletedAt would therefore sweep the revocation first, and this test says
        // so rather than leaving the choice of basis looking arbitrary.
        var cutoff = Now.AddDays(-90);
        var grant = Command(NodeCommands.CreateDatabaseAccessGrant, NodeCommandStatus.Succeeded, cutoff.AddDays(-10));
        grant.CompletedAt = cutoff.AddDays(-8);
        var revoke = Command(NodeCommands.RevokeDatabaseAccessGrant, NodeCommandStatus.Succeeded, cutoff.AddDays(-9));
        revoke.CompletedAt = cutoff.AddDays(-9);

        RetentionRule.NodeCommandAgeBasis(grant).Should().BeBefore(RetentionRule.NodeCommandAgeBasis(revoke));
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
