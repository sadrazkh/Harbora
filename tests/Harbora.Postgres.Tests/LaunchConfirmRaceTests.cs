using FluentAssertions;
using Harbora.Domain.Launch;
using Harbora.Infrastructure.Launch;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// Two submits of one confirm screen, for real. EF InMemory has no <c>ExecuteUpdateAsync</c>, so
/// <see cref="LaunchMoves.TryMoveAsync"/> is a read-then-write there and proves nothing about a race:
/// the claim that only one of two concurrent submits goes on to create anything rests on Postgres
/// serialising two conditional updates of one row, which is what this checks.
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class LaunchConfirmRaceTests(PostgresLane lane)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static async Task<Guid> SeedAsync(string connectionString, LaunchStatus status = LaunchStatus.AwaitingConfirmation)
    {
        await using var db = PostgresLane.Open(connectionString);
        var launch = new AppLaunch
        {
            WorkspaceId = Guid.CreateVersion7(), UserId = Guid.CreateVersion7(), Status = status,
            StatusChangedAt = Now - TimeSpan.FromHours(1)
        };
        db.AppLaunches.Add(launch);
        await db.SaveChangesAsync();
        return launch.Id;
    }

    [PostgresFact]
    public async Task Of_many_concurrent_confirms_exactly_one_claims_the_launch()
    {
        var connectionString = await lane.FreshlyMigratedAsync("launch_confirm_race");
        var id = await SeedAsync(connectionString);

        // Independent connections, so these are genuinely concurrent statements for Postgres to arbitrate.
        var contexts = Enumerable.Range(0, 8).Select(_ => PostgresLane.Open(connectionString)).ToList();
        try
        {
            var outcomes = await Task.WhenAll(contexts.Select(db =>
                LaunchMoves.TryMoveAsync(db, id, LaunchStatus.AwaitingConfirmation, LaunchStatus.Creating, Now, default)));

            outcomes.Count(won => won).Should().Be(1);
        }
        finally
        {
            foreach (var db in contexts) await db.DisposeAsync();
        }

        await using var verify = PostgresLane.Open(connectionString);
        var stored = await verify.AppLaunches.IgnoreQueryFilters().SingleAsync(l => l.Id == id);
        stored.Status.Should().Be(LaunchStatus.Creating);
        stored.StatusChangedAt.Should().Be(Now);
    }

    [PostgresFact]
    public async Task A_launch_no_longer_in_the_state_a_move_expects_is_not_moved()
    {
        var connectionString = await lane.FreshlyMigratedAsync("launch_confirm_stale");
        var id = await SeedAsync(connectionString, LaunchStatus.Abandoned);

        await using var db = PostgresLane.Open(connectionString);
        (await LaunchMoves.TryMoveAsync(db, id, LaunchStatus.AwaitingConfirmation, LaunchStatus.Creating, Now, default))
            .Should().BeFalse("the sweep got there first");

        await using var verify = PostgresLane.Open(connectionString);
        (await verify.AppLaunches.IgnoreQueryFilters().SingleAsync(l => l.Id == id)).Status.Should().Be(LaunchStatus.Abandoned);
    }
}
