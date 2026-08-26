using System.Globalization;
using System.IO.Compression;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// D2 (2026-08-25 shared-databases plan): backing up and restoring ONE logical database inside an
/// instance, through the exact machinery <see cref="DatabaseRestoreSafetySnapshotTests"/> already
/// proves the safety-dump ordering for — nothing here is a second backup path, only new ways to name
/// which logical database a run is of.
/// </summary>
public sealed class LogicalDatabaseBackupEngineTests
{
    /// <summary>
    /// <see cref="FakeDockerEngine"/> records a one-off request but writes nothing to disk (the same
    /// fact <c>DatabaseRestoreSafetySnapshotTests.SeedSafetyDumpFileOnDisk</c> works around), so a
    /// test that needs <c>PersistSafetyDumpAsync</c> to find and publish the safety dump has to put
    /// the file there itself — exactly where <c>RestoreDatabaseAsync</c> computes it for a TARGET that
    /// names a specific logical database.
    /// </summary>
    private static void SeedSafetyDumpFileOnDisk(BackupHarness h, string serviceName, string? databaseLabel)
    {
        var stamp = h.Clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = databaseLabel is null
            ? $"pre-restore-{serviceName}-{stamp}.sql.gz"
            : $"pre-restore-{serviceName}-{databaseLabel}-{stamp}.sql.gz";
        var path = Path.Combine(h.Options.StagingDir, name);
        Directory.CreateDirectory(h.Options.StagingDir);
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.Optimal);
        gz.Write("-- a pre-restore dump\n"u8);
    }

    // ---- backup: one logical database, not the instance admin ---------------------------------

    [Fact]
    public async Task Backing_up_a_named_logical_database_uses_its_own_login_and_name_not_the_admins()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), name: "shared-pg");
        var billing = await h.SeedLogicalDatabaseAsync(svc, "billing_db", username: "billing_login");

        h.Docker.OneOffExitCode = 0;
        var backupId = await h.Engine().QueueBackupAsync(
            h.WorkspaceId, BackupType.Database, svc.Id.ToString(), h.Destination.Id, scheduled: false,
            default, billing.Id);
        await h.Engine().RunAsync(backupId, default);

        var command = h.Docker.OneOffCommands.Should().ContainSingle().Subject;
        command.Should().Contain("billing_login", "the dump must connect as the logical database's OWN login");
        command.Should().Contain("billing_db", "the dump must target the logical database's own name");
        command.Should().NotContain($"-U '{svc.Username}'",
            "the instance admin login must never be used for one logical database's dump");

        var stored = h.Db.Backups.Single(b => b.Id == backupId);
        stored.ManagedServiceDatabaseId.Should().Be(billing.Id);
    }

    [Fact]
    public async Task Backing_up_the_instance_with_no_database_named_still_uses_the_admin_login_unchanged()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), name: "solo-pg");

        h.Docker.OneOffExitCode = 0;
        var backupId = await h.Engine().QueueBackupAsync(
            h.WorkspaceId, BackupType.Database, svc.Id.ToString(), h.Destination.Id, scheduled: false, default);
        await h.Engine().RunAsync(backupId, default);

        var command = h.Docker.OneOffCommands.Should().ContainSingle().Subject;
        command.Should().Contain(svc.Username, "a whole-instance backup keeps using the admin login exactly as it always did");
    }

    // ---- retention: one logical database's backups do not prune its neighbours' -----------------

    [Fact]
    public async Task Retention_keeps_each_logical_databases_backups_separate_from_its_neighbours()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), name: "multi-db");
        var billing = await h.SeedLogicalDatabaseAsync(svc, "billing_db");
        var reporting = await h.SeedLogicalDatabaseAsync(svc, "reporting_db");

        h.Db.BackupSchedules.Add(new Harbora.Domain.Backups.BackupSchedule
        {
            WorkspaceId = h.WorkspaceId, DestinationId = h.Destination.Id, Type = BackupType.Database,
            TargetRef = svc.Id.ToString(), ManagedServiceDatabaseId = billing.Id, RetentionCount = 1, IsEnabled = true
        });
        h.Db.SaveChanges();

        // Two backups of "billing_db" and two of "reporting_db" — as if both had been running nightly.
        for (var i = 0; i < 2; i++)
        {
            var b = await h.SeedCompletedDatabaseDumpAsync(svc.Id, billing.Id);
            b.CreatedAt = DateTimeOffset.UtcNow.AddHours(-i);
            var r = await h.SeedCompletedDatabaseDumpAsync(svc.Id, reporting.Id);
            r.CreatedAt = DateTimeOffset.UtcNow.AddHours(-i);
        }
        h.Db.SaveChanges();

        await h.Engine().EnforceRetentionAsync(default);

        h.Db.Backups.Count(b => b.ManagedServiceDatabaseId == billing.Id).Should().Be(1,
            "billing_db has an explicit schedule capping it at 1");
        h.Db.Backups.Count(b => b.ManagedServiceDatabaseId == reporting.Id).Should().Be(2,
            "reporting_db has no schedule of its own, so it keeps the default retention count — " +
            "billing_db's schedule must never prune it");
    }

    // ---- restore into a DIFFERENT logical database on the same instance --------------------------

    [Fact]
    public async Task Restoring_into_a_different_logical_database_dumps_and_loads_the_target_not_the_source()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), name: "clone-pg");
        var source = await h.SeedLogicalDatabaseAsync(svc, "prod_db", username: "prod_login");
        var target = await h.SeedLogicalDatabaseAsync(svc, "staging_db", username: "staging_login");
        var backup = await h.SeedCompletedDatabaseDumpAsync(svc.Id, source.Id);
        SeedSafetyDumpFileOnDisk(h, svc.Name, target.Name);

        h.Docker.OneOffExitCode = 0;

        await h.Engine().RestoreIntoAsync(backup.Id, svc.Id, target.Id, default);

        h.Docker.OneOffCommands.Should().HaveCount(2, "a safety dump of the TARGET, then the restore into it");
        h.Docker.OneOffCommands[0].Should().Contain("pre-restore-",
            "the safety dump must be the first one-off call, exactly as it already is for a same-place restore");
        h.Docker.OneOffCommands[0].Should().Contain("staging_login").And.Contain("staging_db",
            "the safety dump must be OF the target about to be overwritten, never the source");
        h.Docker.OneOffCommands[1].Should().Contain("staging_login").And.Contain("staging_db",
            "the restore itself must load into the target's own login and name");
        h.Docker.OneOffCommands[1].Should().NotContain("prod_login",
            "the source database's login must never appear in the restore command");

        var safety = h.Db.Backups.Single(b => b.Id != backup.Id);
        safety.TargetRef.Should().Be(svc.Id.ToString());
        safety.ManagedServiceDatabaseId.Should().Be(target.Id,
            "the safety snapshot is recorded against what it is a snapshot OF — the target");
    }

    [Fact]
    public async Task Restoring_into_a_brand_new_database_works_the_same_way_a_controller_would_use_it()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), name: "clone-source");
        var source = await h.SeedLogicalDatabaseAsync(svc, "prod_db");
        var backup = await h.SeedCompletedDatabaseDumpAsync(svc.Id, source.Id);

        // What the controller does before calling RestoreIntoAsync for "restore into a new database":
        // create the row first, exactly as LogicalDatabaseService.CreateAsync would after the engine
        // confirmed it — by the time restore runs, "new" and "already existed" look identical to it.
        var fresh = await h.SeedLogicalDatabaseAsync(svc, "staging_clone");

        h.Docker.OneOffExitCode = 0;
        await h.Engine().RestoreIntoAsync(backup.Id, svc.Id, fresh.Id, default);

        h.Docker.OneOffCommands.Should().HaveCount(2);
        h.Docker.OneOffCommands[1].Should().Contain("staging_clone");
    }

    // ---- engine compatibility: refuse by name before anything is touched -------------------------

    [Fact]
    public async Task Restoring_a_postgres_dump_into_a_mysql_instance_refuses_by_name_before_any_docker_call()
    {
        using var h = new BackupHarness();
        var pg = await h.SeedDatabaseAsync(Guid.NewGuid(), name: "pg-source", type: ManagedServiceType.PostgreSql);
        var mysql = await h.SeedDatabaseAsync(Guid.NewGuid(), name: "mysql-target", type: ManagedServiceType.MySql);
        var backup = await h.SeedCompletedDatabaseDumpAsync(pg.Id);

        var restore = async () => await h.Engine().RestoreIntoAsync(backup.Id, mysql.Id, null, default);

        var thrown = await restore.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("PostgreSql").And.Contain("MySql");
        h.Docker.OneOffCommands.Should().BeEmpty(
            "an incompatible-engine restore must be refused before the safety dump or anything else runs");
        h.Db.Backups.Count().Should().Be(1, "no safety snapshot is taken for a restore that never started");
    }

    [Fact]
    public async Task Restoring_a_mysql_dump_into_a_mariadb_instance_is_allowed_as_one_engine_family()
    {
        using var h = new BackupHarness();
        var mysql = await h.SeedDatabaseAsync(Guid.NewGuid(), name: "mysql-source", type: ManagedServiceType.MySql);
        var maria = await h.SeedDatabaseAsync(Guid.NewGuid(), name: "maria-target", type: ManagedServiceType.MariaDb);
        var backup = await h.SeedCompletedDatabaseDumpAsync(mysql.Id);

        h.Docker.OneOffExitCode = 0;
        await h.Engine().RestoreIntoAsync(backup.Id, maria.Id, null, default);

        h.Docker.OneOffCommands.Should().HaveCount(2, "MySQL and MariaDB share a dump format, so this restore proceeds");
    }

    // ---- tenancy: never restore across workspaces --------------------------------------------

    [Fact]
    public async Task Restoring_into_a_managed_service_owned_by_a_different_workspace_is_refused()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), name: "tenant-a-db");
        var backup = await h.SeedCompletedDatabaseDumpAsync(svc.Id);

        // A database that belongs to a completely different workspace than the backup being restored.
        var otherWorkspaceId = Guid.NewGuid();
        h.Db.Workspaces.Add(new Harbora.Domain.Identity.Workspace { Id = otherWorkspaceId, Name = "Other", Slug = "other-" + Guid.NewGuid().ToString("N")[..6] });
        var otherEnv = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.NewGuid(), WorkspaceId = otherWorkspaceId,
            ProjectId = Guid.NewGuid(), Name = "prod", Slug = "prod", IsDefault = true
        };
        h.Db.Projects.Add(new Harbora.Domain.Projects.Project { Id = otherEnv.ProjectId, WorkspaceId = otherWorkspaceId, Name = "shop", Slug = "shop-" + Guid.NewGuid().ToString("N")[..6] });
        h.Db.Environments.Add(otherEnv);
        var otherSvc = new ManagedService
        {
            Id = Guid.NewGuid(), WorkspaceId = otherWorkspaceId, EnvironmentId = otherEnv.Id,
            ServerId = Guid.NewGuid(), Name = "tenant-b-db", Type = ManagedServiceType.PostgreSql,
            Version = "16-alpine", ContainerName = "harbora-tenant-b-db", InternalPort = 5432,
            Username = "harbora", EncryptedPassword = "s3cret", DatabaseName = "tenant_b_db",
            VolumeName = "tenant-b-db-data"
        };
        h.Db.ManagedServices.Add(otherSvc);
        h.Db.SaveChanges();

        var restore = async () => await h.Engine().RestoreIntoAsync(backup.Id, otherSvc.Id, null, default);

        var thrown = await restore.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("different workspace");
        h.Docker.OneOffCommands.Should().BeEmpty("a cross-workspace restore must never reach the engine at all");
    }

    // ---- a redirected restore that fails still names the safety snapshot -------------------------

    [Fact]
    public async Task A_failed_redirected_restore_still_names_the_targets_own_safety_snapshot()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), name: "redirect-fail");
        var source = await h.SeedLogicalDatabaseAsync(svc, "prod_db");
        var target = await h.SeedLogicalDatabaseAsync(svc, "staging_db");
        var backup = await h.SeedCompletedDatabaseDumpAsync(svc.Id, source.Id);
        SeedSafetyDumpFileOnDisk(h, svc.Name, target.Name);

        h.Docker.OneOffExitCodes.Enqueue(0); // safety dump of the target succeeds
        h.Docker.OneOffExitCodes.Enqueue(1); // the restore itself fails

        var restore = async () => await h.Engine().RestoreIntoAsync(backup.Id, svc.Id, target.Id, default);
        var thrown = await restore.Should().ThrowAsync<InvalidOperationException>();

        var safety = h.Db.Backups.Single(b => b.Id != backup.Id);
        safety.ManagedServiceDatabaseId.Should().Be(target.Id);
        thrown.Which.Message.Should().Contain(safety.Id.ToString());
    }
}
