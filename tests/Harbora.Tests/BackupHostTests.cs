using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which machine a backup actually reads and writes.
///
/// <para>
/// The worst defect the audit found lived here. A volume or database backup ran its helper container
/// on the panel's own Docker daemon whatever server the data was scheduled on, so a backup of a
/// service on another machine archived whichever local volume happened to carry the same name — or
/// an empty one Docker created on the spot — and recorded a successful backup. The failure is
/// invisible until a restore, which is the one moment it must not be.
/// </para>
///
/// <para>
/// So these tests assert against the engine <em>factory</em>, never against a single engine: with one
/// engine in the test, right host and wrong host produce the same call log.
/// </para>
/// </summary>
public sealed class BackupHostTests
{
    /// <summary>A v1 node, which by contract runs no one-off containers at all.</summary>
    private static NodeWorkloadEngine Node(string nodeId = "web-01") =>
        new(nodeId, null!, null!, null!, NullLogger.Instance);

    // --- taking a backup ---------------------------------------------------------------------

    [Fact]
    public async Task A_database_backup_is_exported_on_the_machine_that_holds_the_database()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        var remote = h.ServerAt(serverId);
        var service = await h.SeedDatabaseAsync(serverId);
        var backup = await h.SeedPendingBackupAsync(BackupType.Database, service.Id.ToString());

        await h.Engine().RunAsync(backup.Id, default);

        remote.OneOffRequests.Should().ContainSingle(
            "the export has to run where the database is; nowhere else has its data");
        h.Docker.OneOffRequests.Should().BeEmpty(
            "the panel's own daemon holds no copy of a database scheduled elsewhere");
        h.Engines.Resolved.Should().Contain(serverId);
    }

    [Fact]
    public async Task A_volume_backup_is_archived_on_the_machine_that_runs_the_application()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        var remote = h.ServerAt(serverId);
        await h.SeedAppWithVolumeAsync(serverId, "blog-data");
        var backup = await h.SeedPendingBackupAsync(BackupType.Volume, "blog-data");

        await h.Engine().RunAsync(backup.Id, default);

        remote.OneOffRequests.Should().ContainSingle();
        h.Docker.OneOffRequests.Should().BeEmpty(
            "a same-named volume on the panel is a different volume, and archiving it is the defect");
    }

    /// <summary>
    /// A v1 node has no verb for running a container to completion — deliberately, because that is a
    /// shell with extra steps. So a volume backup of anything on one cannot be taken yet, and the
    /// only honest outcome is a refusal that names the node.
    /// </summary>
    [Fact]
    public async Task A_backup_on_a_node_that_cannot_run_helpers_fails_and_names_the_node()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        h.Engines.On(serverId, Node("web-01"));
        await h.SeedAppWithVolumeAsync(serverId, "blog-data");
        var backup = await h.SeedPendingBackupAsync(BackupType.Volume, "blog-data");

        await h.Engine().RunAsync(backup.Id, default);

        var stored = await h.Db.Backups.AsNoTracking().FirstAsync(b => b.Id == backup.Id);
        stored.Status.Should().Be(BackupStatus.Failed);
        stored.ErrorMessage.Should().Contain("web-01", "an operator has to know which machine could not do it");
        stored.ArtifactPath.Should().BeNull("nothing may be recorded as an artifact of a backup never taken");
        h.Docker.OneOffRequests.Should().BeEmpty("falling back to the panel would archive the wrong volume");
    }

    [Fact]
    public async Task A_database_backup_on_a_node_fails_rather_than_dumping_from_the_panel()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        h.Engines.On(serverId, Node("db-node"));
        var service = await h.SeedDatabaseAsync(serverId);
        var backup = await h.SeedPendingBackupAsync(BackupType.Database, service.Id.ToString());

        await h.Engine().RunAsync(backup.Id, default);

        var stored = await h.Db.Backups.AsNoTracking().FirstAsync(b => b.Id == backup.Id);
        stored.Status.Should().Be(BackupStatus.Failed);
        stored.ErrorMessage.Should().Contain("db-node");
        h.Docker.OneOffRequests.Should().BeEmpty();
    }

    /// <summary>
    /// The factory refuses a server with no endpoint and no node rather than handing back the local
    /// engine. That refusal has to end the backup, not be swallowed into a local run.
    /// </summary>
    [Fact]
    public async Task A_backup_whose_server_cannot_be_reached_fails_with_the_factorys_reason()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        h.Engines.Unreachable(serverId, "Server 'web-02' cannot be reached: no agent endpoint, no node.");
        var service = await h.SeedDatabaseAsync(serverId);
        var backup = await h.SeedPendingBackupAsync(BackupType.Database, service.Id.ToString());

        await h.Engine().RunAsync(backup.Id, default);

        var stored = await h.Db.Backups.AsNoTracking().FirstAsync(b => b.Id == backup.Id);
        stored.Status.Should().Be(BackupStatus.Failed);
        stored.ErrorMessage.Should().Contain("web-02");
        h.Docker.OneOffRequests.Should().BeEmpty();
    }

    /// <summary>
    /// A volume target is a bare docker volume name, so the machine holding it is whichever
    /// application declares it. When none does there is nothing to attribute it to, and the panel's
    /// own daemon is the only place the name can be addressed — which is what already happened, and
    /// what <c>BackupSafetyTests</c> pins the failure message for.
    /// </summary>
    [Fact]
    public async Task A_volume_no_application_declares_is_still_read_from_this_panel()
    {
        using var h = new BackupHarness();
        var backup = await h.SeedPendingBackupAsync(BackupType.Volume, "some-volume");

        await h.Engine().RunAsync(backup.Id, default);

        h.Docker.OneOffRequests.Should().ContainSingle();
    }

    // --- restoring ---------------------------------------------------------------------------

    [Fact]
    public async Task A_volume_restore_onto_a_node_refuses_and_touches_no_daemon()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        h.Engines.On(serverId, Node("web-01"));
        await h.SeedAppWithVolumeAsync(serverId, "blog-data");
        var backup = await h.SeedVolumeBackupAsync();

        var restore = async () => await h.Engine().RestoreAsync(backup.Id, default);

        (await restore.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*web-01*");
        h.Docker.Calls.Should().BeEmpty(
            "restoring onto the panel's own volume would destroy data that was never backed up here");
    }

    // --- verifying --------------------------------------------------------------------------

    /// <summary>
    /// The rehearsal restores a dump into a scratch database on the server the database is on. A node
    /// cannot host that either — and "not checked" must never be reported as "checked and fine", nor
    /// as an unreadable archive when the archive is perfectly good.
    /// </summary>
    [Fact]
    public async Task A_rehearsal_that_the_host_cannot_run_is_recorded_as_skipped_not_as_a_bad_archive()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        h.Engines.On(serverId, Node("web-01"));
        var service = await h.SeedDatabaseAsync(serverId);
        var backup = await h.SeedCompletedDatabaseDumpAsync(service.Id);

        var result = await h.Engine().VerifyAsync(backup.Id, default);

        result.Checks.Should().Contain(c => c.Name == "Archive readable" && c.Passed);
        result.Checks.Should().Contain(c => c.Skipped && c.Detail!.Contains("web-01"));
        h.Docker.OneOffRequests.Should().BeEmpty();
    }
}
