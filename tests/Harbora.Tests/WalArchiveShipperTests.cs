using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Backups;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 3.1 (round-2 market-gaps plan): the shipper that actually moves WAL segments into object storage
/// and is the ONLY writer of <see cref="WalArchivingStatus.LastSuccessAt"/> — the fact
/// <c>PitrRecoveryWindow</c> reads back as "how far can you actually recover to, right now".
///
/// <para>
/// Registered the same way <c>DataRetentionSweeperTests</c> proves its own sweeper: the shipper
/// resolves its dependencies through <see cref="IServiceScopeFactory"/> exactly as it does in
/// production, so every dependency here is registered as a singleton instance the test still holds.
/// </para>
/// </summary>
public class WalArchiveShipperTests
{
    private static WalArchiveShipper NewShipper(BackupHarness h)
    {
        var services = new ServiceCollection();
        services.AddSingleton(h.Db);
        services.AddSingleton<IServerEngineFactory>(h.Engines);
        services.AddSingleton<IBackupStorage>(h.Storage);
        services.AddSingleton<ISystemClock>(h.Clock);
        services.AddSingleton(Options.Create(h.Options));
        var provider = services.BuildServiceProvider();

        return new WalArchiveShipper(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<WalArchiveShipper>.Instance);
    }

    [Fact]
    public async Task Ships_new_segments_and_records_a_success()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        svc.PitrEnabled = true;
        svc.HasUnpublishedChanges = false;
        await h.Db.SaveChangesAsync();

        // "ls -1 /wal_archive" inside the fake reports back through OneOffOutput, exactly the shape
        // the real archive volume would answer with.
        h.Docker.OneOffOutput.Add("00000001000000000000000A\n00000001000000000000000B\n");

        // FakeDockerEngine never touches a real filesystem — the "cp <archive>/%f /backup/%f" step
        // that stages a segment for storage.PutFileAsync needs its own effect simulated, the "cp"
        // counterpart of PostgresBaseBackupRunTests.SimulateStagingWrites' shell-redirect version.
        h.Docker.OnOneOff = request =>
        {
            if (request.Command is not ["cp", _, var destination] || !destination.StartsWith("/backup/", StringComparison.Ordinal))
                return;
            File.WriteAllText(Path.Combine(h.Storage.LocalStagingDir, destination["/backup/".Length..]), "fake wal bytes");
        };

        await NewShipper(h).ShipDueInstancesAsync(default);

        var status = await h.Db.WalArchivingStatuses.SingleAsync(w => w.ManagedServiceId == svc.Id);
        status.LastSuccessAt.Should().NotBeNull();
        status.ConsecutiveFailures.Should().Be(0);
        status.SegmentsArchived.Should().Be(2);

        var segments = await h.Db.WalSegments.Where(w => w.ManagedServiceId == svc.Id).ToListAsync();
        segments.Should().HaveCount(2);
        h.Docker.OneOffCommands.Should().Contain(c => c.Contains("rm -f"), "shipped segments must be pruned from the volume");
    }

    [Fact]
    public async Task A_failing_list_leaves_LastSuccessAt_untouched_and_records_the_failure()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        svc.PitrEnabled = true;
        svc.HasUnpublishedChanges = false;
        var earlierSuccess = h.Clock.UtcNow.AddHours(-3);
        h.Db.WalArchivingStatuses.Add(new WalArchivingStatus
        {
            WorkspaceId = svc.WorkspaceId, ManagedServiceId = svc.Id, LastSuccessAt = earlierSuccess
        });
        await h.Db.SaveChangesAsync();
        h.Docker.OneOffExitCode = 1;

        await NewShipper(h).ShipDueInstancesAsync(default);

        var status = await h.Db.WalArchivingStatuses.SingleAsync(w => w.ManagedServiceId == svc.Id);
        status.LastSuccessAt.Should().Be(earlierSuccess,
            "a failing run must never advance the last-known-good point — that is what keeps the reported recoverable window honest");
        status.ConsecutiveFailures.Should().Be(1);
        status.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_service_not_enabled_for_pitr_is_never_touched()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), "shop"); // PitrEnabled stays false

        await NewShipper(h).ShipDueInstancesAsync(default);

        h.Docker.Calls.Should().BeEmpty();
        (await h.Db.WalArchivingStatuses.AnyAsync(w => w.ManagedServiceId == svc.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task An_instance_pending_a_rebuild_is_never_shipped_before_its_container_actually_archives()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        svc.PitrEnabled = true;
        svc.HasUnpublishedChanges = true; // requested, not yet applied
        await h.Db.SaveChangesAsync();

        await NewShipper(h).ShipDueInstancesAsync(default);

        h.Docker.Calls.Should().BeEmpty(
            "archive_command only exists in the container's command line after a rebuild — shipping before that would read an empty volume and record it as a healthy, empty run");
    }

    [Fact]
    public async Task Already_shipped_segments_are_never_re_uploaded()
    {
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid(), "shop");
        svc.PitrEnabled = true;
        svc.HasUnpublishedChanges = false;
        h.Db.WalSegments.Add(new WalSegment
        {
            WorkspaceId = svc.WorkspaceId, ManagedServiceId = svc.Id, DestinationId = h.Destination.Id,
            FileName = "00000001000000000000000A", ArchivedAt = h.Clock.UtcNow.AddMinutes(-10),
            ArtifactPath = Path.Combine(h.Storage.LocalStagingDir, "00000001000000000000000A")
        });
        await h.Db.SaveChangesAsync();
        h.Docker.OneOffOutput.Add("00000001000000000000000A\n");

        await NewShipper(h).ShipDueInstancesAsync(default);

        (await h.Db.WalSegments.CountAsync(w => w.ManagedServiceId == svc.Id)).Should().Be(1,
            "the segment already recorded must not be shipped a second time");
    }
}
