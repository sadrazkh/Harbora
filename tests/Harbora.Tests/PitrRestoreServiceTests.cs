using FluentAssertions;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Backups;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 3.1 (round-2 market-gaps plan): restore-to-timestamp. What is proved here — target selection,
/// the recoverable-window gate, the new-database-by-default destination, and typed-name confirmation
/// naming attached apps before an overwrite — is real and runs against a real (in-memory) database.
/// The physical WAL-replay steps inside <see cref="PitrRestoreService"/> run against
/// <see cref="FakeDockerEngine"/> too, which proves their ORDER and that a failure at any step is
/// reported rather than swallowed — it cannot prove a real PostgreSQL recovery, which needs a live
/// host this development machine does not have.
/// </summary>
public sealed class PitrRestoreHarness : IDisposable
{
    public BackupHarness Backup { get; } = new();
    public FixedClock Clock => Backup.Clock;

    public LogicalDatabaseService LogicalDatabases() => new(
        Backup.Db, new PassthroughProtector(), NullLogger<LogicalDatabaseService>.Instance, Clock,
        new DatabaseGrantExecutor(Backup.Engines, new PassthroughProtector(), NullLogger<DatabaseGrantExecutor>.Instance),
        new ManagedServiceEngine(
            Backup.Db, Backup.Engines, new PassthroughProtector(), Backup.Jobs,
            new Harbora.Infrastructure.Billing.BillingGate(
                Backup.Db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions { Enabled = false })),
            Options.Create(Backup.Runtime), Clock, NullLogger<ManagedServiceEngine>.Instance));

    public WalArchivingService WalService() => new(Backup.Db, NullLogger<WalArchivingService>.Instance);

    public PitrRestoreService RestoreService() => new(
        Backup.Db, Backup.Engines, Backup.Storage, new PassthroughProtector(),
        WalService(), LogicalDatabases(), Clock,
        Options.Create(Backup.Options), Options.Create(Backup.Runtime), NullLogger<PitrRestoreService>.Instance)
    { PollInterval = TimeSpan.Zero };

    /// <summary>A completed base backup, with a real (fake-content) file on disk so
    /// <c>storage.GetToLocalAsync</c> has something to hand back.</summary>
    public async Task<Backup> SeedBaseBackupAsync(ManagedService svc, DateTimeOffset finishedAt)
    {
        var path = Path.Combine(Backup.Storage.LocalStagingDir, $"basebackup-{Guid.NewGuid():N}.tar.gz");
        await File.WriteAllTextAsync(path, "fake base backup bytes for a test fixture, never a real one");

        var backup = new Backup
        {
            WorkspaceId = Backup.WorkspaceId, DestinationId = Backup.Destination.Id,
            Type = BackupType.PostgresBaseBackup, TargetRef = svc.Id.ToString(),
            Status = BackupStatus.Completed, ArtifactPath = path, FinishedAt = finishedAt
        };
        Backup.Db.Backups.Add(backup);
        await Backup.Db.SaveChangesAsync();
        return backup;
    }

    public async Task SeedArchivingHealthyAsync(ManagedService svc, DateTimeOffset lastSuccessAt)
    {
        svc.PitrEnabled = true;
        svc.HasUnpublishedChanges = false;
        Backup.Db.WalArchivingStatuses.Add(new WalArchivingStatus
        {
            WorkspaceId = svc.WorkspaceId, ManagedServiceId = svc.Id, LastSuccessAt = lastSuccessAt
        });
        await Backup.Db.SaveChangesAsync();
    }

    public void Dispose() => Backup.Dispose();
}

public class PitrRestoreServiceTests
{
    [Fact]
    public async Task Restoring_with_no_target_creates_a_brand_new_logical_database()
    {
        using var h = new PitrRestoreHarness();
        // FakeDockerEngine never touches a real filesystem, so the recovered instance's own pg_dump
        // one-off needs its "> '/backup/…'" redirect simulated for RecoverAndDumpAsync's own
        // File.Exists check to see anything — see PostgresBaseBackupRunTests.SimulateStagingWrites.
        PostgresBaseBackupRunTests.SimulateStagingWrites(h.Backup);
        var svc = await h.Backup.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        var now = h.Clock.UtcNow;
        await h.SeedBaseBackupAsync(svc, now.AddHours(-6));
        await h.SeedArchivingHealthyAsync(svc, now.AddMinutes(-1));

        var (ok, error, databaseId) = await h.RestoreService().RestoreToTimestampAsync(
            svc.Id, now.AddMinutes(-30), overwriteDatabaseId: null, typedConfirmation: null, default);

        ok.Should().BeTrue(error);
        databaseId.Should().NotBeNull();

        var existingDefault = await h.Backup.Db.ManagedServiceDatabases
            .Where(d => d.ManagedServiceId == svc.Id).ToListAsync();
        existingDefault.Should().Contain(d => d.Id == databaseId!.Value,
            "the restore must land in a database that actually exists now");

        h.Backup.Docker.RunRequests.Should().Contain(r => r.ContainerName.StartsWith("harbora-pitr-"),
            "a scratch recovery instance must have been started");
        h.Backup.Docker.OneOffCommands.Should().Contain(c => c.Contains("tar xzf"),
            "the base backup must be extracted before recovery starts");
    }

