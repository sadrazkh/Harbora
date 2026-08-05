using FluentAssertions;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Snapshot state transitions. The rule worth enforcing is the backwards one: a Failed snapshot
/// moved back to Running loses the reason it failed, and a Completed one moved back claims data is
/// being written when it is not. Both are easy to write by accident in a retry path.
/// </summary>
public class SnapshotLifecycleTests
{
    [Theory]
    [InlineData(BackupSnapshotStatus.Pending, BackupSnapshotStatus.Running)]
    [InlineData(BackupSnapshotStatus.Pending, BackupSnapshotStatus.Preparing)]
    [InlineData(BackupSnapshotStatus.Running, BackupSnapshotStatus.Completed)]
    [InlineData(BackupSnapshotStatus.Running, BackupSnapshotStatus.CompletedWithWarnings)]
    [InlineData(BackupSnapshotStatus.Running, BackupSnapshotStatus.Verifying)]
    [InlineData(BackupSnapshotStatus.Verifying, BackupSnapshotStatus.Completed)]
    [InlineData(BackupSnapshotStatus.Completed, BackupSnapshotStatus.Deleting)]
    [InlineData(BackupSnapshotStatus.Deleting, BackupSnapshotStatus.Deleted)]
    public void Allows_forward_transitions(BackupSnapshotStatus from, BackupSnapshotStatus to)
    {
        SnapshotLifecycle.CanTransition(from, to).Should().BeTrue();
    }

    [Theory]
    [InlineData(BackupSnapshotStatus.Completed, BackupSnapshotStatus.Running)]
    [InlineData(BackupSnapshotStatus.Failed, BackupSnapshotStatus.Running)]
    [InlineData(BackupSnapshotStatus.Cancelled, BackupSnapshotStatus.Completed)]
    [InlineData(BackupSnapshotStatus.Deleted, BackupSnapshotStatus.Running)]
    [InlineData(BackupSnapshotStatus.Completed, BackupSnapshotStatus.Failed)]
    public void Refuses_to_reopen_a_finished_snapshot(BackupSnapshotStatus from, BackupSnapshotStatus to)
    {
        SnapshotLifecycle.CanTransition(from, to).Should().BeFalse();
    }

    /// <summary>
    /// An idempotent job that resumes after a crash re-applies the state it already set. Treating
    /// that as illegal would make every crash-and-resume look like a bug.
    /// </summary>
    [Fact]
    public void Re_applying_the_current_state_is_allowed()
    {
        SnapshotLifecycle.CanTransition(BackupSnapshotStatus.Running, BackupSnapshotStatus.Running)
            .Should().BeTrue();
    }

    [Fact]
    public void Transition_throws_and_names_both_states()
    {
        var snapshot = new BackupSnapshot { Status = BackupSnapshotStatus.Failed };

        var act = () => SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Running);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed*Running*");
        snapshot.Status.Should().Be(BackupSnapshotStatus.Failed);
    }

    [Fact]
    public void A_deleting_snapshot_may_fall_back_to_failed()
    {
        // So the row does not sit in a state implying the data is gone when the engine refused.
        SnapshotLifecycle.CanTransition(BackupSnapshotStatus.Deleting, BackupSnapshotStatus.Failed)
            .Should().BeTrue();
    }
}

/// <summary>
/// Policy validation. Every rule here prevents a policy that looks configured and does nothing —
/// the failure mode that stays invisible until someone needs a restore.
/// </summary>
public class BackupPolicyValidatorTests
{
    private static BackupPolicy Valid() => new()
    {
        Name = "Nightly app data",
        RepositoryId = Guid.CreateVersion7(),
        TargetRef = "harbora_app_data",
        TargetType = BackupTargetType.DockerVolume,
        Schedule = "0 3 * * *",
        Timezone = "UTC",
        Retention = new RetentionPolicy()
    };

    private static bool AnySchedule(string _) => true;

    [Fact]
    public void Accepts_a_well_formed_policy()
    {
        BackupPolicyValidator.Validate(Valid(), AnySchedule).Should().BeEmpty();
    }

    /// <summary>The companion to the retention test that proves such a policy prunes everything.</summary>
    [Fact]
    public void Refuses_a_retention_that_would_keep_nothing()
    {
        var policy = Valid();
        policy.Retention = new RetentionPolicy
        {
            KeepLatest = 0, KeepHourly = 0, KeepDaily = 0,
            KeepWeekly = 0, KeepMonthly = 0, KeepYearly = 0
        };

        var errors = BackupPolicyValidator.Validate(policy, AnySchedule);

        errors.Should().Contain(e => e.Field == nameof(RetentionPolicy.KeepLatest));
    }

