using FluentAssertions;
using Harbora.Data;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// The two filtered unique indexes, refusing what they exist to refuse.
///
/// <para>
/// <c>BackupSnapshotService</c> and <c>RestoreService</c> both check before they insert, and both
/// give a sentence a person can act on — but a read followed by an insert is two steps, and a manual
/// run and the scheduler can pass the check in the same instant. The index is the half of the answer
/// that cannot be raced. EF InMemory has no such thing, so until this lane the guard was an
/// assumption.
/// </para>
///
/// <para>
/// The <c>WHERE</c> on each is what keeps them liveable: without it a target could be backed up
/// exactly once, ever.
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