    [Fact]
    public async Task Overwriting_an_existing_database_without_the_typed_name_is_refused_and_names_the_attached_apps()
    {
        using var h = new PitrRestoreHarness();
        var svc = await h.Backup.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        var target = await h.Backup.SeedLogicalDatabaseAsync(svc, "orders");
        var now = h.Clock.UtcNow;
        await h.SeedBaseBackupAsync(svc, now.AddHours(-6));
        await h.SeedArchivingHealthyAsync(svc, now.AddMinutes(-1));

        var app = new Harbora.Domain.Apps.App
        {
            Id = Guid.NewGuid(), WorkspaceId = svc.WorkspaceId, EnvironmentId = svc.EnvironmentId,
            ServerId = svc.ServerId, Name = "checkout-api", Slug = "checkout-api"
        };
        h.Backup.Db.Apps.Add(app);
        h.Backup.Db.AppManagedServices.Add(new AppManagedService
        {
            AppId = app.Id, ManagedServiceId = svc.Id, ManagedServiceDatabaseId = target.Id, Alias = "ORDERS"
        });
        await h.Backup.Db.SaveChangesAsync();

        var (ok, error, databaseId) = await h.RestoreService().RestoreToTimestampAsync(
            svc.Id, now.AddMinutes(-30), overwriteDatabaseId: target.Id, typedConfirmation: null, default);

        ok.Should().BeFalse();
        databaseId.Should().BeNull();
        error.Should().Contain("orders").And.Contain("checkout-api",
            "the person restoring may not know what is attached, so the refusal itself must say");
        h.Backup.Docker.Calls.Should().BeEmpty("refused before anything was touched");
    }

    [Fact]
    public async Task Overwriting_with_the_correct_typed_name_proceeds()
    {
        using var h = new PitrRestoreHarness();
        PostgresBaseBackupRunTests.SimulateStagingWrites(h.Backup);
        var svc = await h.Backup.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        var target = await h.Backup.SeedLogicalDatabaseAsync(svc, "orders");
        var now = h.Clock.UtcNow;
        await h.SeedBaseBackupAsync(svc, now.AddHours(-6));
        await h.SeedArchivingHealthyAsync(svc, now.AddMinutes(-1));

        var (ok, error, databaseId) = await h.RestoreService().RestoreToTimestampAsync(
            svc.Id, now.AddMinutes(-30), overwriteDatabaseId: target.Id, typedConfirmation: "orders", default);

        ok.Should().BeTrue(error);
        databaseId.Should().Be(target.Id);
    }

    [Fact]
    public async Task A_non_postgresql_engine_is_refused_by_name()
    {
        using var h = new PitrRestoreHarness();
        var svc = await h.Backup.SeedDatabaseAsync(Guid.NewGuid(), "queue", type: ManagedServiceType.MySql);

        var (ok, error, _) = await h.RestoreService().RestoreToTimestampAsync(
            svc.Id, h.Clock.UtcNow, overwriteDatabaseId: null, typedConfirmation: null, default);

        ok.Should().BeFalse();
        error.Should().Contain("MySql");
        h.Backup.Docker.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_target_time_outside_the_recoverable_window_is_refused_and_names_the_window()
    {
        using var h = new PitrRestoreHarness();
        var svc = await h.Backup.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        var now = h.Clock.UtcNow;
        await h.SeedBaseBackupAsync(svc, now.AddHours(-6));
        await h.SeedArchivingHealthyAsync(svc, now.AddMinutes(-1));

        var (ok, error, _) = await h.RestoreService().RestoreToTimestampAsync(
            svc.Id, now.AddDays(-2), overwriteDatabaseId: null, typedConfirmation: null, default);

        ok.Should().BeFalse();
        error.Should().Contain("outside the recoverable window");
        h.Backup.Docker.Calls.Should().BeEmpty("refused before any docker call");
    }

    [Fact]
    public async Task With_no_base_backup_at_all_the_restore_is_refused()
    {
        using var h = new PitrRestoreHarness();
        var svc = await h.Backup.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        svc.PitrEnabled = true;
        svc.HasUnpublishedChanges = false;
        h.Backup.Db.WalArchivingStatuses.Add(new WalArchivingStatus
        {
            WorkspaceId = svc.WorkspaceId, ManagedServiceId = svc.Id, LastSuccessAt = h.Clock.UtcNow
        });
        await h.Backup.Db.SaveChangesAsync();

        var (ok, error, _) = await h.RestoreService().RestoreToTimestampAsync(
            svc.Id, h.Clock.UtcNow, overwriteDatabaseId: null, typedConfirmation: null, default);

        ok.Should().BeFalse();
        error.Should().Contain("nothing to restore");
    }

    [Fact]
    public async Task A_failed_recovery_step_is_reported_rather_than_reporting_success()
    {
        using var h = new PitrRestoreHarness();
        var svc = await h.Backup.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        var now = h.Clock.UtcNow;
        await h.SeedBaseBackupAsync(svc, now.AddHours(-6));
        await h.SeedArchivingHealthyAsync(svc, now.AddMinutes(-1));
        h.Backup.Docker.OneOffExitCode = 1; // the base-backup extraction step fails

        var (ok, error, databaseId) = await h.RestoreService().RestoreToTimestampAsync(
            svc.Id, now.AddMinutes(-30), overwriteDatabaseId: null, typedConfirmation: null, default);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        databaseId.Should().BeNull();
    }
}
