using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Features;
using Harbora.Domain.Identity;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Features;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The gate over real rows.
///
/// <para>
/// <see cref="FeatureAccessTestsSeeAlso"/> covers precedence as arithmetic; this covers the part
/// that reads the database and can therefore be wrong in ways a pure function cannot: reading the
/// wrong plan, confusing a plan id with a workspace id because both live in one column, or coming
/// back empty because a filter hid the rows.
/// </para>
/// </summary>
public class FeatureGateTests
{
    private const string Key = PlatformFeatures.Functions;

    private static HarboraDbContext NewDb() => new(
        new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("gate-" + Guid.NewGuid()).Options);

    private static async Task<(Guid Workspace, Guid Plan)> SeedAsync(HarboraDbContext db, bool planIsDefault = false)
    {
        var plan = new Plan { Name = "Starter", IsDefault = planIsDefault, IsEnabled = true };
        var workspace = new Workspace { Name = "Acme", Slug = "acme", PlanId = plan.Id };
        db.Plans.Add(plan);
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        return (workspace.Id, plan.Id);
    }

    private static void Grant(HarboraDbContext db, FeatureScope scope, Guid targetId, FeatureState state) =>
        db.FeatureGrants.Add(new FeatureGrant
        {
            Scope = scope, TargetId = targetId, FeatureKey = Key, State = state
        });

    [Fact]
    public async Task With_no_grants_a_workspace_gets_the_shipped_default()
    {
        await using var db = NewDb();
        var (workspace, _) = await SeedAsync(db);

        var verdict = await new FeatureGate(db).EvaluateAsync(workspace, Key, default);

        verdict.State.Should().Be(PlatformFeatures.DefaultFor(Key));
        verdict.DecidedBy.Should().Be(FeatureDecision.ShippedDefault);
    }

    [Fact]
    public async Task A_grant_on_the_workspaces_plan_reaches_it()
    {
        await using var db = NewDb();
        var (workspace, plan) = await SeedAsync(db);
        Grant(db, FeatureScope.Plan, plan, FeatureState.Enabled);
        await db.SaveChangesAsync();

        var verdict = await new FeatureGate(db).EvaluateAsync(workspace, Key, default);

        verdict.IsEnabled.Should().BeTrue();
        verdict.DecidedBy.Should().Be(FeatureDecision.Plan);
    }

    [Fact]
    public async Task A_grant_on_someone_elses_plan_does_not()
    {
        await using var db = NewDb();
        var (workspace, _) = await SeedAsync(db);
        var otherPlan = new Plan { Name = "Pro", IsEnabled = true };
        db.Plans.Add(otherPlan);
        Grant(db, FeatureScope.Plan, otherPlan.Id, FeatureState.Enabled);
        await db.SaveChangesAsync();

        var verdict = await new FeatureGate(db).EvaluateAsync(workspace, Key, default);

        verdict.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_workspace_id_in_a_plan_scoped_row_is_not_mistaken_for_a_plan()
    {
        // Both ids live in one TargetId column. Without the scope discriminator being part of the
        // match, a workspace override would answer a plan lookup and vice versa.
        await using var db = NewDb();
        var (workspace, _) = await SeedAsync(db);
        Grant(db, FeatureScope.Plan, workspace, FeatureState.Enabled);
        await db.SaveChangesAsync();

        var verdict = await new FeatureGate(db).EvaluateAsync(workspace, Key, default);

        verdict.IsEnabled.Should().BeFalse("that row is scoped to a plan, and this id is a workspace");
    }

    [Fact]
    public async Task A_workspace_override_outranks_its_plan()
    {
        await using var db = NewDb();
        var (workspace, plan) = await SeedAsync(db);
        Grant(db, FeatureScope.Plan, plan, FeatureState.Locked);
        Grant(db, FeatureScope.Workspace, workspace, FeatureState.Enabled);
        await db.SaveChangesAsync();

        var verdict = await new FeatureGate(db).EvaluateAsync(workspace, Key, default);

        verdict.IsEnabled.Should().BeTrue();
        verdict.DecidedBy.Should().Be(FeatureDecision.Workspace);
    }

    [Fact]
    public async Task A_workspace_with_no_plan_falls_back_to_the_default_plan()
    {
        // A customer nobody assigned a plan to is not a customer entitled to everything — the same
        // fallback IQuotaService applies to their caps.
        await using var db = NewDb();
        var (_, plan) = await SeedAsync(db, planIsDefault: true);
        var orphan = new Workspace { Name = "Orphan", Slug = "orphan", PlanId = null };
        db.Workspaces.Add(orphan);
        Grant(db, FeatureScope.Plan, plan, FeatureState.Enabled);
        await db.SaveChangesAsync();

        var verdict = await new FeatureGate(db).EvaluateAsync(orphan.Id, Key, default);

        verdict.IsEnabled.Should().BeTrue();
        verdict.DecidedBy.Should().Be(FeatureDecision.Plan);
    }

    [Fact]
    public async Task A_workspace_that_does_not_exist_is_not_entitled_to_anything()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var verdict = await new FeatureGate(db).EvaluateAsync(Guid.CreateVersion7(), Key, default);

        verdict.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Every_catalogue_feature_comes_back_from_one_call()
    {
        // The sidebar asks about all of them on every render; asking one at a time would be one
        // query per feature per page.
        await using var db = NewDb();
        var (workspace, _) = await SeedAsync(db);

        var all = await new FeatureGate(db).EvaluateAllAsync(workspace, default);

        all.Keys.Should().BeEquivalentTo(PlatformFeatures.All.Select(f => f.Key));
    }

    [Fact]
    public async Task An_unknown_key_fails_closed_even_with_grants_present()
    {
        await using var db = NewDb();
        var (workspace, plan) = await SeedAsync(db);
        Grant(db, FeatureScope.Plan, plan, FeatureState.Enabled);
        await db.SaveChangesAsync();

        var verdict = await new FeatureGate(db).EvaluateAsync(workspace, "nothing.reads.this", default);

        verdict.State.Should().Be(FeatureState.Hidden);
    }

    [Fact]
    public async Task Grants_are_readable_without_a_session()
    {
        // The scheduler and the event bus have no workspace scope. If this table carried a tenant
        // filter they would read nothing and decide nobody is entitled to anything — silently, on
        // every tick.
        await using var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase("gate-scoped-" + Guid.NewGuid()).Options,
            new FixedWorkspaceScope(Guid.CreateVersion7()));

        var (workspace, plan) = await SeedAsync(db);
        Grant(db, FeatureScope.Plan, plan, FeatureState.Enabled);
        await db.SaveChangesAsync();

        var verdict = await new FeatureGate(db).EvaluateAsync(workspace, Key, default);

        verdict.IsEnabled.Should().BeTrue();
    }
}

/// <summary>A scope pinned to somebody else's workspace, so a tenant filter would bite if one existed.</summary>
internal sealed class FixedWorkspaceScope(Guid workspaceId) : Harbora.Application.Abstractions.IWorkspaceScope
{
    public Guid WorkspaceId => workspaceId;
    public bool IsUnscoped => false;
}

/// <summary>Marker for the cross-reference in this file's summary; see FeatureEntitlementTests.</summary>
internal static class FeatureAccessTestsSeeAlso;
