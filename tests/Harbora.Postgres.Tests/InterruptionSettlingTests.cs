using FluentAssertions;
using Harbora.Modules.Backup.Contracts;
using Xunit;
using static Harbora.Postgres.Tests.UpgradeFromPreviousRelease;

namespace Harbora.Postgres.Tests;

/// <summary>
/// The three settling statements in <c>BackupInterruptionRecovery</c>, branch by branch.
///
/// <para>
/// They exist because the migration goes on to build two <b>unique</b> indexes over rows the old
/// schema permitted. A <c>CREATE UNIQUE INDEX</c> that meets a duplicate throws, and a migration
/// that throws is a panel that will not boot — a worse failure than the one being fixed. So the
/// rows are settled first: nothing is deleted, one of each pair survives, and the loser carries a
/// sentence saying what happened to it.
/// </para>
///
/// <para>
/// Every fact here is either "this row had to change" or "this row had to be left alone", and the
/// second kind is the harder half: a settling statement that is too eager destroys live backups.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class InterruptionSettlingTests(PostgresLane lane)
{
    // ---- backup snapshots -------------------------------------------------------------------

    [PostgresFact]
    public async Task Of_three_active_backups_of_one_target_the_newest_survives()
    {
        var upgraded = await lane.UpgradedAsync();

        var newest = await UpgradedReads.SnapshotAsync(upgraded.ConnectionString, Seeded.NewestOfThree);

        newest.Status.Should().Be(BackupSnapshotStatus.Running);
        newest.FailureReason.Should().BeNull();
        newest.CompletedAt.Should().BeNull("it was not finished, only left alone");
    }

    [PostgresFact]
    public async Task The_older_two_are_settled_failed_with_a_reason_a_person_can_read()
    {
        var upgraded = await lane.UpgradedAsync();

        foreach (var id in new[] { Seeded.OldestOfThree, Seeded.MiddleOfThree })
        {
            var settled = await UpgradedReads.SnapshotAsync(upgraded.ConnectionString, id);

            settled.Status.Should().Be(BackupSnapshotStatus.Failed);
            settled.FailureReason.Should().Contain("Settled during an upgrade")
                .And.Contain("treat it as not taken",
                    "an operator reading this row has to learn that no backup exists for that window");

            // NOW(), weeks after the row was written — which is how "this row was settled by the
            // migration" is told apart from "this row was already like that".
            settled.CompletedAt.Should().NotBeNull().And.BeAfter(settled.CreatedAt);
            settled.UpdatedAt.Should().BeAfter(settled.CreatedAt);
        }
    }

    [PostgresFact]
    public async Task Two_backups_written_in_the_same_instant_are_broken_apart_by_id()
    {
        // A manual run and the scheduler's, in the same second. Without the id term the CreatedAt
        // comparison settles neither and the index build fails.
        var upgraded = await lane.UpgradedAsync();

        var loser = await UpgradedReads.SnapshotAsync(upgraded.ConnectionString, Seeded.TiedSnapshotLoser);
        var winner = await UpgradedReads.SnapshotAsync(upgraded.ConnectionString, Seeded.TiedSnapshotWinner);

        loser.CreatedAt.Should().Be(winner.CreatedAt, "the tie is what this case is about");
        Ordering(loser.Id, winner.Id).Should().BeNegative("the seed relies on which id is the greater");
        loser.Status.Should().Be(BackupSnapshotStatus.Failed);
        winner.Status.Should().Be(BackupSnapshotStatus.Running);
    }

    /// <summary>
    /// Postgres orders <c>uuid</c> by its bytes in the order they are written, which is what the
    /// canonical text says — and not what <c>Guid.CompareTo</c> says, which reorders the first three
    /// fields. Comparing the text is comparing what the database compared.
    /// </summary>
    private static int Ordering(Guid left, Guid right) =>
        string.CompareOrdinal(left.ToString(), right.ToString());

    [PostgresFact]
    public async Task A_single_active_backup_is_untouched()
    {
        // Also the idempotence case: one active row per target is exactly what a second run of this
        // migration would find, and it must change nothing.
        var upgraded = await lane.UpgradedAsync();

        var lone = await UpgradedReads.SnapshotAsync(upgraded.ConnectionString, Seeded.LoneSnapshot);

        lone.Status.Should().Be(BackupSnapshotStatus.Running);
        lone.FailureReason.Should().BeNull();
        lone.CompletedAt.Should().BeNull();
        lone.UpdatedAt.Should().BeCloseTo(lone.CreatedAt, TimeSpan.FromMilliseconds(1),
            "the statement never reached it, so nothing stamped it");
    }

    [PostgresFact]
    public async Task A_finished_backup_of_the_same_target_is_not_a_duplicate()
    {
        // The newer of the two finished rows is the one this fact turns on. The statement's EXISTS
        // asks "is there a newer run of this target?" and restricts that search to ACTIVE rows; the
        // older finished sibling cannot show that restriction working, because being older it is
        // thrown out by the CreatedAt comparison whatever its status. The newer one is finished and
        // an hour ahead of the live run, so it reaches the status term and nothing else — and if
        // that term were missing, this upgrade would settle a running backup Failed on the grounds
        // that a backup which had already succeeded came after it.
        var upgraded = await lane.UpgradedAsync();

        var active = await UpgradedReads.SnapshotAsync(upgraded.ConnectionString, Seeded.SnapshotWithHistory);
        var older = await UpgradedReads.SnapshotAsync(
            upgraded.ConnectionString, Seeded.OlderCompletedSnapshotOfTheSameTarget);
        var newer = await UpgradedReads.SnapshotAsync(
            upgraded.ConnectionString, Seeded.NewerCompletedSnapshotOfTheSameTarget);

        newer.CreatedAt.Should().BeAfter(active.CreatedAt,
            "a finished run that came after the live one is the only kind that can reach the status " +
            "term inside the EXISTS, and so the only kind that proves it is there");
        older.CreatedAt.Should().BeBefore(active.CreatedAt, "the other half of the pair, for contrast");

        active.Status.Should().Be(BackupSnapshotStatus.Running,
            "every other run of this target has finished, so nothing is racing it and it is not a " +
            "duplicate of anything");
        active.FailureReason.Should().BeNull();
        active.CompletedAt.Should().BeNull();
        active.UpdatedAt.Should().BeCloseTo(active.CreatedAt, TimeSpan.FromMilliseconds(1),
            "nothing wrote to the live row");

        foreach (var history in new[] { older, newer })
        {
            history.Status.Should().Be(BackupSnapshotStatus.Completed,
                "the index only covers live runs, so history is not in the running for being settled");
            history.FailureReason.Should().BeNull();
            history.UpdatedAt.Should().BeCloseTo(history.CreatedAt, TimeSpan.FromMilliseconds(1),
                "the outer status term is what keeps a finished row out, and it held");
        }
    }

    [PostgresFact]
    public async Task The_same_target_reference_in_another_tenant_is_a_different_target()
    {
        // The index is workspace-scoped. Settling this would destroy another tenant's live backup
        // because they happened to name a volume the same thing.
        var upgraded = await lane.UpgradedAsync();

        var other = await UpgradedReads.SnapshotAsync(upgraded.ConnectionString, Seeded.OtherWorkspaceSnapshot);

        other.WorkspaceId.Should().Be(Seeded.WorkspaceTwo);
        other.TargetRef.Should().Be("vol-duplicated", "the collision with workspace one is the point");
        other.Status.Should().Be(BackupSnapshotStatus.Running);
    }

    [PostgresFact]
    public async Task The_same_reference_of_a_different_kind_is_a_different_target()
    {
        var upgraded = await lane.UpgradedAsync();

        var other = await UpgradedReads.SnapshotAsync(upgraded.ConnectionString, Seeded.OtherTargetTypeSnapshot);

        other.TargetType.Should().Be(BackupTargetType.Directory);
        other.Status.Should().Be(BackupSnapshotStatus.Running);
    }

    // ---- restores, by duplicate destination -------------------------------------------------

    [PostgresFact]
    public async Task Of_two_restores_into_one_destination_the_older_is_settled()
    {
        var upgraded = await lane.UpgradedAsync();

        var older = await UpgradedReads.RestoreAsync(
            upgraded.ConnectionString, Seeded.OlderRestoreOfOneDestination);
        var newer = await UpgradedReads.RestoreAsync(
            upgraded.ConnectionString, Seeded.NewerRestoreOfOneDestination);

        older.Status.Should().Be(RestoreJobStatus.Failed);
        older.FailureReason.Should().Contain("Settled during an upgrade")
            .And.Contain("Check the destination before restoring again");
        older.CompletedAt.Should().NotBeNull().And.BeAfter(older.CreatedAt);

        newer.Status.Should().Be(RestoreJobStatus.Running);
        newer.FailureReason.Should().BeNull();
        newer.UpdatedAt.Should().BeCloseTo(newer.CreatedAt, TimeSpan.FromMilliseconds(1));
    }

    [PostgresFact]
    public async Task Two_tenants_restoring_into_one_path_are_a_duplicate()
    {
        // Deliberately not workspace-scoped, and this is why: a destination is a resolved path on
        // the machine. Two tenants racing for it is precisely what a per-tenant index would allow.
        var upgraded = await lane.UpgradedAsync();

        var older = await UpgradedReads.RestoreAsync(upgraded.ConnectionString, Seeded.RestoreIntoASharedPath);
        var newer = await UpgradedReads.RestoreAsync(
            upgraded.ConnectionString, Seeded.OtherTenantsRestoreIntoTheSamePath);

        older.WorkspaceId.Should().NotBe(newer.WorkspaceId);
        older.Destination.Should().Be(newer.Destination);
        older.Status.Should().Be(RestoreJobStatus.Failed);
        newer.Status.Should().Be(RestoreJobStatus.Running);
    }

    [PostgresFact]
    public async Task Two_restores_written_in_the_same_instant_are_broken_apart_by_id()
    {
        var upgraded = await lane.UpgradedAsync();

        var loser = await UpgradedReads.RestoreAsync(upgraded.ConnectionString, Seeded.TiedRestoreLoser);
        var winner = await UpgradedReads.RestoreAsync(upgraded.ConnectionString, Seeded.TiedRestoreWinner);

        loser.CreatedAt.Should().Be(winner.CreatedAt, "the tie is what this case is about");
        Ordering(loser.Id, winner.Id).Should().BeNegative("the seed relies on which id is the greater");
        loser.Status.Should().Be(RestoreJobStatus.Failed);
        winner.Status.Should().Be(RestoreJobStatus.Running);
    }

    [PostgresFact]
    public async Task A_single_active_restore_is_untouched()
    {
        var upgraded = await lane.UpgradedAsync();

        var lone = await UpgradedReads.RestoreAsync(upgraded.ConnectionString, Seeded.LoneRestore);

        lone.Status.Should().Be(RestoreJobStatus.Running);
        lone.FailureReason.Should().BeNull();
        lone.CompletedAt.Should().BeNull();
    }

    [PostgresFact]
    public async Task A_finished_restore_into_the_same_path_is_not_a_duplicate()
    {
        // As with the backups: the newer finished restore is the interesting one. A restore that
        // finished BEFORE the live one never reaches the status term inside the EXISTS, because the
        // CreatedAt comparison has already dismissed it. This one finished an hour after, so the
        // word "active" in that subquery is the only thing that stops a running restore being
        // settled Failed because a completed restore into the same directory came later.
        var upgraded = await lane.UpgradedAsync();

        var active = await UpgradedReads.RestoreAsync(upgraded.ConnectionString, Seeded.RestoreWithHistory);
        var older = await UpgradedReads.RestoreAsync(
            upgraded.ConnectionString, Seeded.OlderCompletedRestoreOfTheSamePath);
        var newer = await UpgradedReads.RestoreAsync(
            upgraded.ConnectionString, Seeded.NewerCompletedRestoreOfTheSamePath);

        newer.CreatedAt.Should().BeAfter(active.CreatedAt,
            "only a finished restore that came after the live one can prove the search is confined " +
            "to active rows");
        older.CreatedAt.Should().BeBefore(active.CreatedAt, "the other half of the pair, for contrast");

        active.Status.Should().Be(RestoreJobStatus.Running,
            "no other restore into this path is still live, so it is not a duplicate of anything");
        active.FailureReason.Should().BeNull();
        active.CompletedAt.Should().BeNull();
        active.UpdatedAt.Should().BeCloseTo(active.CreatedAt, TimeSpan.FromMilliseconds(1),
            "nothing wrote to the live row");

        foreach (var history in new[] { older, newer })
        {
            history.Status.Should().Be(RestoreJobStatus.Completed);
            history.FailureReason.Should().BeNull("it is the audit trail of a destructive operation");
            history.UpdatedAt.Should().BeCloseTo(history.CreatedAt, TimeSpan.FromMilliseconds(1),
                "the outer status term is what keeps a finished row out, and it held");
        }
    }

    // ---- restores, by destination length ----------------------------------------------------

    [PostgresFact]
    public async Task An_active_restore_too_long_for_the_index_is_settled_before_the_index_is_built()
    {
        // This one is load-bearing for the upgrade itself. 1024 three-byte characters is 3072 bytes,
        // past what a btree index row can hold; leaving the row active makes CREATE UNIQUE INDEX
        // fail with "index row size … exceeds btree version 4 maximum 2704" and the panel does not
        // come back. That failure would surface as the upgrade throwing, so the fixture's own
        // construction is half of this assertion and the reason below is the other half.
        var upgraded = await lane.UpgradedAsync();

        var settled = await UpgradedReads.RestoreAsync(
            upgraded.ConnectionString, Seeded.ActiveRestoreWithAnOverLongDestination);

        settled.Status.Should().Be(RestoreJobStatus.Failed);
        settled.FailureReason.Should().Contain("longer than the platform can index")
            .And.Contain("Nothing was deleted")
            .And.Contain("512 characters or fewer", "the sentence has to say what would work instead");
        settled.Destination.Should().HaveLength(1024, "the destination itself is left as it was found");
    }

    [PostgresFact]
    public async Task A_finished_restore_with_the_same_length_keeps_its_record()
    {
        // Outside the index's filter, so it cannot break the build — and it is the record of where a
        // destructive operation actually wrote. Rewriting it would be the settling statement doing
        // harm for no reason.
        var upgraded = await lane.UpgradedAsync();

        var history = await UpgradedReads.RestoreAsync(
            upgraded.ConnectionString, Seeded.CompletedRestoreWithAnOverLongDestination);

        history.Status.Should().Be(RestoreJobStatus.Completed);
        history.FailureReason.Should().BeNull();
        history.CompletedAt.Should().BeNull("nothing wrote to this row");
        history.UpdatedAt.Should().BeCloseTo(history.CreatedAt, TimeSpan.FromMilliseconds(1));
    }

    [PostgresFact]
    public async Task The_bound_is_where_it_says_it_is()
    {
        // length() counts characters, not bytes: 512 of them is at most 2048 bytes in UTF-8 and
        // therefore always fits. "> 512" is strict, so 512 stays and 513 goes.
        var upgraded = await lane.UpgradedAsync();

        var atTheBound = await UpgradedReads.RestoreAsync(
            upgraded.ConnectionString, Seeded.RestoreAtExactlyTheBound);
        var pastIt = await UpgradedReads.RestoreAsync(
            upgraded.ConnectionString, Seeded.RestoreOneCharacterPastTheBound);

        atTheBound.Destination.Should().HaveLength(512);
        atTheBound.Status.Should().Be(RestoreJobStatus.Running);
        atTheBound.FailureReason.Should().BeNull();

        pastIt.Destination.Should().HaveLength(513);
        pastIt.Status.Should().Be(RestoreJobStatus.Failed);
    }
}
