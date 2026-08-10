using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// A global query filter and <c>ExecuteDeleteAsync</c> in the same statement.
///
/// <para>
/// This pairing is the one the audit named first, and for a reason: <c>ExecuteDelete</c> issues a
/// <c>DELETE</c> the provider composes — nothing is loaded, nothing is tracked, and there is no
/// second chance to notice that a predicate did not make it into the SQL. EF InMemory does not
/// support the operation at all, so every one of the panel's <c>ExecuteDeleteAsync</c> calls is
/// unexercised by the fast suite. What could go wrong is not subtle: a filter that silently fails to
/// compose deletes another tenant's rows.
/// </para>
///
/// <para>
/// The other half is the one this codebase has already been bitten by — a sweeper that runs with a
/// request scope reads an empty table, deletes nothing and reports a clean pass. Both directions are
/// below.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class WorkspaceFilterDeleteTests(PostgresLane lane)
{
    private static readonly Guid TenantOne = new("31111111-0000-0000-0000-000000000001");
    private static readonly Guid TenantTwo = new("31111111-0000-0000-0000-000000000002");

    [PostgresFact]
    public async Task A_scoped_delete_takes_only_the_callers_own_rows()
    {
        var connectionString = await SeededAsync("scoped_delete");

        await using (var scoped = PostgresLane.Open(connectionString, new FixedWorkspaceScope(TenantOne)))
            (await scoped.Alerts.ExecuteDeleteAsync()).Should().Be(2);

        (await SurvivorsAsync(connectionString)).Should().Equal(TenantTwo);
    }

    [PostgresFact]
    public async Task A_request_with_no_workspace_deletes_nothing()
    {
        // An unauthenticated request resolves to Guid.Empty, which matches nothing. That is the
        // model's deny-by-default, and here it is as a DELETE rather than as a SELECT.
        var connectionString = await SeededAsync("unscoped_request");

        await using (var anonymous = PostgresLane.Open(connectionString, new FixedWorkspaceScope(Guid.Empty)))
            (await anonymous.Alerts.ExecuteDeleteAsync()).Should().Be(0);

        (await SurvivorsAsync(connectionString)).Should().Equal(TenantOne, TenantOne, TenantTwo);
    }

    [PostgresFact]
    public async Task A_sweeper_that_says_it_ignores_the_filter_reaches_every_tenant_whatever_scope_it_is_in()
    {
        // DataRetentionSweeper's exact shape. It deliberately does not rely on the ambient scope
        // being the system one — this is the assertion that saying so out loud actually works when
        // the statement is a DELETE rather than a query, and under the worst scope it could be given.
        var connectionString = await SeededAsync("sweeper");

        await using (var wronglyScoped = PostgresLane.Open(connectionString, new FixedWorkspaceScope(TenantTwo)))
            (await wronglyScoped.Alerts.IgnoreQueryFilters().ExecuteDeleteAsync()).Should().Be(3);

        (await SurvivorsAsync(connectionString)).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_controllers_delete_of_another_tenants_row_by_id_removes_nothing()
    {
        // AlertsController deletes by id AND workspace, on top of the filter. Belt and braces, and
        // the braces are what this checks: the id is real and guessable from a URL, and the row
        // still does not go.
        var connectionString = await SeededAsync("cross_tenant");

        Guid otherTenantsAlert;
        await using (var system = PostgresLane.Open(connectionString))
            otherTenantsAlert = await system.Alerts.Where(a => a.WorkspaceId == TenantTwo)
                .Select(a => a.Id).SingleAsync();

        await using (var scoped = PostgresLane.Open(connectionString, new FixedWorkspaceScope(TenantOne)))
        {
            var deleted = await scoped.Alerts
                .Where(a => a.Id == otherTenantsAlert && a.WorkspaceId == TenantOne)
                .ExecuteDeleteAsync();

            deleted.Should().Be(0);
        }

        (await SurvivorsAsync(connectionString)).Should().Contain(TenantTwo);
    }

    [PostgresFact]
    public async Task A_scoped_read_and_a_scoped_delete_agree_about_what_is_there()
    {
        // The failure this would catch is a filter that composes into SELECT but not into DELETE:
        // the page would show two rows and "delete all" would take three.
        var connectionString = await SeededAsync("read_and_delete");

        await using var scoped = PostgresLane.Open(connectionString, new FixedWorkspaceScope(TenantOne));

        var visible = await scoped.Alerts.CountAsync();
        var deleted = await scoped.Alerts.ExecuteDeleteAsync();

        deleted.Should().Be(visible);
    }

    [PostgresFact]
    public async Task A_scoped_update_of_another_tenants_app_changes_nothing_and_says_nothing()
    {
        // The half of AppOperationsService that has no voice. Its SetStatusAsync is the single place
        // an app's status is written, and it writes with ExecuteUpdate — which folds the tenant
        // filter into the UPDATE's WHERE. Called about another workspace's app it matches no rows,
        // raises nothing, and returns as though it had worked: the panel reports the app stopped, the
        // container keeps running, and the hourly tick keeps billing it at the running rate.
        //
        // That is why the fix is IgnoreQueryFilters on ResolveAsync AND on SetStatusAsync together.
        // The read half throws loudly and is pinned by a fast-lane test; this half is silent and only
        // exists as SQL, so it is pinned here. Both directions are below.
        var connectionString = await lane.FreshlyMigratedAsync("scoped_update");

        var theirApp = new App { WorkspaceId = TenantTwo, Name = "api", Slug = "api", Status = AppStatus.Running };
        await using (var system = PostgresLane.Open(connectionString))
        {
            system.Apps.Add(theirApp);
            await system.SaveChangesAsync();
        }

        await using (var scoped = PostgresLane.Open(connectionString, new FixedWorkspaceScope(TenantOne)))
        {
            var filtered = await scoped.Apps.Where(a => a.Id == theirApp.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AppStatus.Stopped));

            filtered.Should().Be(0,
                "the filter composed into the UPDATE, so the statement reported success having " +
                "changed nothing — which is the shape nobody sees");
        }

        await using (var scoped = PostgresLane.Open(connectionString, new FixedWorkspaceScope(TenantOne)))
        {
            var unfiltered = await scoped.Apps.IgnoreQueryFilters().Where(a => a.Id == theirApp.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AppStatus.Stopped));

            unfiltered.Should().Be(1, "saying it ignores the filter has to actually reach the row");
        }

        await using (var system = PostgresLane.Open(connectionString))
            (await system.Apps.AsNoTracking().SingleAsync(a => a.Id == theirApp.Id))
                .Status.Should().Be(AppStatus.Stopped);
    }

    /// <summary>Two rows for one tenant and one for another, written without a scope in the way.</summary>
    private async Task<string> SeededAsync(string label)
    {
        var connectionString = await lane.FreshlyMigratedAsync(label);

        await using var system = PostgresLane.Open(connectionString);
        system.Alerts.AddRange(
            Alert(TenantOne, "first"),
            Alert(TenantOne, "second"),
            Alert(TenantTwo, "theirs"));
        await system.SaveChangesAsync();

        return connectionString;
    }

    private static async Task<IReadOnlyList<Guid>> SurvivorsAsync(string connectionString)
    {
        await using var system = PostgresLane.Open(connectionString);
        return await system.Alerts.AsNoTracking()
            .OrderBy(a => a.WorkspaceId).ThenBy(a => a.Name)
            .Select(a => a.WorkspaceId).ToListAsync();
    }

    private static Alert Alert(Guid workspaceId, string name) =>
        new() { WorkspaceId = workspaceId, Name = name, EncryptedTarget = "-" };
}
