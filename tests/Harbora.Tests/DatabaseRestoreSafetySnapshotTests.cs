using System.Globalization;
using System.IO.Compression;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project 10: the safety-snapshot-before-restore ordering a self-serve import relies on.
///
/// <para>
/// Nothing here is new machinery for sub-project 10 — <c>BackupEngine.RestoreDatabaseAsync</c> already
/// took a pre-restore dump before loading any database backup, admin-triggered or self-serve alike.
/// What these tests pin down is the part that used to be missing: the dump now survives as an ordinary
/// completed <see cref="Harbora.Domain.Backups.Backup"/> row rather than a bare file in staging, so a
/// failed import can point at something restorable instead of saying "Import failed" with no way back.
/// </para>
/// </summary>
public sealed class DatabaseRestoreSafetySnapshotTests
{
    /// <summary>
    /// FakeDockerEngine records a one-off request but writes nothing to disk (see
    /// BackupDatabaseNetworkTests' own comment on the same fact), so a test that needs the safety
    /// dump to be found and published has to put the file there itself — exactly where
    /// RestoreDatabaseAsync computes it, under the exact name it would have written.
    /// </summary>
    private static void SeedSafetyDumpFileOnDisk(BackupHarness h, string serviceName)
    {
        var stamp = h.Clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(h.Options.StagingDir, $"pre-restore-{serviceName}-{stamp}.sql.gz");
        Directory.CreateDirectory(h.Options.StagingDir);
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.Optimal);
        gz.Write("-- a pre-restore dump\n"u8);
    }

    [Fact]
    public async Task The_safety_dump_runs_before_the_restore_is_even_attempted()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid());
        var uploaded = await h.SeedCompletedDatabaseDumpAsync(svc.Id);

        h.Docker.OneOffExitCode = 0; // both the safety dump and the restore succeed

        await h.Engine().RestoreAsync(uploaded.Id, default);

        h.Docker.OneOffCommands.Should().HaveCount(2, "the safety dump, then the restore");
        h.Docker.OneOffCommands[0].Should().Contain("pre-restore-",
            "the safety dump must be the FIRST one-off run, not merely run at some point");
        h.Docker.OneOffCommands[1].Should().NotContain("pre-restore-",
            "the second call is the restore of the uploaded artifact, not another safety copy");
    }

    [Fact]
    public async Task A_successful_safety_dump_is_recorded_as_an_ordinary_completed_backup()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid());
        var uploaded = await h.SeedCompletedDatabaseDumpAsync(svc.Id);
        SeedSafetyDumpFileOnDisk(h, svc.Name);

        h.Docker.OneOffExitCode = 0;

        await h.Engine().RestoreAsync(uploaded.Id, default);

        // Before this, the safety dump was a file left in staging and named only in a sentence —
        // nothing on the Backups page could show it, verify it, or restore from it. It must now be a
        // real row: same workspace, same target, completed, distinguishable from an ordinary backup.
        var safety = await h.Db.Backups.SingleAsync(b => b.Id != uploaded.Id);
        safety.WorkspaceId.Should().Be(uploaded.WorkspaceId);
        safety.TargetRef.Should().Be(uploaded.TargetRef);
        safety.Type.Should().Be(BackupType.Database);
        safety.Status.Should().Be(BackupStatus.Completed);
        safety.ArtifactPath.Should().NotBeNullOrWhiteSpace();
        safety.Checksum.Should().NotBeNullOrWhiteSpace();
        safety.VerificationNote.Should().ContainEquivalentOf("safety");
    }

    [Fact]
    public async Task A_failed_restore_names_the_safety_backup_that_can_be_restored_from()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid());
        var uploaded = await h.SeedCompletedDatabaseDumpAsync(svc.Id);
        SeedSafetyDumpFileOnDisk(h, svc.Name);

        // The safety dump succeeds; the restore of the uploaded artifact then fails. This is exactly
        // the case the brief called out: "Import failed" with no way back is the worst outcome, so
        // the failure has to name which snapshot to restore from.
        h.Docker.OneOffExitCodes.Enqueue(0);
        h.Docker.OneOffExitCodes.Enqueue(1);

        var restore = async () => await h.Engine().RestoreAsync(uploaded.Id, default);
        var thrown = await restore.Should().ThrowAsync<InvalidOperationException>();

        var safety = await h.Db.Backups.SingleAsync(b => b.Id != uploaded.Id);
        safety.Status.Should().Be(BackupStatus.Completed,
            "the safety dump itself succeeded — only the restore of the uploaded artifact failed");
        thrown.Which.Message.Should().Contain(safety.Id.ToString(),
            "the failure must name the specific backup a customer can restore from, not just a raw filename");
    }

    [Fact]
    public async Task A_safety_dump_that_itself_fails_stops_the_restore_before_anything_is_touched()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid());
        var uploaded = await h.SeedCompletedDatabaseDumpAsync(svc.Id);

        h.Docker.OneOffExitCode = 1; // the safety dump itself fails

        var restore = async () => await h.Engine().RestoreAsync(uploaded.Id, default);
        await restore.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*there would have been nothing to go back to*");

        h.Docker.OneOffCommands.Should().HaveCount(1,
            "the restore command must never run when the safety dump it depends on did not succeed");
        h.Db.Backups.Count().Should().Be(1, "only the pre-seeded upload — no safety row for a dump that failed");
    }
}
