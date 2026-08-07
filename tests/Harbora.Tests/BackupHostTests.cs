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

    /// <summary>
    /// The database's own server is the one asked about, and it is asked before anything runs.
    /// Whether that machine can then be used is a separate question with its own tests below; what is
    /// pinned here is that the panel's daemon is never the answer by default.
    /// </summary>
    [Fact]
    public async Task A_database_backup_is_addressed_to_the_machine_that_holds_the_database()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        h.ServerAt(serverId);
        var service = await h.SeedDatabaseAsync(serverId);
        var backup = await h.SeedPendingBackupAsync(BackupType.Database, service.Id.ToString());

        await h.Engine().RunAsync(backup.Id, default);

        h.Engines.Resolved.Should().Contain(serverId,
            "the export belongs where the database is; nowhere else has its data");
        h.Docker.OneOffRequests.Should().BeEmpty(
            "the panel's own daemon holds no copy of a database scheduled elsewhere");
    }

    [Fact]
    public async Task A_volume_backup_is_addressed_to_the_machine_that_runs_the_application()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        h.ServerAt(serverId);
        await h.SeedAppWithVolumeAsync(serverId, "blog-data");
        var backup = await h.SeedPendingBackupAsync(BackupType.Volume, "blog-data");

        await h.Engine().RunAsync(backup.Id, default);

        h.Engines.Resolved.Should().Contain(serverId);
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

    // --- another machine that WILL run the helper ----------------------------------------------

    /// <summary>
    /// The older inbound HTTP agent is the dangerous case, because nothing refuses it. It runs the
    /// helper exactly as asked — and the helper writes the archive into the staging volume on
    /// <em>that</em> machine, while the panel reads its own. So the tar is created correctly
    /// somewhere nothing here can ever read, and the failure the panel then reports is the
    /// staging-volume message, which sends an operator to run `docker volume ls` on a machine where
    /// everything looks perfectly fine.
    /// </summary>
    [Fact]
    public async Task A_volume_backup_on_another_server_is_refused_before_a_helper_writes_anything()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        var remote = h.ServerAt(serverId, "web-02");
        await h.SeedAppWithVolumeAsync(serverId, "blog-data");
        var backup = await h.SeedPendingBackupAsync(BackupType.Volume, "blog-data");

        await h.Engine().RunAsync(backup.Id, default);

        var stored = await h.Db.Backups.AsNoTracking().FirstAsync(b => b.Id == backup.Id);
        stored.Status.Should().Be(BackupStatus.Failed);
        stored.ErrorMessage.Should().Contain("web-02", "the machine is the whole reason this cannot be done");
        stored.ErrorMessage.Should().NotContain("docker volume ls",
            "that check describes two volumes on one machine; here there are two machines, and the " +
            "operator it sends to the panel's volume list will find nothing wrong there");
        remote.OneOffRequests.Should().BeEmpty(
            "a tar left in that machine's staging volume is an artifact nothing on either side collects");
        h.Docker.OneOffRequests.Should().BeEmpty();
        stored.ArtifactPath.Should().BeNull();
    }

    /// <summary>
    /// Same host, worse consequence: a scheduled database backup would run a real <c>pg_dump</c> on
    /// the remote machine every tick and leave the dump in its staging volume, on a disk the panel
    /// does not measure and nothing prunes.
    /// </summary>
    [Fact]
    public async Task A_database_backup_on_another_server_leaves_no_dump_behind_on_that_machine()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        var remote = h.ServerAt(serverId, "db-host");
        var service = await h.SeedDatabaseAsync(serverId);
        var backup = await h.SeedPendingBackupAsync(BackupType.Database, service.Id.ToString());

        await h.Engine().RunAsync(backup.Id, default);

        var stored = await h.Db.Backups.AsNoTracking().FirstAsync(b => b.Id == backup.Id);
        stored.Status.Should().Be(BackupStatus.Failed);
        stored.ErrorMessage.Should().Contain("db-host");
        stored.ErrorMessage.Should().NotContain("docker volume ls");
        remote.OneOffRequests.Should().BeEmpty(
            "every scheduled run would otherwise add another dump to a disk nothing here can see or clear");
        h.Docker.OneOffRequests.Should().BeEmpty();
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

    /// <summary>
    /// A restore onto the legacy agent's machine is the mirror of the backup: the panel copies the
    /// artifact into its own staging directory and the helper over there looks in a staging volume
    /// that has never heard of it. The refusal has to arrive before the container is stopped and
    /// before the pre-restore snapshot, or the machine is left down for a restore that cannot run.
    /// </summary>
    [Fact]
    public async Task A_volume_restore_onto_another_server_stops_nothing_and_snapshots_nothing()
    {
        using var h = new BackupHarness();
        h.Options.SnapshotBeforeRestore = true;
        var serverId = Guid.NewGuid();
        var remote = h.ServerAt(serverId, "web-02");
        await h.SeedAppWithVolumeAsync(serverId, "blog-data");
        var backup = await h.SeedVolumeBackupAsync();

        var restore = async () => await h.Engine().RestoreAsync(backup.Id, default);

        (await restore.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*web-02*");
        remote.Calls.Should().BeEmpty(
            "nothing may be stopped or snapshotted for a restore that was never going to happen");
        h.Docker.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// The pre-restore snapshot is the only thing standing between a successful restore of the WRONG
    /// backup and permanent loss, and it now runs on a resolved engine rather than an injected field.
    /// Every other test in the suite turns it off, so this is the one that watches it happen —
    /// on the host the restore resolved, and before the restore itself.
    /// </summary>
    [Fact]
    public async Task The_pre_restore_snapshot_runs_on_the_resolved_host_before_the_restore()
    {
        using var h = new BackupHarness();
        h.Options.SnapshotBeforeRestore = true;
        var backup = await h.SeedVolumeBackupAsync();

        await h.Engine().RestoreAsync(backup.Id, default);

        h.Docker.OneOffCommands.Should().HaveCount(2);
        h.Docker.OneOffCommands[0].Should().Contain("pre-restore-blog-data").And.Contain("tar czf",
            "the safety copy is taken first, or there is nothing to go back to");
        h.Docker.OneOffCommands[1].Should().NotContain("pre-restore-");
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

    /// <summary>
    /// The legacy agent's machine would run the rehearsal's scratch database happily and then fail to
    /// find the dump — because the panel copied it into the panel's own staging directory. The
    /// verdict that came back was "This backup does not restore", a hard statement of an unreadable
    /// archive about an archive nothing has found anything wrong with. Not checked is not the same as
    /// checked and broken.
    /// </summary>
    [Fact]
    public async Task A_rehearsal_on_another_server_is_skipped_rather_than_called_a_bad_backup()
    {
        using var h = new BackupHarness();
        var serverId = Guid.NewGuid();
        var remote = h.ServerAt(serverId, "web-02");
        var service = await h.SeedDatabaseAsync(serverId);
        var backup = await h.SeedCompletedDatabaseDumpAsync(service.Id);

        var result = await h.Engine().VerifyAsync(backup.Id, default);

        result.Checks.Should().Contain(c => c.Name == "Archive readable" && c.Passed);
        result.Checks.Should().Contain(c => c.Skipped && c.Detail!.Contains("web-02"));
        result.IsRestorable.Should().BeTrue(
            "nothing has been found wrong with this archive; only that it could not be rehearsed");
        result.Reason.Should().BeNull();
        remote.OneOffRequests.Should().BeEmpty();
        h.Docker.OneOffRequests.Should().BeEmpty();
    }
}
