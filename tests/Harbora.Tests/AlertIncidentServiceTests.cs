using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="IncidentService"/> in isolation: opening, refreshing, and the three distinct ways an
/// incident closes (2026-08-16 monitoring-alerting spec §M4).
///
/// <para>
/// The two scenarios that matter most, named directly in the sub-project's own brief: a rule
/// breaching two different conditions opens two independently-closeable incidents (decision 1 — per
/// condition, not per rule), and each of the three closes records a different reason.
/// </para>
/// </summary>
public class AlertIncidentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Workspace = Guid.CreateVersion7();

    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("incidents-" + Guid.NewGuid()).Options);

    [Fact]
    public async Task Opening_a_condition_that_has_never_fired_writes_a_new_open_incident()
    {
        var db = NewDb();
        var incidents = new IncidentService(db);

        await incidents.OpenAsync(Workspace, AlertEvent.DiskWarning, "server-1",
            AlertSeverity.Warning, "Low disk space", "94% used", Now, default);
        await db.SaveChangesAsync();

        var row = db.AlertIncidents.Should().ContainSingle().Subject;
        row.WorkspaceId.Should().Be(Workspace);
        row.Condition.Should().Be(AlertEvent.DiskWarning);
        row.SubjectRef.Should().Be("server-1");
        row.OpenedAt.Should().Be(Now);
        row.ClosedAt.Should().BeNull();
        row.ClosedReason.Should().BeNull();
    }

    [Fact]
    public async Task A_second_breach_of_the_same_condition_and_subject_refreshes_the_open_row_instead_of_opening_a_second()
    {
        var db = NewDb();
        var incidents = new IncidentService(db);

        await incidents.OpenAsync(Workspace, AlertEvent.DiskWarning, "server-1",
            AlertSeverity.Warning, "Low disk space", "90% used", Now, default);
        await db.SaveChangesAsync();

        await incidents.OpenAsync(Workspace, AlertEvent.DiskWarning, "server-1",
            AlertSeverity.Warning, "Low disk space", "96% used", Now.AddMinutes(30), default);
        await db.SaveChangesAsync();

        var row = db.AlertIncidents.Should().ContainSingle("a standing breach is one row for as long as it lasts").Subject;
        row.OpenedAt.Should().Be(Now, "the first observation is when it actually opened");
        row.LastObservedAt.Should().Be(Now.AddMinutes(30));
        row.Body.Should().Be("96% used", "the row carries what was most recently observed");
    }

    /// <summary>
    /// Decision 1, exercised directly against the service: one rule watching two different
    /// conditions on the same subject (an app breaching both its memory threshold and its restart
    /// rate) opens two rows, each closeable on its own.
    /// </summary>
    [Fact]
    public async Task Two_conditions_on_one_app_open_two_incidents_that_close_independently()
    {
        var db = NewDb();
        var incidents = new IncidentService(db);
        var appId = Guid.CreateVersion7().ToString();

        await incidents.OpenAsync(Workspace, AlertEvent.ThresholdBreached, appId,
            AlertSeverity.Warning, "api: memory above 90%", "held for 5m", Now, default);
        await incidents.OpenAsync(Workspace, AlertEvent.AppCrashed, appId,
            AlertSeverity.Critical, "App crashed: api", "exited unexpectedly", Now, default);
        await db.SaveChangesAsync();

        db.AlertIncidents.Count(i => i.SubjectRef == appId).Should().Be(2,
            "the same app breaching two different conditions is two incidents, not one");

        // Resolving the memory threshold must not touch the crash incident on the same app.
        await incidents.ResolveAsync(Workspace, AlertEvent.ThresholdBreached, appId, Now.AddMinutes(10), default);
        await db.SaveChangesAsync();

        var thresholdRow = db.AlertIncidents.Single(i => i.Condition == AlertEvent.ThresholdBreached);
        var crashRow = db.AlertIncidents.Single(i => i.Condition == AlertEvent.AppCrashed);

        thresholdRow.ClosedAt.Should().Be(Now.AddMinutes(10));
        thresholdRow.ClosedReason.Should().Be(IncidentClosedReason.Resolved);
        crashRow.ClosedAt.Should().BeNull("the crash condition on the same app has not cleared");
    }

    [Fact]
    public async Task Resolving_a_condition_with_nothing_open_is_a_quiet_no_op()
    {
        var db = NewDb();
        var incidents = new IncidentService(db);

        await incidents.ResolveAsync(Workspace, AlertEvent.DiskWarning, "server-1", Now, default);
        await db.SaveChangesAsync();

        db.AlertIncidents.Should().BeEmpty();
    }

    // ---- the three closes, and that each records its own reason ----

    [Fact]
    public async Task A_condition_observed_clearing_closes_as_resolved()
    {
        var db = NewDb();
        var incidents = new IncidentService(db);
        await incidents.OpenAsync(Workspace, AlertEvent.AppCrashed, "app-1",
            AlertSeverity.Critical, "App crashed: api", "exited", Now, default);
        await db.SaveChangesAsync();

        await incidents.ResolveAsync(Workspace, AlertEvent.AppCrashed, "app-1", Now.AddMinutes(5), default);
        await db.SaveChangesAsync();

        var row = db.AlertIncidents.Single();
        row.ClosedReason.Should().Be(IncidentClosedReason.Resolved);
        row.ClosedAt.Should().Be(Now.AddMinutes(5));
    }

    [Fact]
    public async Task A_person_closing_it_by_hand_closes_as_acknowledged()
    {
        var db = NewDb();
        var incidents = new IncidentService(db);
        await incidents.OpenAsync(Workspace, AlertEvent.DeployFailed, "deployment-1",
            AlertSeverity.Critical, "Deploy failed: api #4", "build error", Now, default);
        await db.SaveChangesAsync();
        var incidentId = db.AlertIncidents.Single().Id;

        var acknowledged = await incidents.AcknowledgeAsync(Workspace, incidentId, Now.AddHours(1), default);

        acknowledged.Should().BeTrue();
        var row = await db.AlertIncidents.AsNoTracking().SingleAsync(i => i.Id == incidentId);
        row.ClosedReason.Should().Be(IncidentClosedReason.Acknowledged);
        row.ClosedAt.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public async Task Acknowledging_another_workspaces_incident_changes_nothing_and_reports_failure()
    {
        var db = NewDb();
        var incidents = new IncidentService(db);
        await incidents.OpenAsync(Workspace, AlertEvent.BackupFailed, "backup-1",
            AlertSeverity.Warning, "Backup failed", "disk full", Now, default);
        await db.SaveChangesAsync();
        var incidentId = db.AlertIncidents.Single().Id;

        var acknowledged = await incidents.AcknowledgeAsync(Guid.CreateVersion7(), incidentId, Now, default);

        acknowledged.Should().BeFalse();
        db.AlertIncidents.Single().ClosedAt.Should().BeNull();
    }

    [Fact]
    public async Task An_unattended_incident_past_the_bound_closes_as_expired()
    {
        var db = NewDb();
        var incidents = new IncidentService(db);
        await incidents.OpenAsync(Workspace, AlertEvent.BackupFailed, "backup-1",
            AlertSeverity.Warning, "Backup failed", "disk full", Now, default);
        await db.SaveChangesAsync();

        var expiredCount = await incidents.ExpireStaleAsync(Now.AddDays(8), TimeSpan.FromDays(7), default);
        await db.SaveChangesAsync();

        expiredCount.Should().Be(1);
        var row = db.AlertIncidents.Single();
        row.ClosedReason.Should().Be(IncidentClosedReason.Expired);
        row.ClosedAt.Should().Be(Now.AddDays(8));
    }

    [Fact]
    public async Task An_incident_still_inside_the_bound_is_left_open_by_the_expiry_pass()
    {
        var db = NewDb();
        var incidents = new IncidentService(db);
        await incidents.OpenAsync(Workspace, AlertEvent.BackupFailed, "backup-1",
            AlertSeverity.Warning, "Backup failed", "disk full", Now, default);
        await db.SaveChangesAsync();

        var expiredCount = await incidents.ExpireStaleAsync(Now.AddDays(2), TimeSpan.FromDays(7), default);

        expiredCount.Should().Be(0);
        db.AlertIncidents.Single().ClosedAt.Should().BeNull();
    }

    [Fact]
    public async Task Acknowledging_an_incident_whose_condition_is_still_live_does_not_stop_the_next_tick_reopening_it()
    {
        // The documented, intentional behaviour: an ack closes THIS row; if the condition is still
        // breaching, the next observation opens a fresh one rather than being silently swallowed by
        // an incident a person already dismissed.
        var db = NewDb();
        var incidents = new IncidentService(db);
        await incidents.OpenAsync(Workspace, AlertEvent.DiskWarning, "server-1",
            AlertSeverity.Warning, "Low disk space", "94% used", Now, default);
        await db.SaveChangesAsync();
        var firstId = db.AlertIncidents.Single().Id;

        await incidents.AcknowledgeAsync(Workspace, firstId, Now.AddMinutes(1), default);

        await incidents.OpenAsync(Workspace, AlertEvent.DiskWarning, "server-1",
            AlertSeverity.Warning, "Low disk space", "95% used", Now.AddMinutes(31), default);
        await db.SaveChangesAsync();

        db.AlertIncidents.Count().Should().Be(2, "the acknowledged row stays closed; the still-live condition opens a new one");
        db.AlertIncidents.Count(i => i.ClosedAt == null).Should().Be(1);
    }
}
