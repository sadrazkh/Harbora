using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Backups;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Data-safety tests for backup/restore (doc 15, Phase E).
///
/// The checksum column existed since the first schema and was written on every backup, but nothing
/// ever read it — a volume restore runs `rm -rf /data/*` before untarring, so restoring a corrupt
/// archive destroyed the live data and had nothing to put back. These tests pin down the gate that
/// now stands in front of that.
/// </summary>
public class BackupSafetyTests
{
    // ---- dry-run verification ----

    [Fact]
    public async Task A_healthy_backup_verifies_as_restorable()
    {
        using var h = new BackupHarness();
        var backup = await h.SeedAppConfigBackupAsync();

        var result = await h.Engine().VerifyAsync(backup.Id, default);

        result.IsRestorable.Should().BeTrue(result.Reason);
        result.Checks.Should().OnlyContain(c => c.Passed);
        result.SizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_corrupted_artifact_fails_verification()
    {
        using var h = new BackupHarness();
        var backup = await h.SeedAppConfigBackupAsync();
        h.CorruptArtifact(backup);

        var result = await h.Engine().VerifyAsync(backup.Id, default);

        result.IsRestorable.Should().BeFalse();
        result.Reason.Should().Contain("checksum");
        result.Checks.Should().Contain(c => c.Name == "Checksum matches" && !c.Passed);
    }

    [Fact]
    public async Task A_missing_artifact_fails_verification()
    {
        using var h = new BackupHarness();
        var backup = await h.SeedAppConfigBackupAsync();
        h.DeleteArtifact(backup);

        var result = await h.Engine().VerifyAsync(backup.Id, default);

        result.IsRestorable.Should().BeFalse();
        result.Reason.Should().Contain("missing");
    }

    [Fact]
    public async Task Verification_reads_the_archive_not_just_its_checksum()
    {
        // A checksum only proves the bytes are the ones we stored — not that they form a usable
        // archive. A backup that was garbage at the moment it was written has a perfectly valid
        // checksum, and only reading it can reveal that.
        using var h = new BackupHarness();
        var backup = await h.SeedUnreadableArtifactAsync();

        var result = await h.Engine().VerifyAsync(backup.Id, default);

        result.IsRestorable.Should().BeFalse();
        result.Checks.Should().Contain(c => c.Name == "Checksum matches" && c.Passed,
            "the bytes really are the ones we stored");
        result.Checks.Should().Contain(c => c.Name == "Archive readable" && !c.Passed,
            "but they are not a usable archive");
    }

    [Fact]
    public async Task An_unreadable_archive_is_refused_at_restore_too()
    {
        using var h = new BackupHarness();
        var backup = await h.SeedUnreadableArtifactAsync();

        var restore = async () => await h.Engine().RestoreAsync(backup.Id, default);

        await restore.Should().ThrowAsync<Exception>();
        h.Docker.Calls.Should().NotContain(c => c.Operation == "RunOneOffAsync");
    }

    [Fact]
    public async Task Verification_leaves_no_decrypted_copy_behind()
    {
        using var h = new BackupHarness();
        var backup = await h.SeedAppConfigBackupAsync();

        await h.Engine().VerifyAsync(backup.Id, default);

        // A dry run that leaves plaintext on disk defeats the point of encrypting archives.
        var decrypted = backup.ArtifactPath![..^Harbora.Infrastructure.Backups.ArchiveCipher.Extension.Length];
        File.Exists(decrypted).Should().BeFalse();
    }

    [Fact]
    public async Task An_incomplete_backup_cannot_be_verified()
    {
        using var h = new BackupHarness();
        var backup = await h.SeedAppConfigBackupAsync();
        backup.Status = BackupStatus.Failed;
        await h.Db.SaveChangesAsync();

        var result = await h.Engine().VerifyAsync(backup.Id, default);

        result.IsRestorable.Should().BeFalse();
        result.Reason.Should().Contain("completed");
    }

    [Fact]
    public async Task An_unknown_backup_reports_not_found()
    {
        using var h = new BackupHarness();

        var result = await h.Engine().VerifyAsync(Guid.NewGuid(), default);

        result.IsRestorable.Should().BeFalse();
        result.Reason.Should().Contain("not found");
    }

    [Fact]
    public async Task An_archive_written_to_the_wrong_volume_is_reported_clearly()
    {
        // Production bug: the helper container mounts the staging volume by NAME while the panel
        // reads it through a mount. Compose prefixed the name with the project directory, so those
        // were two different volumes — tar exited 0 and the archive landed where the panel could
        // never see it. Every volume/database backup failed with a bare "file not found", and a
        // restore would have wiped the target volume before discovering the archive was missing.
        using var h = new BackupHarness();
        var backup = new Harbora.Domain.Backups.Backup
        {
            Id = Guid.NewGuid(), WorkspaceId = h.WorkspaceId, DestinationId = h.Destination.Id,
            Type = BackupType.Volume, TargetRef = "some-volume", Status = BackupStatus.Pending
        };
        h.Db.Backups.Add(backup);
        await h.Db.SaveChangesAsync();

        // The fake reports success without producing a file — exactly what the wrong volume looked like.
        h.Docker.OneOffExitCode = 0;
        await h.Engine().RunAsync(backup.Id, default);

        var stored = await h.Db.Backups.AsNoTracking().FirstAsync(b => b.Id == backup.Id);
        stored.Status.Should().Be(BackupStatus.Failed);
        stored.ErrorMessage.Should().ContainEquivalentOf("same docker volume",
            "the message must name the cause, not just the missing path");
    }

    // ---- the integrity gate in front of restore ----

    [Fact]
    public async Task Restoring_a_corrupted_artifact_is_refused()
    {
        using var h = new BackupHarness();
        var backup = await h.SeedAppConfigBackupAsync();
        h.CorruptArtifact(backup);

        var restore = async () => await h.Engine().RestoreAsync(backup.Id, default);

        (await restore.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*does not match its recorded checksum*")
            .And.Message.Should().Contain("has NOT been touched",
                "the operator needs to know their live data is still intact");
    }

    [Fact]
    public async Task A_corrupted_volume_restore_never_reaches_the_wipe()
    {
        // The decisive case: the volume restore path does `rm -rf /data/*`. If the gate lets a bad
        // archive through, the data is gone before the failure surfaces.
        using var h = new BackupHarness();
        var backup = await h.SeedVolumeBackupAsync();
        h.CorruptArtifact(backup);

        var restore = async () => await h.Engine().RestoreAsync(backup.Id, default);
        await restore.Should().ThrowAsync<InvalidOperationException>();

        h.Docker.Calls.Should().BeEmpty("nothing may run against the volume once the artifact is suspect");
    }

    [Fact]
    public async Task Restoring_a_missing_artifact_is_refused()
    {
        using var h = new BackupHarness();
        var backup = await h.SeedAppConfigBackupAsync();
        h.DeleteArtifact(backup);

        var restore = async () => await h.Engine().RestoreAsync(backup.Id, default);

        await restore.Should().ThrowAsync<InvalidOperationException>();
        h.Docker.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_backup_without_a_recorded_checksum_still_restores()
    {
        // Backups taken before checksums were recorded must not become unrestorable — refusing
        // would strand exactly the oldest backups someone may need most.
        using var h = new BackupHarness();
        var backup = await h.SeedAppConfigBackupAsync();
        backup.Checksum = null;
        h.Db.Apps.Add(new Harbora.Domain.Apps.App
        { Id = Guid.NewGuid(), WorkspaceId = h.WorkspaceId, Name = "Blog", Slug = "blog" });
        await h.Db.SaveChangesAsync();

        var restore = async () => await h.Engine().RestoreAsync(backup.Id, default);

        await restore.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_unencrypted_legacy_artifact_still_restores()
    {
        // Encryption was added after the fact; detection is per file so existing artifacts keep working.
        using var h = new BackupHarness();
        var backup = await h.SeedAppConfigBackupAsync(encrypt: false);
        h.Db.Apps.Add(new Harbora.Domain.Apps.App
        { Id = Guid.NewGuid(), WorkspaceId = h.WorkspaceId, Name = "Blog", Slug = "blog" });
        await h.Db.SaveChangesAsync();

        var result = await h.Engine().VerifyAsync(backup.Id, default);
        result.IsRestorable.Should().BeTrue(result.Reason);

        var restore = async () => await h.Engine().RestoreAsync(backup.Id, default);
        await restore.Should().NotThrowAsync();
    }

    // ---- two backups of one target, at the same time ----

    /// <summary>
    /// The failure this section exists for is an archive that is two moments of the data
    /// interleaved, reported as a successful backup.
    ///
    /// <para>
    /// Jobs run in parallel now. <c>QueueBackupAsync</c> inserts a fresh <c>Backup</c> row and
    /// enqueues against it, so two backups of one target are two different <c>TargetId</c>s and
    /// nothing in the queue holds them apart — the same trap deployments were in, and for the same
    /// reason. Two schedules of one target due on the same tick, or a manual run racing the
    /// scheduler, then run at the same time: two helper containers writing
    /// <c>{type}-{label}-{yyyyMMdd-HHmmss}</c> into the shared staging volume, both checksumming,
    /// both uploading, both recording Completed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_backups_of_one_target_are_queued_so_they_cannot_run_beside_each_other()
    {
        using var h = new BackupHarness();

        await h.Engine().QueueBackupAsync(
            h.WorkspaceId, BackupType.Volume, "uploads", h.Destination.Id, scheduled: true, default);
        await h.Engine().QueueBackupAsync(
            h.WorkspaceId, BackupType.Volume, "uploads", h.Destination.Id, scheduled: false, default);

        h.Jobs.Enqueued.Should().HaveCount(2);
        h.Jobs.Enqueued[0].TargetId.Should().NotBe(h.Jobs.Enqueued[1].TargetId,
            "each run is its own row and its own artifact — that is not the thing being deduplicated");
        h.Jobs.Enqueued[0].ExcludesOn.Should().Be(h.Jobs.Enqueued[1].ExcludesOn,
            "what must never double up is the TARGET, and the queue only knows that if the caller " +
            "says so — a fresh row per run excludes on nothing");
        h.Jobs.Enqueued[0].ExcludesOn.Should().Be(
            BackupRunIdentity.ExclusionKeyFor(BackupType.Volume, "uploads"));
    }

    [Fact]
    public async Task Backups_of_different_targets_are_free_to_run_beside_each_other()
    {
        using var h = new BackupHarness();

        await h.Engine().QueueBackupAsync(
            h.WorkspaceId, BackupType.Volume, "uploads", h.Destination.Id, scheduled: true, default);
        await h.Engine().QueueBackupAsync(
            h.WorkspaceId, BackupType.Volume, "avatars", h.Destination.Id, scheduled: true, default);

        h.Jobs.Enqueued[0].ExcludesOn.Should().NotBe(h.Jobs.Enqueued[1].ExcludesOn,
            "serialising every backup on the platform would undo the parallel worker for this kind");
    }

    /// <summary>
    /// The second guard, and the one that does not depend on a process.
    ///
    /// <para>
    /// The exclusion above is held in memory by the worker running the jobs, so it is a promise
    /// about <i>this</i> panel. The staged filename was the only other thing keeping two runs apart
    /// and it did not: at one-second resolution two runs of one target claimed the same path in the
    /// staging volume. Both helpers then wrote it, and whichever finished second was the file both
    /// backups checksummed, uploaded and recorded — with <c>File.Delete(stagedPath)</c> able to
    /// remove the other run's copy mid-upload.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_runs_of_one_target_in_the_same_second_do_not_claim_the_same_staged_file()
    {
        using var h = new BackupHarness();
        var first = await h.SeedPendingBackupAsync(BackupType.Volume, "uploads");
        var second = await h.SeedPendingBackupAsync(BackupType.Volume, "uploads");

        // The clock does not move between them, which is the whole point: a scheduler tick and a
        // manual run land in the same second all the time.
        await h.Engine().RunAsync(first.Id, default);
        await h.Engine().RunAsync(second.Id, default);

        h.Docker.OneOffCommands.Should().HaveCount(2);
        h.Docker.OneOffCommands[0].Should().NotBe(h.Docker.OneOffCommands[1],
            "two helper containers writing one path in the shared staging volume produce an archive " +
            "that is two moments of the data interleaved, and nothing about it says so");
    }

    [Fact]
    public void A_backup_artifact_still_leads_with_a_sortable_gregorian_stamp()
    {
        var at = new DateTimeOffset(2026, 7, 29, 18, 49, 9, TimeSpan.Zero);

        BackupRunIdentity.StampFor(at, Guid.Parse("0198f2c1-0000-7000-8000-000000000000"))
            .Should().StartWith("20260729-184909-",
                "the time is what makes a directory listing of the staging volume readable, so the " +
                "run's own identity goes after it rather than in front");
    }
}
