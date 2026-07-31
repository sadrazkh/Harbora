using FluentAssertions;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Backups;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Choosing which backup to check, without anyone remembering to.
///
/// Nobody presses "verify" on a Tuesday for fun — it gets pressed during an incident, which is far
/// too late. Verifying costs a real restore into a scratch database, so the choice has to be frugal
/// and has to prefer the answer that matters most.
/// </summary>
public class VerificationScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static Backup Completed(string target, DateTimeOffset finished,
                                    DateTimeOffset? verified = null, bool? restorable = null) =>
        new()
        {
            Type = BackupType.Database, TargetRef = target,
            Status = BackupStatus.Completed, ArtifactPath = $"/b/{target}-{finished:HHmmss}.sql.gz",
            FinishedAt = finished, VerifiedAt = verified, VerifiedRestorable = restorable
        };

    [Fact]
    public void A_backup_nobody_has_checked_comes_first()
    {
        // "Unknown" is worse than "was fine a week ago".
        var stale = Completed("a", Now.AddDays(-1), verified: Now.AddDays(-30), restorable: true);
        var never = Completed("b", Now.AddDays(-1));

        VerificationSchedule.NextDue([stale, never], Now).Should().Be(never);
    }

    [Fact]
    public void A_verdict_older_than_a_week_is_checked_again()
    {
        var old = Completed("a", Now.AddDays(-20), verified: Now.AddDays(-8), restorable: true);

        VerificationSchedule.NextDue([old], Now).Should().Be(old);
    }

    [Fact]
    public void A_recent_verdict_is_left_alone()
    {
        // Re-restoring something checked yesterday spends a real restore on a question already
        // answered.
        var fresh = Completed("a", Now.AddDays(-2), verified: Now.AddDays(-1), restorable: true);

        VerificationSchedule.NextDue([fresh], Now).Should().BeNull();
    }

    [Fact]
    public void Only_the_newest_backup_of_each_thing_is_a_candidate()
    {
        // Verifying an artifact that retention will prune tomorrow spends a restore on a question
        // nobody will ask.
        var older = Completed("a", Now.AddDays(-3));
        var newest = Completed("a", Now.AddHours(-2));

        VerificationSchedule.NextDue([older, newest], Now).Should().Be(newest);
    }

    [Fact]
    public void Each_target_is_considered_separately()
    {
        // One app's fresh verdict must not hide another app's unchecked backup.
        var checkedOne = Completed("app-a", Now.AddHours(-3), verified: Now.AddHours(-1), restorable: true);
        var uncheckedOne = Completed("app-b", Now.AddHours(-3));

        VerificationSchedule.NextDue([checkedOne, uncheckedOne], Now).Should().Be(uncheckedOne);
    }

    [Fact]
    public void A_backup_that_did_not_finish_is_never_chosen()
    {
        var failed = new Backup { Type = BackupType.Database, TargetRef = "a", Status = BackupStatus.Failed };
        var running = new Backup { Type = BackupType.Database, TargetRef = "b", Status = BackupStatus.Running };

        VerificationSchedule.NextDue([failed, running], Now).Should().BeNull();
    }

    [Fact]
    public void A_completed_backup_with_no_artifact_is_never_chosen()
    {
        // There is nothing to fetch, and the attempt would only produce a confusing failure.
        var noArtifact = new Backup
        {
            Type = BackupType.Database, TargetRef = "a",
            Status = BackupStatus.Completed, ArtifactPath = null, FinishedAt = Now.AddDays(-1)
        };

        VerificationSchedule.NextDue([noArtifact], Now).Should().BeNull();
    }

    [Fact]
    public void The_ones_known_not_to_restore_are_pulled_out_separately()
    {
        // The finding worth waking someone for, and the easiest to lose in a list of green ticks.
        var bad = Completed("a", Now.AddDays(-1), verified: Now, restorable: false);
        var good = Completed("b", Now.AddDays(-1), verified: Now, restorable: true);
        var unknown = Completed("c", Now.AddDays(-1));

        VerificationSchedule.KnownBad([bad, good, unknown]).Should().BeEquivalentTo([bad]);
    }

    [Fact]
    public void Nothing_at_all_is_not_an_error()
    {
        VerificationSchedule.NextDue([], Now).Should().BeNull();
        VerificationSchedule.KnownBad([]).Should().BeEmpty();
    }
}
