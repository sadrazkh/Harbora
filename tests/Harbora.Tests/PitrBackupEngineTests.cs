using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 3.1 (round-2 market-gaps plan): the base backup reuses <see cref="Harbora.Infrastructure.Backups.BackupEngine"/>'s
/// own scheduling/destinations/retention/delivery rather than forking a second engine — proved here
/// the same way <c>BackupDatabaseAsync</c>'s own tests prove pg_dump reaches the fake engine.
/// </summary>
public class PostgresBaseBackupRunTests
{
    /// <summary>
    /// <see cref="FakeDockerEngine"/> never touches a real filesystem — a helper container's own
    /// <c>&gt; '/backup/…'</c> redirect is simulated here so the surrounding pipeline
    /// (checksum/encrypt/store/retain) can be proved for real rather than only asserting the command
    /// shape was issued. Generic over which command ran; every PITR test in this file that needs the
    /// artifact to actually land uses it.
    /// </summary>
    internal static void SimulateStagingWrites(BackupHarness h) => h.Docker.OnOneOff = request =>
    {
        var joined = string.Join(' ', request.Command);
        var match = System.Text.RegularExpressions.Regex.Match(joined, @">\s*'?/backup/([^'\s]+)'?");
        if (!match.Success) return;
        var localPath = Path.Combine(h.Storage.LocalStagingDir, match.Groups[1].Value);
        using var fs = File.Create(localPath);
        using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest);
        gz.Write(System.Text.Encoding.UTF8.GetBytes("fake bytes for a test fixture, never a real pg_basebackup/pg_dump output\n"));
    };

    [Fact]
    public async Task A_base_backup_runs_pg_basebackup_through_the_ordinary_backup_pipeline()
    {
        using var h = new BackupHarness();
        SimulateStagingWrites(h);
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        var backup = await h.SeedPendingBackupAsync(BackupType.PostgresBaseBackup, svc.Id.ToString());

        await h.Engine().RunAsync(backup.Id, default);

        var stored = await h.Db.Backups.FindAsync(backup.Id);
        stored!.Status.Should().Be(BackupStatus.Completed, stored.ErrorMessage);
        stored.ArtifactPath.Should().NotBeNullOrEmpty();
        stored.Checksum.Should().NotBeNullOrEmpty("the same encryption/checksum pipeline every backup type already goes through");
        h.Docker.OneOffCommands.Should().ContainSingle(c => c.Contains("pg_basebackup"));
    }

    [Fact]
    public async Task A_non_postgresql_engine_is_refused_by_name_before_any_docker_call()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), "queue", type: ManagedServiceType.RabbitMq);
        var backup = await h.SeedPendingBackupAsync(BackupType.PostgresBaseBackup, svc.Id.ToString());

        await h.Engine().RunAsync(backup.Id, default);

        var stored = await h.Db.Backups.FindAsync(backup.Id);
        stored!.Status.Should().Be(BackupStatus.Failed);
        stored.ErrorMessage.Should().Contain("RabbitMq");
        h.Docker.Calls.Should().BeEmpty("an unsupported engine must never be asked to run a base backup");
    }

    [Fact]
    public async Task A_base_backup_cannot_be_restored_on_its_own()
    {
        // Seeded directly rather than run through RunAsync: what matters here is a Completed
        // PostgresBaseBackup row with a real artifact on disk, not how it got that way, and seeding
        // it keeps h.Docker.Calls empty going in — the same reason BackupHarness's own
        // SeedCompletedDatabaseDumpAsync seeds rather than runs for its own restore tests.
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        var path = Path.Combine(h.Storage.LocalStagingDir, $"basebackup-{Guid.NewGuid():N}.tar.gz");
        await File.WriteAllTextAsync(path, "fake base backup bytes for a test fixture, never a real one");
        var completed = new Harbora.Domain.Backups.Backup
        {
            WorkspaceId = h.WorkspaceId, DestinationId = h.Destination.Id, Type = BackupType.PostgresBaseBackup,
            TargetRef = svc.Id.ToString(), Status = BackupStatus.Completed, ArtifactPath = path,
            FinishedAt = h.Clock.UtcNow
        };
        h.Db.Backups.Add(completed);
        await h.Db.SaveChangesAsync();

        var act = () => h.Engine().RestoreAsync(completed.Id, default);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*point in time*");
        h.Docker.Calls.Should().BeEmpty("refused before any docker call — nothing was read, nothing was written");
    }

    [Fact]
    public async Task A_base_backup_taken_on_another_server_is_refused_the_same_way_a_database_backup_already_is()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        h.ServerAt(serverId, "web-02");
        var svc = await h.SeedDatabaseAsync(serverId, "shop");
        var backup = await h.SeedPendingBackupAsync(BackupType.PostgresBaseBackup, svc.Id.ToString());

        await h.Engine().RunAsync(backup.Id, default);

        var stored = await h.Db.Backups.FindAsync(backup.Id);
        stored!.Status.Should().Be(BackupStatus.Failed);
        stored.ErrorMessage.Should().Contain("web-02");
    }
}

