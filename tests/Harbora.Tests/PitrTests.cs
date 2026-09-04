using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Backups;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 3.1 (round-2 market-gaps plan): point-in-time recovery for PostgreSQL. Layered the same way
/// <c>PgVectorTests</c> proves pgvector: <see cref="PitrSupport"/> refuses the wrong engine before
/// anything is built; <see cref="PostgresWalArchivingCommand"/> proves the container command shape;
/// <see cref="PitrRecoveryWindow"/> proves the recoverable-window arithmetic, including the one that
/// matters most — a failing archive must shrink the reported window, never overstate it;
/// <see cref="WalArchivingService"/> proves the instance-level toggle; <c>ManagedServiceEngine</c>
/// proves which command line an actual rebuild runs.
/// </summary>
public class PitrSupportTests
{
    [Theory]
    [InlineData(ManagedServiceType.PostgreSql, true)]
    [InlineData(ManagedServiceType.MySql, false)]
    [InlineData(ManagedServiceType.MariaDb, false)]
    [InlineData(ManagedServiceType.Redis, false)]
    [InlineData(ManagedServiceType.MongoDb, false)]
    [InlineData(ManagedServiceType.RabbitMq, false)]
    [InlineData(ManagedServiceType.Nats, false)]
    [InlineData(ManagedServiceType.Meilisearch, false)]
    public void Only_postgresql_has_a_pitr_story(ManagedServiceType type, bool expected) =>
        PitrSupport.Supports(type).Should().Be(expected);

    [Fact]
    public void The_unsupported_reason_names_the_engine() =>
        PitrSupport.UnsupportedReason(ManagedServiceType.Redis).Should().Contain("Redis");

    [Fact]
    public void MySql_is_told_binlog_pitr_is_a_separate_follow_on() =>
        PitrSupport.UnsupportedReason(ManagedServiceType.MySql).Should().Contain("binlog",
            "MySQL PITR is a named follow-on item, not something this refusal should imply is coming for free");
}

public class PostgresWalArchivingCommandTests
{
    [Fact]
    public void Extending_a_null_command_starts_from_the_bare_postgres_entrypoint()
    {
        var command = PostgresWalArchivingCommand.Extend(null);

        command[0].Should().Be("postgres");
        string.Join(' ', command).Should().Contain("wal_level=replica")
            .And.Contain("archive_mode=on")
            .And.Contain("archive_command=");
    }

    [Fact]
    public void Extending_an_existing_command_appends_rather_than_replaces()
    {
        var tlsCommand = new List<string> { "postgres", "-c", "ssl=on" };

        var command = PostgresWalArchivingCommand.Extend(tlsCommand);

        command.Should().StartWith(tlsCommand, "TLS's own arguments must survive, not be clobbered");
        string.Join(' ', command).Should().Contain("archive_mode=on");
    }

    [Fact]
    public void The_archive_command_never_overwrites_a_segment_that_already_landed()
    {
        var command = PostgresWalArchivingCommand.Extend(null);
        var joined = string.Join(' ', command);

        joined.Should().Contain("test ! -f",
            "a bare `cp` would silently accept a crash-truncated retry overwriting a complete segment");
    }

    [Fact]
    public void The_wal_volume_is_its_own_volume_not_the_data_volume() =>
        PostgresWalArchivingCommand.VolumeNameFor("harbora-svc-shop-data")
            .Should().Be("harbora-svc-shop-data-wal")
            .And.NotBe("harbora-svc-shop-data");
}

