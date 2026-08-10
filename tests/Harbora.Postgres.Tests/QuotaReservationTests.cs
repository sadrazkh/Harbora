using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Identity;
using Harbora.Domain.Projects;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// Proves the part of quota enforcement that cannot exist in EF InMemory: two independent requests
/// reaching the same workspace are serialised before either one reads usage.
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class QuotaReservationTests(PostgresLane lane)
{
    private static readonly Guid WorkspaceId = new("71111111-0000-0000-0000-000000000001");

    [PostgresFact]
    public async Task A_second_creator_waits_then_sees_the_resource_the_first_creator_committed()
    {
        var connectionString = await lane.FreshlyMigratedAsync("quota_creation_race");
        await SeedAsync(connectionString);

        await using var firstDb = PostgresLane.Open(connectionString);
        await using var secondDb = PostgresLane.Open(connectionString);
        var firstQuota = Service(firstDb);
        var secondQuota = Service(secondDb);

        await using var firstLease = await firstQuota.AcquireCreationLockAsync(WorkspaceId, default);
        (await firstQuota.CanAddGovernedResourcesAsync(
            WorkspaceId, new GovernanceQuotaDelta(Projects: 1), default)).Allowed.Should().BeTrue();

        var reachedLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquiredLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = Task.Run(async () =>
        {
            reachedLock.SetResult();
            await using var lease = await secondQuota.AcquireCreationLockAsync(WorkspaceId, default);
            acquiredLock.SetResult();
            var check = await secondQuota.CanAddGovernedResourcesAsync(
                WorkspaceId, new GovernanceQuotaDelta(Projects: 1), default);
            await lease.CommitAsync(default);
            return check;
        });

        await reachedLock.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(150);
        acquiredLock.Task.IsCompleted.Should().BeFalse(
            "the first creator still owns the workspace's transaction-scoped advisory lock");

        firstDb.Projects.Add(new Project
        {
            WorkspaceId = WorkspaceId,
            Name = "First",
            Slug = "first"
        });
        await firstDb.SaveChangesAsync();
        await firstLease.CommitAsync(default);

        var result = await secondAttempt.WaitAsync(TimeSpan.FromSeconds(5));
        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("1 are already reserved");
    }

    [PostgresFact]
    public async Task Disposing_an_uncommitted_reservation_rolls_the_resource_back()
    {
        var connectionString = await lane.FreshlyMigratedAsync("quota_creation_rollback");
        await SeedAsync(connectionString);

        await using (var writer = PostgresLane.Open(connectionString))
        {
            var quota = Service(writer);
            await using var lease = await quota.AcquireCreationLockAsync(WorkspaceId, default);
            writer.Projects.Add(new Project
            {
                WorkspaceId = WorkspaceId,
                Name = "Rolled back",
                Slug = "rolled-back"
            });
            await writer.SaveChangesAsync();
            // No CommitAsync: leaving the creation path early must not leave the resource behind.
        }

        await using var reader = PostgresLane.Open(connectionString);
        (await reader.Projects.AsNoTracking().CountAsync(p => p.WorkspaceId == WorkspaceId))
            .Should().Be(0);
    }

    private static QuotaService Service(HarboraDbContext db) =>
        new(db, Options.Create(new BillingOptions { Enabled = true }));

    private static async Task SeedAsync(string connectionString)
    {
        await using var db = PostgresLane.Open(connectionString);
        var plan = new Plan
        {
            Name = "One project",
            MaxProjects = 1,
            IsEnabled = true
        };
        db.Plans.Add(plan);
        db.Workspaces.Add(new Workspace
        {
            Id = WorkspaceId,
            Name = "Race",
            Slug = "race",
            PlanId = plan.Id
        });
        await db.SaveChangesAsync();
    }
}