/// <summary>WAL retention prunes without ever orphaning a base backup still on file — the task's own
/// requirement, proved directly against <see cref="Harbora.Infrastructure.Backups.BackupEngine.EnforceRetentionAsync"/>.</summary>
public class WalRetentionTests
{
    [Fact]
    public async Task Wal_segments_older_than_the_oldest_retained_base_backup_are_pruned()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        var now = h.Clock.UtcNow;

        // Two base backups; retention (default count) keeps both, so the floor is the OLDER one.
        h.Db.Backups.Add(new Harbora.Domain.Backups.Backup
        {
            WorkspaceId = h.WorkspaceId, DestinationId = h.Destination.Id, Type = BackupType.PostgresBaseBackup,
            TargetRef = svc.Id.ToString(), Status = BackupStatus.Completed, FinishedAt = now.AddDays(-2)
        });
        h.Db.Backups.Add(new Harbora.Domain.Backups.Backup
        {
            WorkspaceId = h.WorkspaceId, DestinationId = h.Destination.Id, Type = BackupType.PostgresBaseBackup,
            TargetRef = svc.Id.ToString(), Status = BackupStatus.Completed, FinishedAt = now.AddDays(-1)
        });

        var before = new Harbora.Domain.Backups.WalSegment
        {
            WorkspaceId = h.WorkspaceId, ManagedServiceId = svc.Id, DestinationId = h.Destination.Id,
            FileName = "segment-a", ArchivedAt = now.AddDays(-3), ArtifactPath = WriteFakeSegment(h, "segment-a")
        };
        var afterFloor = new Harbora.Domain.Backups.WalSegment
        {
            WorkspaceId = h.WorkspaceId, ManagedServiceId = svc.Id, DestinationId = h.Destination.Id,
            FileName = "segment-b", ArchivedAt = now.AddHours(-1), ArtifactPath = WriteFakeSegment(h, "segment-b")
        };
        h.Db.WalSegments.AddRange(before, afterFloor);
        await h.Db.SaveChangesAsync();

        await h.Engine().EnforceRetentionAsync(default);

        var remaining = h.Db.WalSegments.Select(w => w.FileName).ToList();
        remaining.Should().NotContain("segment-a", "older than the oldest base backup still kept — unreachable, safe to prune");
        remaining.Should().Contain("segment-b", "needed to replay forward from the retained base backup");
    }

    [Fact]
    public async Task No_base_backup_on_file_yet_prunes_nothing()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        var segment = new Harbora.Domain.Backups.WalSegment
        {
            WorkspaceId = h.WorkspaceId, ManagedServiceId = svc.Id, DestinationId = h.Destination.Id,
            FileName = "segment-a", ArchivedAt = h.Clock.UtcNow.AddDays(-30), ArtifactPath = WriteFakeSegment(h, "segment-a")
        };
        h.Db.WalSegments.Add(segment);
        await h.Db.SaveChangesAsync();

        await h.Engine().EnforceRetentionAsync(default);

        h.Db.WalSegments.Select(w => w.FileName).Should().Contain("segment-a",
            "nothing safely anchors a prune yet — pruning here would strand the segment before any base backup exists to replay it onto");
    }

    private static string WriteFakeSegment(BackupHarness h, string name)
    {
        var path = Path.Combine(h.Storage.LocalStagingDir, name);
        File.WriteAllText(path, "fake wal bytes for a test fixture, never a real segment");
        return path;
    }
}
