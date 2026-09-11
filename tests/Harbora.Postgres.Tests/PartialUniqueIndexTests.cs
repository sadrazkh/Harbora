using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Postgres.Tests;

/// <summary>
/// The filtered unique indexes, refusing what they exist to refuse.
///
/// <para>
/// <c>BackupSnapshotService</c> and <c>RestoreService</c> both check before they insert, and both
/// give a sentence a person can act on — but a read followed by an insert is two steps, and a manual
/// run and the scheduler can pass the check in the same instant. The index is the half of the answer
/// that cannot be raced. EF InMemory has no such thing, so until this lane the guard was an
/// assumption. <c>DeploymentEngine.QueueDeploymentAsync</c> (HARBORA-0057) is the identical shape:
/// a read for an in-flight deployment, then an insert, with nothing between them that a real race
/// cannot slip through.
/// </para>
///
/// <para>
/// The <c>WHERE</c> on each is what keeps them liveable: without it a target could be backed up
/// exactly once, ever — and without the deployment index's own filter, an app could be deployed
/// exactly once, ever, the moment that one deployment finished.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class PartialUniqueIndexTests(PostgresLane lane)
{
    private static readonly Guid TenantOne = new("21111111-0000-0000-0000-000000000001");
    private static readonly Guid TenantTwo = new("21111111-0000-0000-0000-000000000002");

    [PostgresFact]
    public async Task A_second_active_backup_of_one_target_is_refused()
    {
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("active_backup"));
        var repository = await RepositoryAsync(db);

        db.BackupSnapshots.Add(Snapshot(repository, TenantOne, "vol-one", BackupSnapshotStatus.Running));
        await db.SaveChangesAsync();

        db.BackupSnapshots.Add(Snapshot(repository, TenantOne, "vol-one", BackupSnapshotStatus.Pending));

        (await Refusal(db)).ConstraintName.Should().Be("IX_BackupSnapshots_ActiveTarget");
    }

    [PostgresFact]
    public async Task A_backup_that_is_only_preparing_holds_the_target_just_as_firmly()
    {
        // Preparing is the middle of the three states the filter covers and the one nothing else in
        // this lane reaches: the fact above pairs Running with Pending, and the migration's settling
        // statement carries its own status list, which is a different list in a different statement.
        // An index filtered to IN (0, 2) would pass every other fact here and leave the guard open
        // for exactly as long as a backup spends preparing — which is the window a manual run and
        // the scheduler are likeliest to collide in, because preparing is the slow part.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("preparing_backup"));
        var repository = await RepositoryAsync(db);

        db.BackupSnapshots.Add(Snapshot(repository, TenantOne, "vol-preparing", BackupSnapshotStatus.Preparing));
        await db.SaveChangesAsync();

        db.BackupSnapshots.Add(Snapshot(repository, TenantOne, "vol-preparing", BackupSnapshotStatus.Pending));

        (await Refusal(db)).ConstraintName.Should().Be("IX_BackupSnapshots_ActiveTarget");
    }

    [PostgresFact]
    public async Task A_backup_of_the_same_target_is_allowed_once_the_first_one_has_finished()
    {
        // The filter, in one fact. If it were dropped, a volume could be backed up once and never
        // again — which is a worse outage than the double-run the index prevents.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("finished_backup"));
        var repository = await RepositoryAsync(db);

        var first = Snapshot(repository, TenantOne, "vol-two", BackupSnapshotStatus.Running);
        db.BackupSnapshots.Add(first);
        await db.SaveChangesAsync();

        first.Status = BackupSnapshotStatus.Completed;
        await db.SaveChangesAsync();

        db.BackupSnapshots.Add(Snapshot(repository, TenantOne, "vol-two", BackupSnapshotStatus.Pending));

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [PostgresFact]
    public async Task Two_tenants_may_each_have_a_live_backup_of_a_target_they_both_call_the_same_thing()
    {
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("tenant_backup"));
        var repository = await RepositoryAsync(db);

        db.BackupSnapshots.Add(Snapshot(repository, TenantOne, "shared-name", BackupSnapshotStatus.Running));
        db.BackupSnapshots.Add(Snapshot(repository, TenantTwo, "shared-name", BackupSnapshotStatus.Running));

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [PostgresFact]
    public async Task A_second_active_restore_into_one_destination_is_refused()
    {
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("active_restore"));
        var snapshot = await RestorableSnapshotAsync(db);

        db.RestoreJobs.Add(Restore(snapshot, TenantOne, "/srv/harbora/restore/one", RestoreJobStatus.Running));
        await db.SaveChangesAsync();

        db.RestoreJobs.Add(Restore(snapshot, TenantOne, "/srv/harbora/restore/one", RestoreJobStatus.Pending));

        (await Refusal(db)).ConstraintName.Should().Be("IX_RestoreJobs_ActiveDestination");
    }

    [PostgresFact]
    public async Task Two_tenants_restoring_into_one_destination_is_still_refused()
    {
        // The asymmetry with backups above is deliberate and is the whole reason this index is not
        // workspace-scoped: a destination is one directory on one machine, and two tenants writing
        // into it at once is precisely the case a per-tenant index would wave through.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("shared_restore"));
        var snapshot = await RestorableSnapshotAsync(db);

        db.RestoreJobs.Add(Restore(snapshot, TenantOne, "/srv/harbora/restore/shared", RestoreJobStatus.Running));
        await db.SaveChangesAsync();

        db.RestoreJobs.Add(Restore(snapshot, TenantTwo, "/srv/harbora/restore/shared", RestoreJobStatus.Running));

        (await Refusal(db)).ConstraintName.Should().Be("IX_RestoreJobs_ActiveDestination");
    }

    [PostgresFact]
    public async Task A_finished_restore_does_not_block_the_next_one_into_the_same_place()
    {
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("finished_restore"));
        var snapshot = await RestorableSnapshotAsync(db);

        var first = Restore(snapshot, TenantOne, "/srv/harbora/restore/again", RestoreJobStatus.Running);
        db.RestoreJobs.Add(first);
        await db.SaveChangesAsync();

        first.Status = RestoreJobStatus.Completed;
        await db.SaveChangesAsync();

        db.RestoreJobs.Add(Restore(snapshot, TenantOne, "/srv/harbora/restore/again", RestoreJobStatus.Pending));

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [PostgresFact]
    public async Task The_longest_destination_the_service_accepts_still_fits_in_the_index()
    {
        // RestoreJob.MaxDestinationLength's whole argument: the column holds 1024 characters and a
        // btree index row cannot exceed roughly 2704 bytes, so refusing at 512 in RestoreService is
        // what makes the limit unreachable — 512 characters are at most 2048 bytes in UTF-8, even
        // when every one of them is a four-byte code point. That is an arithmetic claim about
        // Postgres, and this is it being made to Postgres.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("longest_destination"));
        var snapshot = await RestorableSnapshotAsync(db);

        var worstCase = string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F600), 512));
        worstCase.Should().HaveLength(1024, "512 code points outside the basic plane are 1024 UTF-16 chars");

        db.RestoreJobs.Add(Restore(snapshot, TenantOne, worstCase, RestoreJobStatus.Running));

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync(
            "2048 bytes is comfortably under the btree row limit");
    }

    [PostgresFact]
    public async Task A_second_in_flight_deployment_of_one_app_is_refused()
    {
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("active_deployment"));
        var app = await AppAsync(db);

        db.Deployments.Add(DeploymentRow(app, 1, DeploymentStatus.Queued));
        await db.SaveChangesAsync();

        db.Deployments.Add(DeploymentRow(app, 2, DeploymentStatus.Building));

        (await Refusal(db)).ConstraintName.Should().Be("IX_Deployments_ActiveDeployment");
    }

    [PostgresFact]
    public async Task A_deployment_still_pending_approval_holds_the_app_just_as_firmly()
    {
        // PendingApproval is what 5.2 widened the rule to also cover: no Job exists for it yet, but a
        // second request while it waits on a person must be refused exactly like one arriving
        // mid-build already is — otherwise a protected environment could pile up parallel approval
        // requests for the same app. DeploymentStateMachine.Unsettled is what the index's own filter
        // is built from, and this is the one status in it that InFlight alone does not cover.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("pending_deployment"));
        var app = await AppAsync(db);

        db.Deployments.Add(DeploymentRow(app, 1, DeploymentStatus.PendingApproval));
        await db.SaveChangesAsync();

        db.Deployments.Add(DeploymentRow(app, 2, DeploymentStatus.Queued));

        (await Refusal(db)).ConstraintName.Should().Be("IX_Deployments_ActiveDeployment");
    }

    [PostgresFact]
    public async Task A_new_deployment_is_allowed_once_the_previous_one_is_terminal()
    {
        // The filter, in one fact. If it were dropped, or written wide enough to also match a
        // terminal status, an app could be deployed exactly once, ever — the same failure mode the
        // backup index's own version of this fact guards above.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("terminal_deployment"));
        var app = await AppAsync(db);

        var first = DeploymentRow(app, 1, DeploymentStatus.Deploying);
        db.Deployments.Add(first);
        await db.SaveChangesAsync();

        first.Status = DeploymentStatus.Succeeded;
        await db.SaveChangesAsync();

        db.Deployments.Add(DeploymentRow(app, 2, DeploymentStatus.Queued));

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [PostgresFact]
    public async Task Two_apps_may_each_have_their_own_in_flight_deployment()
    {
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("two_apps_deployment"));
        var appOne = await AppAsync(db, "one");
        var appTwo = await AppAsync(db, "two");

        db.Deployments.Add(DeploymentRow(appOne, 1, DeploymentStatus.Queued));
        db.Deployments.Add(DeploymentRow(appTwo, 1, DeploymentStatus.Queued));

        await db.Awaiting(c => c.SaveChangesAsync()).Should().NotThrowAsync(
            "the index is scoped to one app's own rows — a second app's in-flight deployment is not " +
            "this app's second one");
    }

    /// <summary>
    /// Reads the filter <c>IX_Deployments_ActiveDeployment</c> actually shipped with back out of
    /// <c>pg_indexes</c> and checks it against <see cref="DeploymentStateMachine.Unsettled"/> directly
    /// — the natural tripwire the brief asks for in place of trusting a comment: a status added to
    /// <c>DeploymentStateMachine.Unsettled</c> without a fresh migration to match, or a migration
    /// hand-edited out of step with it, fails exactly here rather than only in a production incident
    /// where an app cannot be deployed to again.
    /// </summary>
    [PostgresFact]
    public async Task The_active_deployment_index_is_filtered_to_exactly_DeploymentStateMachine_Unsettled()
    {
        var definition = await IndexCatalogue.DefinitionAsync(
            await lane.HeadSchemaAsync(), "IX_Deployments_ActiveDeployment");

        definition.Should().StartWith("CREATE UNIQUE INDEX");
        definition.Should().Contain("(\"AppId\")");

        var expected = DeploymentStateMachine.Unsettled.Select(s => (int)s).ToArray();

        IndexCatalogue.FilteredValues(definition,
                "an index with no filter at all covers every terminal deployment too, and an app " +
                "could be deployed exactly once, ever")
            .Should().BeEquivalentTo(expected,
                "the filter must admit exactly the statuses DeploymentStateMachine.Unsettled calls " +
                "still unsettled — too few and a second in-flight deployment walks straight past the " +
                "index, too many (a terminal status such as Cancelled, or some future TimedOut) and " +
                "the app it belongs to could never be deployed again. Postgres printed the index as {0}",
                definition);
    }

    private static async Task<App> AppAsync(HarboraDbContext db, string suffix = "one")
    {
        var workspace = new Workspace { Name = $"acme-{suffix}", Slug = $"acme-{suffix}" };
        var project = new Project { WorkspaceId = workspace.Id, Name = "shop", Slug = $"shop-{suffix}" };
        var environment = new Environment
        {
            WorkspaceId = workspace.Id, ProjectId = project.Id,
            Name = "production", Slug = "production", IsDefault = true
        };
        var app = new App
        {
            WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(), EnvironmentId = environment.Id,
            Name = $"app-{suffix}", Slug = $"app-{suffix}"
        };
        db.Workspaces.Add(workspace);
        db.Projects.Add(project);
        db.Environments.Add(environment);
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        return app;
    }

    private static Deployment DeploymentRow(App app, int number, DeploymentStatus status) =>
        new()
        {
            AppId = app.Id, WorkspaceId = app.WorkspaceId, Number = number, Status = status,
            TriggeredByUserId = Guid.CreateVersion7()
        };

    /// <summary>The duplicate-key error, unwrapped — and asserted to be one, not some other failure.</summary>
    private static async Task<PostgresException> Refusal(HarboraDbContext db)
    {
        var thrown = await db.Awaiting(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();

        var inner = thrown.Which.InnerException.Should().BeOfType<PostgresException>().Which;
        inner.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation,
            "the services catch exactly this code and turn it into a refusal a person can read");
        return inner;
    }

    private static async Task<BackupRepository> RepositoryAsync(HarboraDbContext db)
    {
        var repository = new BackupRepository { WorkspaceId = TenantOne, Name = "primary" };
        db.BackupRepositories.Add(repository);
        await db.SaveChangesAsync();
        return repository;
    }

    private static async Task<BackupSnapshot> RestorableSnapshotAsync(HarboraDbContext db)
    {
        var repository = await RepositoryAsync(db);
        var snapshot = Snapshot(repository, TenantOne, "vol-restorable", BackupSnapshotStatus.Completed);
        db.BackupSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        return snapshot;
    }

    private static BackupSnapshot Snapshot(
        BackupRepository repository, Guid workspaceId, string targetRef, BackupSnapshotStatus status) =>
        new()
        {
            WorkspaceId = workspaceId,
            RepositoryId = repository.Id,
            TargetType = BackupTargetType.DockerVolume,
            TargetRef = targetRef,
            Status = status
        };

    private static RestoreJob Restore(
        BackupSnapshot snapshot, Guid workspaceId, string destination, RestoreJobStatus status) =>
        new()
        {
            WorkspaceId = workspaceId,
            SnapshotId = snapshot.Id,
            Destination = destination,
            Status = status
        };
}
