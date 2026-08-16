using FluentAssertions;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A failed backup opens an <c>AlertIncident</c> the same way a failed deployment does: neither one
/// re-evaluates itself, so the incident stays open until a person acknowledges it or the bounded
/// auto-expiry backstop closes it (2026-08-16 monitoring-alerting spec §M4).
/// </summary>
public class BackupFailedIncidentTests
{
    /// <summary>A v1 node, which by contract runs no one-off containers at all — the same fixture
    /// <c>BackupHostTests</c> uses to force a volume backup to fail.</summary>
    private static NodeWorkloadEngine Node(string nodeId = "web-01") =>
        new(nodeId, null!, null!, null!, NullLogger.Instance);

    [Fact]
    public async Task A_backup_that_fails_opens_an_incident_that_is_not_closed_on_its_own()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        h.Engines.On(serverId, Node("web-01"));
        await h.SeedAppWithVolumeAsync(serverId, "blog-data");
        var backup = await h.SeedPendingBackupAsync(BackupType.Volume, "blog-data");

        await h.Engine().RunAsync(backup.Id, default);

        var stored = await h.Db.Backups.AsNoTracking().FirstAsync(b => b.Id == backup.Id);
        stored.Status.Should().Be(BackupStatus.Failed);

        var incident = h.Db.AlertIncidents.Should().ContainSingle().Subject;
        incident.Condition.Should().Be(AlertEvent.BackupFailed);
        incident.SubjectRef.Should().Be(backup.Id.ToString());
        incident.WorkspaceId.Should().Be(h.WorkspaceId);
        incident.ClosedAt.Should().BeNull("nothing re-evaluates a finished backup run; it stays open until a person or the expiry backstop closes it");
    }
}