public class PitrRecoveryWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Not_enabled_reports_nothing_recoverable()
    {
        var window = PitrRecoveryWindow.Compute(
            pitrEnabled: false, hasUnpublishedChanges: false, oldestRetainedBaseBackupAt: null, archiving: null, now: Now);

        window.Status.Should().Be(PitrStatus.NotConfigured);
        window.HasRecoverableWindow.Should().BeFalse();
    }

    [Fact]
    public void Enabled_but_not_yet_rebuilt_is_never_shown_as_active()
    {
        var window = PitrRecoveryWindow.Compute(
            pitrEnabled: true, hasUnpublishedChanges: true, oldestRetainedBaseBackupAt: null, archiving: null, now: Now);

        window.Status.Should().Be(PitrStatus.PendingRestart,
            "the green badge for a probe that never fired is exactly the defect class this guards against");
        window.HasRecoverableWindow.Should().BeFalse();
    }

    [Fact]
    public void Applied_but_no_base_backup_yet_has_nothing_recoverable()
    {
        var window = PitrRecoveryWindow.Compute(
            pitrEnabled: true, hasUnpublishedChanges: false, oldestRetainedBaseBackupAt: null,
            archiving: new WalArchivingStatus { LastSuccessAt = Now.AddMinutes(-5) }, now: Now);

        window.Status.Should().Be(PitrStatus.NotYetRecoverable);
        window.HasRecoverableWindow.Should().BeFalse();
    }

    [Fact]
    public void Applied_with_a_base_backup_but_nothing_archived_yet_has_nothing_recoverable()
    {
        var window = PitrRecoveryWindow.Compute(
            pitrEnabled: true, hasUnpublishedChanges: false, oldestRetainedBaseBackupAt: Now.AddHours(-1),
            archiving: null, now: Now);

        window.Status.Should().Be(PitrStatus.NotYetRecoverable);
    }

    [Fact]
    public void A_healthy_archive_reports_a_window_up_to_its_last_success_never_up_to_now()
    {
        var lastSuccess = Now.AddMinutes(-2);
        var window = PitrRecoveryWindow.Compute(
            pitrEnabled: true, hasUnpublishedChanges: false, oldestRetainedBaseBackupAt: Now.AddHours(-6),
            archiving: new WalArchivingStatus { LastSuccessAt = lastSuccess, ConsecutiveFailures = 0 }, now: Now);

        window.Status.Should().Be(PitrStatus.Healthy);
        window.HasRecoverableWindow.Should().BeTrue();
        window.EarliestPoint.Should().Be(Now.AddHours(-6));
        window.LatestPoint.Should().Be(lastSuccess, "the window's far edge is the last confirmed success, never DateTimeOffset.UtcNow");
    }

    [Fact]
    public void A_failing_archive_shrinks_the_reported_window_and_says_so()
    {
        var lastSuccess = Now.AddHours(-6);
        var window = PitrRecoveryWindow.Compute(
            pitrEnabled: true, hasUnpublishedChanges: false, oldestRetainedBaseBackupAt: Now.AddHours(-30),
            archiving: new WalArchivingStatus
            {
                LastSuccessAt = lastSuccess, LastAttemptAt = Now.AddMinutes(-1),
                ConsecutiveFailures = 4, LastError = "disk full on the object storage endpoint"
            },
            now: Now);

        window.Status.Should().Be(PitrStatus.Degraded);
        window.HasRecoverableWindow.Should().BeTrue("a degraded window is still a real, restorable window — just one that stopped advancing");
        window.LatestPoint.Should().Be(lastSuccess,
            "the reported latest point must be stuck where archiving last succeeded, six hours behind now — never advanced to hide the failure");
        window.ConsecutiveFailures.Should().Be(4);
        window.Message.Should().Contain("6h").And.Contain("disk full on the object storage endpoint");
    }

    [Fact]
    public void No_recorded_failures_but_stale_beyond_the_threshold_is_still_degraded()
    {
        // A shipper that stopped running entirely (crashed, was never scheduled) never records a
        // failure — there is nobody there to record one. Staleness alone must still catch it.
        var lastSuccess = Now.AddHours(-2);
        var window = PitrRecoveryWindow.Compute(
            pitrEnabled: true, hasUnpublishedChanges: false, oldestRetainedBaseBackupAt: Now.AddDays(-1),
            archiving: new WalArchivingStatus { LastSuccessAt = lastSuccess, ConsecutiveFailures = 0 }, now: Now);

        window.Status.Should().Be(PitrStatus.Degraded);
    }

    [Fact]
    public void A_base_backup_newer_than_the_latest_shipped_wal_collapses_to_a_zero_width_window_rather_than_lying()
    {
        var lastSuccess = Now.AddHours(-1);
        var window = PitrRecoveryWindow.Compute(
            pitrEnabled: true, hasUnpublishedChanges: false,
            oldestRetainedBaseBackupAt: Now, // taken AFTER the last successful WAL ship
            archiving: new WalArchivingStatus { LastSuccessAt = lastSuccess, ConsecutiveFailures = 0 }, now: Now);

        window.EarliestPoint.Should().Be(window.LatestPoint,
            "there is no WAL shipped yet that reaches this base backup's own start point, so the honest window is a single instant, not a range implying more coverage than exists");
    }
}

