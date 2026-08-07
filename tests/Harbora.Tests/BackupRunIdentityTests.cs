using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Backups;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The two identities a backup run has: the target it must not double up on, and itself.
///
/// <para>
/// A <c>Backup</c> row is created per run, so — exactly like a <c>Deployment</c> — the row's own id
/// says nothing about what must stay serial. What must is the target, and the target is not a row
/// either: it is a type and a reference. This is where that pair becomes the single value
/// <c>Job.ExcludesOn</c> compares.
/// </para>
/// </summary>
public class BackupRunIdentityTests
{
    [Fact]
    public void The_same_target_always_gives_the_same_key()
    {
        BackupRunIdentity.ExclusionKeyFor(BackupType.Volume, "uploads")
            .Should().Be(BackupRunIdentity.ExclusionKeyFor(BackupType.Volume, "uploads"),
                "the key is stamped on the job row at enqueue and compared against jobs enqueued " +
                "minutes earlier by something else, so it cannot depend on this process");
    }

    [Fact]
    public void Two_different_targets_do_not_share_a_key()
    {
        BackupRunIdentity.ExclusionKeyFor(BackupType.Volume, "uploads")
            .Should().NotBe(BackupRunIdentity.ExclusionKeyFor(BackupType.Volume, "avatars"));
    }

    /// <summary>
    /// A volume backup and a database backup can name the same reference — a docker volume name and
    /// a service id are different namespaces — and the artifacts they produce are different files.
    /// </summary>
    [Fact]
    public void The_same_reference_under_a_different_type_is_a_different_target()
    {
        var reference = Guid.NewGuid().ToString();

        BackupRunIdentity.ExclusionKeyFor(BackupType.Database, reference)
            .Should().NotBe(BackupRunIdentity.ExclusionKeyFor(BackupType.Volume, reference));
    }

    /// <summary>
    /// The reference reaches this from a form post and from a stored schedule, so the same target
    /// really does arrive spelled two ways — a service id in braces from one caller, lower-case
    /// from another. Two spellings that are one target must not be allowed to run at once.
    /// </summary>
    [Theory]
    [InlineData("  uploads  ")]
    [InlineData("UPLOADS")]
    public void A_reference_spelled_differently_is_still_the_same_target(string spelling)
    {
        BackupRunIdentity.ExclusionKeyFor(BackupType.Volume, spelling)
            .Should().Be(BackupRunIdentity.ExclusionKeyFor(BackupType.Volume, "uploads"));
    }

    /// <summary>
    /// "platform" is the reference every full-platform backup carries, whichever workspace asked for
    /// it — and <c>platform-{stamp}.json.gz</c> carries no workspace either. Keying on the type and
    /// the reference alone is what keeps two tenants' full-platform backups from running into each
    /// other in the shared staging volume.
    /// </summary>
    [Fact]
    public void Every_full_platform_backup_excludes_on_the_same_key()
    {
        BackupRunIdentity.ExclusionKeyFor(BackupType.FullPlatform, "platform")
            .Should().Be(BackupRunIdentity.ExclusionKeyFor(BackupType.FullPlatform, "platform"));
    }

    [Fact]
    public void A_target_with_no_reference_still_has_a_key()
    {
        // Never Guid.Empty by accident and never a throw: an unusable key here would either
        // serialise the whole platform's backups or none of them, silently.
        BackupRunIdentity.ExclusionKeyFor(BackupType.FullPlatform, null)
            .Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Two_runs_of_one_target_at_the_same_instant_stage_under_different_names()
    {
        var at = new DateTimeOffset(2026, 7, 29, 18, 49, 9, TimeSpan.Zero);

        BackupRunIdentity.StampFor(at, Guid.NewGuid())
            .Should().NotBe(BackupRunIdentity.StampFor(at, Guid.NewGuid()),
                "the filename is the last thing standing between two concurrent runs and one " +
                "archive holding half of each");
    }

    [Fact]
    public void The_stamp_of_one_run_never_changes()
    {
        var at = new DateTimeOffset(2026, 7, 29, 18, 49, 9, TimeSpan.Zero);
        var run = Guid.NewGuid();

        BackupRunIdentity.StampFor(at, run).Should().Be(BackupRunIdentity.StampFor(at, run),
            "the staged path is rebuilt from it after the helper has written the file");
    }
}
