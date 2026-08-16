using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Monitoring;
using Harbora.Tests.Fakes;
using Harbora.Web.Controllers;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The monitoring dashboard's own "backups look stale" banner (<c>MonitoringController.Index</c>),
/// which used to compare against a hardcoded 48 hours and now reads
/// <see cref="MonitoringOptions.BackupStalenessHours"/>.
///
/// <para>
/// This is deliberately NOT the same figure as <c>VerificationSchedule.StaleAfter</c> (7 days — when a
/// stored restore verdict needs re-checking by an actual restore) or
/// <c>StorageMeasurer.StaleAfter</c> (24 hours — when a volume's measured size needs remeasuring for
/// quota/billing). Those two stay constants; only this dashboard figure is configurable.
/// </para>
/// </summary>
public class MonitoringControllerBackupStalenessTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();

    // MonitoringController.Index compares against the real DateTimeOffset.UtcNow directly — no clock
    // is injected — so every scenario below anchors to it rather than to a fixed instant.

    private sealed class StubUser : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.CreateVersion7();
        public string? Email => "ops@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId => Workspace;
    }

    private sealed class SilentAudit : IAuditLogger
    {
        public Task LogAsync(string action, string? targetType = null, string? targetId = null,
            string? ipAddress = null, string? actorEmailOverride = null, Guid? userIdOverride = null,
            string? metadataJson = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static MonitoringController NewController(HarboraDbContext db, MonitoringOptions? options)
    {
        var engineFactory = new FakeServerEngineFactory(new FakeDockerEngine());
        var access = new Harbora.Infrastructure.Security.ProjectAccessService(db, new StubUser());
        var cleanup = new Harbora.Infrastructure.Maintenance.DiskCleanupService(
            db, engineFactory, Options.Create(new HarboraRuntimeOptions()),
            NullLogger<Harbora.Infrastructure.Maintenance.DiskCleanupService>.Instance);

        return new MonitoringController(
            db, new FakeDockerEngine(), new StubUser(), access, cleanup, new SilentAudit(),
            Options.Create(options ?? new MonitoringOptions()), NullLogger<MonitoringController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("mon-backup-staleness-" + Guid.NewGuid()).Options);

    private static void SeedCompletedBackup(HarboraDbContext db, DateTimeOffset finishedAt) => db.Backups.Add(new Backup
    {
        WorkspaceId = Workspace, Status = BackupStatus.Completed, FinishedAt = finishedAt, CreatedAt = finishedAt
    });

    /// <summary>
    /// Runs <c>Index</c> under a chosen UI culture and restores whatever was there before, so one
    /// test's Persian does not leak into the next test picked up by the same pooled thread.
    /// </summary>
    private static async Task<MonitoringDashboardViewModel> RunIndexAsync(
        HarboraDbContext db, MonitoringOptions? options = null, string culture = "en")
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);
        try
        {
            var controller = NewController(db, options);
            var result = await controller.Index(default);
            return result.Should().BeOfType<ViewResult>().Subject.Model.Should()
                .BeOfType<MonitoringDashboardViewModel>().Subject;
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public async Task A_backup_30_hours_old_warns_once_staleness_is_configured_down_to_24_hours()
    {
        var db = NewDb();
        SeedCompletedBackup(db, DateTimeOffset.UtcNow.AddHours(-30));
        db.SaveChanges();

        var vm = await RunIndexAsync(db, new MonitoringOptions { BackupStalenessHours = 24 });

        vm.BackupWarning.Should().BeTrue("30 hours is past the configured 24-hour staleness window");
    }

    [Fact]
    public async Task The_shipped_default_staleness_leaves_the_same_30_hour_old_backup_unremarked()
    {
        var db = NewDb();
        SeedCompletedBackup(db, DateTimeOffset.UtcNow.AddHours(-30));
        db.SaveChanges();

        var vm = await RunIndexAsync(db); // default 48 hours

        vm.BackupWarning.Should().BeFalse("30 hours is under the shipped 48-hour default");
    }

    [Fact]
    public async Task A_backup_60_hours_old_stops_warning_once_staleness_is_configured_up_to_72_hours()
    {
        var db = NewDb();
        SeedCompletedBackup(db, DateTimeOffset.UtcNow.AddHours(-60));
        db.SaveChanges();

        var vm = await RunIndexAsync(db, new MonitoringOptions { BackupStalenessHours = 72 });

        vm.BackupWarning.Should().BeFalse("60 hours is under the configured 72-hour window, though over the shipped default");
    }

    [Fact]
    public async Task The_shipped_default_staleness_still_warns_about_the_same_60_hour_old_backup()
    {
        var db = NewDb();
        SeedCompletedBackup(db, DateTimeOffset.UtcNow.AddHours(-60));
        db.SaveChanges();

        var vm = await RunIndexAsync(db); // default 48 hours

        vm.BackupWarning.Should().BeTrue("60 hours is past the shipped 48-hour default");
    }

    [Fact]
    public async Task The_warning_names_the_configured_hour_count_in_english()
    {
        var db = NewDb();
        SeedCompletedBackup(db, DateTimeOffset.UtcNow.AddHours(-30));
        db.SaveChanges();

        var vm = await RunIndexAsync(db, new MonitoringOptions { BackupStalenessHours = 24 }, culture: "en");

        vm.BackupWarningText.Should().Contain("24", "an operator who configured 24 hours should see 24, not the old 48");
    }

    [Fact]
    public async Task The_warning_names_the_configured_hour_count_in_persian()
    {
        // The panel renders Persian by default; the banner must not silently fall back to English
        // once the hour count became a variable instead of a literal string.
        var db = NewDb();
        SeedCompletedBackup(db, DateTimeOffset.UtcNow.AddHours(-30));
        db.SaveChanges();

        var vm = await RunIndexAsync(db, new MonitoringOptions { BackupStalenessHours = 24 }, culture: "fa");

        vm.BackupWarningText.Should().Contain("24");
        vm.BackupWarningText.Should().Contain("ساعت", "a Persian reader must get a Persian sentence, not an English one");
    }

    [Fact]
    public async Task The_configured_ratio_also_sets_the_view_models_disk_warn_ratio()
    {
        // MonitoringDashboardViewModel.DiskWarning reads DiskWarnRatio, not a literal 0.85 — the
        // monitoring page's own banner has to agree with the alert once an operator configures it.
        var db = NewDb();

        var vm = await RunIndexAsync(db, new MonitoringOptions { DiskWarnRatio = 0.6 });

        vm.DiskWarnRatio.Should().Be(0.6);
    }
}