/// <summary>The instance-level toggle. Neither test reaches Docker — the toggle only reads/writes
/// rows, exactly like <c>SetPgVectorEnabledAsync</c>'s own tests.</summary>
public class WalArchivingServiceTests
{
    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("pitr-toggle-" + Guid.NewGuid()).Options);

    private static ManagedService SeedInstance(HarboraDbContext db, ManagedServiceType type = ManagedServiceType.PostgreSql)
    {
        var instance = new ManagedService
        {
            Id = Guid.CreateVersion7(), WorkspaceId = Guid.CreateVersion7(), ServerId = Guid.Empty,
            Name = "shop-db", Type = type, ContainerName = "harbora-svc-shop", DatabaseName = "shop",
            Username = "harbora", InternalPort = 5432, VolumeName = "shop-data"
        };
        db.Add(instance);
        db.SaveChanges();
        return instance;
    }

    [Fact]
    public async Task Enabling_it_marks_the_instance_unpublished_until_the_next_rebuild()
    {
        var db = NewDb();
        var instance = SeedInstance(db);
        var service = new WalArchivingService(db, NullLogger<WalArchivingService>.Instance);

        var error = await service.SetEnabledAsync(instance.Id, true, default);

        error.Should().BeNull();
        var stored = await db.ManagedServices.AsNoTracking().SingleAsync(s => s.Id == instance.Id);
        stored.PitrEnabled.Should().BeTrue();
        stored.HasUnpublishedChanges.Should().BeTrue("saved, but only a rebuild makes archiving real");
    }

    [Fact]
    public async Task A_non_postgresql_engine_is_refused_by_name_and_nothing_is_changed()
    {
        var db = NewDb();
        var instance = SeedInstance(db, ManagedServiceType.MySql);
        var service = new WalArchivingService(db, NullLogger<WalArchivingService>.Instance);

        var error = await service.SetEnabledAsync(instance.Id, true, default);

        error.Should().Contain("MySql");
        var stored = await db.ManagedServices.AsNoTracking().SingleAsync(s => s.Id == instance.Id);
        stored.PitrEnabled.Should().BeFalse();
        stored.HasUnpublishedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Turning_it_off_is_never_refused_existing_archives_stay_exactly_as_restorable()
    {
        var db = NewDb();
        var instance = SeedInstance(db);
        instance.PitrEnabled = true;
        instance.HasUnpublishedChanges = false;
        await db.SaveChangesAsync();
        var service = new WalArchivingService(db, NullLogger<WalArchivingService>.Instance);

        var error = await service.SetEnabledAsync(instance.Id, false, default);

        error.Should().BeNull();
        var stored = await db.ManagedServices.AsNoTracking().SingleAsync(s => s.Id == instance.Id);
        stored.PitrEnabled.Should().BeFalse();
        stored.HasUnpublishedChanges.Should().BeTrue();
    }

    [Fact]
    public async Task The_recovery_window_reads_back_what_was_just_computed_for_a_healthy_instance()
    {
        var db = NewDb();
        var instance = SeedInstance(db);
        instance.PitrEnabled = true;
        instance.HasUnpublishedChanges = false;
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        db.Backups.Add(new Backup
        {
            WorkspaceId = instance.WorkspaceId, DestinationId = Guid.NewGuid(), Type = BackupType.PostgresBaseBackup,
            TargetRef = instance.Id.ToString(), Status = BackupStatus.Completed, FinishedAt = now.AddHours(-6)
        });
        db.WalArchivingStatuses.Add(new WalArchivingStatus
        {
            WorkspaceId = instance.WorkspaceId, ManagedServiceId = instance.Id, LastSuccessAt = now.AddMinutes(-1)
        });
        await db.SaveChangesAsync();
        var service = new WalArchivingService(db, NullLogger<WalArchivingService>.Instance);

        var window = await service.RecoveryWindowAsync(instance.Id, now, default);

        window.Status.Should().Be(PitrStatus.Healthy);
        window.EarliestPoint.Should().Be(now.AddHours(-6));
    }
}

/// <summary>Proves which command line an actual rebuild runs — the only place PitrEnabled turns into
/// a real container command, the same shape <c>PgVectorProvisionTests</c> already proves for the
/// image swap. Reuses <c>PgEngineHarness</c> rather than a parallel harness.</summary>
public class PitrProvisionTests
{
    [Fact]
    public async Task An_instance_with_pitr_enabled_is_rebuilt_with_wal_archiving_arguments()
    {
        using var h = new PgEngineHarness();
        var svc = await h.SeedAsync();
        svc.PitrEnabled = true;
        await h.SaveAsync();

        await h.Engine().ProvisionAsync(svc.Id, default);

        var request = h.Docker.RunRequests.Should().ContainSingle(r => r.ContainerName == svc.ContainerName).Subject;
        request.Command.Should().NotBeNull();
        string.Join(' ', request.Command!).Should().Contain("archive_mode=on");
        request.Volumes.Should().Contain(v => v.MountPath == PostgresWalArchivingCommand.ArchiveMountPath);
    }

    [Fact]
    public async Task An_instance_that_never_asked_for_pitr_gets_the_plain_command_line()
    {
        using var h = new PgEngineHarness();
        var svc = await h.SeedAsync();

        await h.Engine().ProvisionAsync(svc.Id, default);

        var request = h.Docker.RunRequests.Should().ContainSingle(r => r.ContainerName == svc.ContainerName).Subject;
        if (request.Command is not null)
            string.Join(' ', request.Command).Should().NotContain("archive_mode",
                "an instance nobody asked for PITR on must not be silently switched to archiving");
    }

    [Fact]
    public async Task Enabling_pitr_marks_the_instance_unpublished_until_the_next_rebuild_clears_it()
    {
        using var h = new PgEngineHarness();
        var svc = await h.SeedAsync();
        svc.PitrEnabled = true;
        svc.HasUnpublishedChanges = true;
        await h.SaveAsync();

        var before = await h.ReadServiceAsync(svc.Id);
        before.HasUnpublishedChanges.Should().BeTrue();

        await h.Engine().ProvisionAsync(svc.Id, default);

        var after = await h.ReadServiceAsync(svc.Id);
        after.HasUnpublishedChanges.Should().BeFalse(
            "the container was just rebuilt from this row's own settings, so archiving is no longer merely requested");
    }
}
