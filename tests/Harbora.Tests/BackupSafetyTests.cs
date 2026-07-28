using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Tests.Fakes;
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
}