    [Fact]
    public void Refuses_a_schedule_the_parser_rejects()
    {
        var policy = Valid();
        policy.Schedule = "every other tuesday";

        var errors = BackupPolicyValidator.Validate(policy, _ => false);

        errors.Should().Contain(e => e.Field == nameof(BackupPolicy.Schedule));
    }

    [Fact]
    public void Refuses_a_timezone_the_server_cannot_resolve()
    {
        var policy = Valid();
        policy.Timezone = "Mars/Olympus_Mons";

        var errors = BackupPolicyValidator.Validate(policy, AnySchedule);

        errors.Should().Contain(e => e.Field == nameof(BackupPolicy.Timezone));
    }

    [Fact]
    public void Refuses_a_policy_with_no_target()
    {
        var policy = Valid();
        policy.TargetRef = "";

        BackupPolicyValidator.Validate(policy, AnySchedule)
            .Should().Contain(e => e.Field == nameof(BackupPolicy.TargetRef));
    }

    [Fact]
    public void Refuses_a_maximum_age_that_would_delete_backups_immediately()
    {
        var policy = Valid();
        policy.Retention.MaximumAgeDays = 0;

        BackupPolicyValidator.Validate(policy, AnySchedule)
            .Should().Contain(e => e.Field == nameof(RetentionPolicy.MaximumAgeDays));
    }
}

/// <summary>
/// Argument allowlists. The structural defence is that arguments are passed as a list and no shell
/// is spawned; this is the second layer, so that a future refactor reintroducing a shell does not
/// immediately become remote code execution (THREAT_MODEL T1).
/// </summary>
public class EngineArgumentGuardTests
{
    [Theory]
    [InlineData("nightly-backups")]
    [InlineData("Prod DB 2026")]
    [InlineData("app_data.v2")]
    public void Accepts_ordinary_names(string name) =>
        EngineArgumentGuard.IsSafeName(name).Should().BeTrue();

    [Theory]
    [InlineData("repo; rm -rf /")]
    [InlineData("repo && curl evil.example")]
    [InlineData("repo$(whoami)")]
    [InlineData("repo`id`")]
    [InlineData("repo|tee /etc/passwd")]
    [InlineData("repo\nsecond-line")]
    [InlineData("-starts-with-a-dash")]
    [InlineData("climbs/../out")]
    [InlineData("")]
    public void Rejects_names_carrying_shell_or_traversal_syntax(string name) =>
        EngineArgumentGuard.IsSafeName(name).Should().BeFalse();

    [Theory]
    [InlineData("k9f3a1b2c3")]
    [InlineData("snapshot_2026-03-10")]
    public void Accepts_ordinary_snapshot_ids(string id) =>
        EngineArgumentGuard.IsSafeSnapshotId(id).Should().BeTrue();

    [Theory]
    [InlineData("$(whoami)")]
    [InlineData("id; cat /etc/shadow")]
    [InlineData("--delete-everything")]
    public void Rejects_snapshot_ids_carrying_syntax(string id) =>
        EngineArgumentGuard.IsSafeSnapshotId(id).Should().BeFalse();

    [Theory]
    [InlineData("my-backups", true)]
    [InlineData("MyBackups", false)]      // buckets are lowercase
    [InlineData("ab", false)]             // too short
    [InlineData("-leading-dash", false)]
    [InlineData("trailing-dash-", false)]
    public void Applies_bucket_naming_rules(string bucket, bool expected) =>
        EngineArgumentGuard.IsSafeBucket(bucket).Should().Be(expected);

    [Theory]
    [InlineData("harbora_app_data", true)]
    [InlineData("../etc", false)]
    [InlineData("-v", false)]
    [InlineData("vol name", false)]
    public void Applies_volume_naming_rules(string volume, bool expected) =>
        EngineArgumentGuard.IsSafeVolumeName(volume).Should().Be(expected);

    [Fact]
    public void Require_throws_rather_than_returning_false()
    {
        var act = () => EngineArgumentGuard.Require(
            "repo; rm -rf /", EngineArgumentGuard.IsSafeName, "Repository name");

        act.Should().Throw<ArgumentException>().WithMessage("*not permitted*");
    }
}
