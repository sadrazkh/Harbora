using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// The platform's stop/start route, stood in for.
///
/// <para>
/// It is a fake rather than the real <c>AppOperationsService</c> for one mechanical reason: that
/// service writes the status with <c>ExecuteUpdate</c>, which EF InMemory does not implement at all
/// (the same wall <c>CapabilityPolicyHttpTests</c> and <c>WorkspaceFilterDeleteTests</c> both name).
/// So it records what it was asked to do and applies the one effect its contract promises — the app
/// ends up <see cref="AppStatus.Stopped"/> or <see cref="AppStatus.Running"/>.
/// </para>
///
/// <para>
/// It writes through a context of its <b>own</b>, and that is not tidiness. <c>ExecuteUpdate</c>
/// never reaches the calling context's change tracker, so in production the code under test holds a
/// stale copy of every app the route touched. A fake sharing that context would keep the two in step
/// and quietly excuse a verification read that forgot <c>AsNoTracking</c> — which is the read the
/// whole "did it actually stop?" question rests on.
/// </para>
///
/// <para>
/// <see cref="ReportsSuccessWithoutDoingAnything"/> is the mode that earns this fake its keep. A stop
/// route that returns without an exception and without stopping anything is the exact shape this
/// branch keeps finding — and suspension is the one place where believing it costs a customer money
/// they do not have.
/// </para>
/// </summary>
internal sealed class FakeAppOperations(BillingContext own) : IAppOperationsService
{
    /// <summary>Every app the code under test asked to stop, in order.</summary>
    public List<Guid> Stopped { get; } = [];

    /// <summary>Every app the code under test asked to start, in order.</summary>
    public List<Guid> Started { get; } = [];

    /// <summary>Apps whose stop or start throws — an unreachable node, most often.</summary>
    public Dictionary<Guid, string> Refuses { get; } = [];

    /// <summary>Apps the route accepts, records and then leaves exactly as they were.</summary>
    public HashSet<Guid> ReportsSuccessWithoutDoingAnything { get; } = [];

    /// <summary>
    /// What the table said about each app's suspension marker at the instant the route was called.
    ///
    /// <para>
    /// Ordering is a claim a fake returning canned values cannot check, and this one is load-bearing:
    /// the marker has to be durable before a container is touched, or a panel that dies mid-pass
    /// forgets what it was in the middle of stopping.
    /// </para>
    /// </summary>
    public Dictionary<Guid, bool> MarkedWhenCalled { get; } = [];

    public Task StopAsync(Guid appId, CancellationToken ct)
    {
        Stopped.Add(appId);
        return ApplyAsync(appId, AppStatus.Stopped, ct);
    }

    public Task StartAsync(Guid appId, CancellationToken ct)
    {
        Started.Add(appId);
        return ApplyAsync(appId, AppStatus.Running, ct);
    }

    private async Task ApplyAsync(Guid appId, AppStatus status, CancellationToken ct)
    {
        var app = await own.Apps.IgnoreQueryFilters().AsNoTracking().FirstAsync(a => a.Id == appId, ct);
        MarkedWhenCalled[appId] = app.WasRunningAtSuspension;

        if (Refuses.TryGetValue(appId, out var reason)) throw new InvalidOperationException(reason);
        if (ReportsSuccessWithoutDoingAnything.Contains(appId)) return;

        var tracked = await own.Apps.IgnoreQueryFilters().FirstAsync(a => a.Id == appId, ct);
        tracked.Status = status;
        await own.SaveChangesAsync(ct);
    }

    public Task RestartAsync(Guid appId, CancellationToken ct) => StartAsync(appId, ct);
    public Task DeleteAsync(Guid appId, bool removeVolumes, CancellationToken ct) => throw new NotSupportedException();
    public Task<string> GetLogsAsync(Guid appId, int tail, CancellationToken ct) => throw new NotSupportedException();
    public Task<LogSearchResult> SearchLogsAsync(
        IReadOnlyList<Guid> appIds, string? text, bool problemsOnly, TimeSpan? window, int maxLinesPerApp,
        CancellationToken ct) => throw new NotSupportedException();
    public Task<MaintenanceToggleResult> SetMaintenanceModeAsync(
        Guid appId, bool enabled, string? messageEn, string? messageFa, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<RateLimitToggleResult> SetRateLimitAsync(
        Guid appId, bool enabled, int average, int burst, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<LogRetentionResult> SetLogRetentionAsync(Guid appId, int days, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>
/// The platform's database stop/start route, stood in for, on exactly the terms
/// <see cref="FakeAppOperations"/> is.
///
/// <para>
/// The real <c>ManagedServiceEngine</c> cannot be driven here: <c>StartAsync</c> and
/// <c>StopAsync</c> resolve a Docker engine for the service's server before they write anything, and
/// there is no daemon on this machine. So this records what it was asked to do and applies the one
/// effect the contract promises — the service ends <see cref="ServiceStatus.Stopped"/> or
/// <see cref="ServiceStatus.Running"/>.
/// </para>
///
/// <para>
/// It writes through its <b>own</b> context for the same reason the app fake does, and the reason is
/// sharper here: the real engine's reads go <i>through</i> the tenant filter
/// (<c>db.ManagedServices.FirstAsync</c>), so a fake sharing the caller's context would hide the
/// staleness the verification read exists to catch.
/// </para>
///
/// <para>
/// Everything <see cref="BillingSuspension"/> never calls throws rather than returning a plausible
/// empty answer, following <c>FakeManagedServiceEngine</c>'s own rule: a test that reaches one of
/// those has wandered somewhere it did not mean to and should say so.
/// </para>
/// </summary>
internal sealed class FakeDatabaseOperations(BillingContext own) : IManagedServiceEngine
{
    /// <summary>Every database the code under test asked to stop, in order.</summary>
    public List<Guid> Stopped { get; } = [];

    /// <summary>Every database the code under test asked to start, in order.</summary>
    public List<Guid> Started { get; } = [];

    /// <summary>Databases whose stop or start throws — an unreachable node, most often.</summary>
    public Dictionary<Guid, string> Refuses { get; } = [];

    /// <summary>Databases the route accepts, records and then leaves exactly as they were.</summary>
    public HashSet<Guid> ReportsSuccessWithoutDoingAnything { get; } = [];

    /// <summary>What the table said about each service's marker at the instant the route was called.</summary>
    public Dictionary<Guid, bool> MarkedWhenCalled { get; } = [];

    public Task StopAsync(Guid serviceId, CancellationToken ct)
    {
        Stopped.Add(serviceId);
        return ApplyAsync(serviceId, ServiceStatus.Stopped, ct);
    }

    public Task StartAsync(Guid serviceId, CancellationToken ct)
    {
        Started.Add(serviceId);
        return ApplyAsync(serviceId, ServiceStatus.Running, ct);
    }

    private async Task ApplyAsync(Guid serviceId, ServiceStatus status, CancellationToken ct)
    {
        var svc = await own.ManagedServices.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(s => s.Id == serviceId, ct);
        MarkedWhenCalled[serviceId] = svc.WasRunningAtSuspension;

        if (Refuses.TryGetValue(serviceId, out var reason)) throw new InvalidOperationException(reason);
        if (ReportsSuccessWithoutDoingAnything.Contains(serviceId)) return;

        var tracked = await own.ManagedServices.IgnoreQueryFilters().FirstAsync(s => s.Id == serviceId, ct);
        tracked.Status = status;
        await own.SaveChangesAsync(ct);
    }

    public IReadOnlyList<ServiceCatalogEntry> Catalog => throw new NotSupportedException();
    public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task RemoveAsync(Guid serviceId, bool deleteData, CancellationToken ct) => throw new NotSupportedException();
    public Task<long?> MeasureStorageAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<RotatedApp>> RotatePasswordAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> TestConnectionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<ServiceConnectionInfo> GetConnectionInfoAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, string>> BuildAttachEnvAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<RedisMemoryPolicyOutcome> UpdateRedisMemoryPolicyAsync(Guid serviceId, string? policy, long maxMemoryBytes, CancellationToken ct) => throw new NotSupportedException();
    public Task<(bool Ok, string? Error)> PromoteReplicaAsync(Guid replicaId, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>
/// Stopping a customer's workloads because their balance ran out, and bringing back exactly what the
/// outage took away.
///
/// <para>
/// Every test here drives <see cref="Harbora.Infrastructure.Billing.BillingSuspension"/> through a
/// context scoped to <see cref="Guid.Empty"/> — see <c>Harness.Suspension</c>. That is not incidental
/// colour: suspension is sessionless background work, and under a request scope the app table reads
/// as empty. A suspension that found no apps would stop nothing, record nothing about what was
/// running, and report a clean pass.
/// </para>
/// </summary>
public class BillingSuspensionTests
{
    // --- stopping ---------------------------------------------------------------------------

    [Fact]
    public async Task Suspending_for_no_balance_stops_the_running_apps()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);

        (await db.Apps.SingleAsync(a => a.WorkspaceId == ws)).Status.Should().Be(AppStatus.Stopped);
        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeTrue();
    }

    [Fact]
    public async Task Suspending_goes_through_the_platforms_own_stop_route()
    {
        // Not `docker stop`. The route resolves the app's server engine, so it works for a remote
        // node as well as the panel's own daemon, and it is the single place the status is written.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();
        var app = await db.Apps.AsNoTracking().SingleAsync(a => a.WorkspaceId == ws);

        var ops = Harness.Operations(db);
        await Harness.Suspension(db, ops).SuspendAsync(ws, default);

        ops.Stopped.Should().Equal(app.Id);
    }

    [Fact]
    public async Task What_was_running_is_written_down_before_anything_is_stopped()
    {
        // The node agent's rule, borrowed: the drain flag is persisted before any stop. If the panel
        // dies halfway through stopping ten apps, resumption must still know all ten were running —
        // otherwise the outage silently becomes the customer's new configuration. Ordering is not
        // something a result can be asked about afterwards, so the route reports what the table said
        // at the moment it was called.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();
        var app = await db.Apps.AsNoTracking().SingleAsync(a => a.WorkspaceId == ws);

        var ops = Harness.Operations(db);
        await Harness.Suspension(db, ops).SuspendAsync(ws, default);

        ops.MarkedWhenCalled[app.Id].Should().BeTrue(
            "a stop issued before the record is durable is a stop nothing can undo");
    }

    [Fact]
    public async Task A_stop_the_node_refused_leaves_what_was_running_written_down()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();
        var app = await db.Apps.AsNoTracking().SingleAsync(a => a.WorkspaceId == ws);

        var ops = Harness.Operations(db);
        ops.Refuses[app.Id] = "the node is unreachable";

        var result = await Harness.Suspension(db, ops).SuspendAsync(ws, default);

        (await db.Apps.SingleAsync(a => a.Id == app.Id)).WasRunningAtSuspension.Should().BeTrue(
            "the stop failed, and what was running is exactly what a retry needs to know");
        result.Failures.Should().ContainSingle(f => f.Contains("unreachable"));
    }

    [Fact]
    public async Task A_stop_that_reports_success_without_stopping_anything_is_not_believed()
    {
        // The shape this whole branch keeps finding. The route returns, no exception is raised, and
        // the container is still up burning a balance the customer no longer has. Asking the table
        // again afterwards is the only thing that tells the difference.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();
        var app = await db.Apps.AsNoTracking().SingleAsync(a => a.WorkspaceId == ws);

        var ops = Harness.Operations(db);
        ops.ReportsSuccessWithoutDoingAnything.Add(app.Id);

        var result = await Harness.Suspension(db, ops).SuspendAsync(ws, default);

        result.AppsStopped.Should().Be(0);
        result.Failures.Should().ContainSingle(f => f.Contains("tenant-api") && f.Contains("still running"));
    }

    [Fact]
    public async Task A_second_suspension_stops_what_the_first_one_could_not()
    {
        // Suspension is retried, and an already-suspended workspace is not a finished one: the first
        // pass may have lost a node halfway through. Returning early because IsSuspended is already
        // true would leave a container running for ever with every pass reporting success.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();
        var app = await db.Apps.AsNoTracking().SingleAsync(a => a.WorkspaceId == ws);

        var first = Harness.Operations(db);
        first.Refuses[app.Id] = "the node is unreachable";
        await Harness.Suspension(db, first).SuspendAsync(ws, default);

        var result = await Harness.Suspension(db).SuspendAsync(ws, default);

        result.AppsStopped.Should().Be(1);
        result.Failures.Should().BeEmpty();
        (await db.Apps.SingleAsync(a => a.Id == app.Id)).Status.Should().Be(AppStatus.Stopped);
    }

    [Fact]
    public async Task A_suspension_that_is_starting_forgets_a_mark_stranded_by_an_earlier_one()
    {
        // A marker outlives its suspension whenever the resumption never ran. Left in place, the
        // next top-up would start an app the customer stopped themselves months ago and spend the
        // money they had just put in.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);

        // The flags cleared without the markers being read, staged directly against the table. The
        // console's resume button used to be the everyday way to produce this state and no longer
        // is: a NoBalance resume now goes through ResumeAsync, which starts what it can and clears
        // the marker of everything that came back (TenantsControllerResumeTests). What is left is
        // the stranding this pass still has to survive — a workspace whose flags were cleared by a
        // release older than that fix, by a hand at the database, or by a resumption killed halfway.
        var workspace = await db.Workspaces.SingleAsync(w => w.Id == ws);
        workspace.IsSuspended = false;
        workspace.SuspendedReason = SuspensionReason.None;
        await db.SaveChangesAsync();

        (await db.Apps.SingleAsync(a => a.Slug == "api")).WasRunningAtSuspension.Should().BeTrue(
            "the fixture is only worth anything if the stale mark is really there");

        // Months later the balance runs out again, with "api" long since stopped.
        await Harness.Suspension(db).SuspendAsync(ws, default);

        (await db.Apps.SingleAsync(a => a.Slug == "api")).WasRunningAtSuspension.Should().BeFalse(
            "it was not running when this suspension began, whatever an older one recorded");
    }

    [Fact]
    public async Task A_suspension_that_is_retrying_does_not_forget_what_the_first_pass_recorded()
    {
        // The other direction, and the one that costs a customer their service. By the second pass
        // the apps are stopped precisely because the first pass stopped them, so rebuilding the set
        // from what is running now would erase the only record that they were ever running — the
        // retry meant to finish the job would be the thing that made the outage permanent.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);
        await Harness.Suspension(db).SuspendAsync(ws, default);
        await Harness.Suspension(db).ResumeAsync(ws, default);

        (await db.Apps.SingleAsync(a => a.Slug == "api")).Status.Should().Be(AppStatus.Running);
        (await db.Apps.SingleAsync(a => a.Slug == "worker")).Status.Should().Be(AppStatus.Stopped);
    }

    [Fact]
    public async Task The_providers_own_workspace_is_never_stopped_for_money()
    {
        // The console already refuses this by hand ("The provider workspace cannot be suspended"),
        // and a background job must refuse it too: the default workspace is where the platform's own
        // workloads live, so suspending it takes the panel down to collect a debt owed to itself.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "provider", ratePerHour: 100);
        await db.SaveChangesAsync();

        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsDefault = true;
        await db.SaveChangesAsync();

        var result = await Harness.Suspension(db).SuspendAsync(ws, default);

        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeFalse();
        (await db.Apps.SingleAsync(a => a.WorkspaceId == ws)).Status.Should().Be(AppStatus.Running);
        result.Failures.Should().ContainSingle(f => f.Contains("provider"));
    }

    [Fact]
    public async Task Billing_that_is_switched_off_stops_nobody()
    {
        // Off is the shipped default. An install that upgraded into billing unasked must not stop a
        // tenant's workloads over a balance nobody told them existed.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();

        var result = await Harness.Suspension(db, enabled: false).SuspendAsync(ws, default);

        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeFalse();
        (await db.Apps.SingleAsync(a => a.WorkspaceId == ws)).Status.Should().Be(AppStatus.Running);
        result.Failures.Should().ContainSingle(f => f.Contains("Billing:Enabled"));
    }

    [Fact]
    public async Task An_app_the_deploy_pipeline_is_still_working_on_is_left_alone()
    {
        // Deployments own their own state machine and it throws on an illegal transition. Suspension
        // reaching into a Deploying app would be a second writer on that path; the app is charged for
        // the hour either way, and the deploy finishes into a workspace that already blocks the next
        // one.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: 100, status: AppStatus.Deploying);
        await db.SaveChangesAsync();

        var ops = Harness.Operations(db);
        await Harness.Suspension(db, ops).SuspendAsync(ws, default);

        ops.Stopped.Should().BeEmpty();
        (await db.Apps.SingleAsync(a => a.WorkspaceId == ws)).Status.Should().Be(AppStatus.Deploying);
    }

    // --- the database, which the suspension used not to touch at all --------------------------
    //
    // The worst combination this branch could produce, and until now it did: the hourly pass charges
    // a managed database for its size and its disk, and the suspension stopped only apps. So a
    // workspace with no balance had its site taken down, its database left running, and the running
    // database still billed — while the customer could not stop it themselves, because being
    // suspended is what blocks them.

    [Fact]
    public async Task Suspending_for_no_balance_stops_the_running_databases_too()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        var database = Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        var result = await Harness.Suspension(db).SuspendAsync(ws, default);

        (await db.ManagedServices.SingleAsync(s => s.Id == database)).Status
            .Should().Be(ServiceStatus.Stopped);
        result.DatabasesStopped.Should().Be(1);
    }

    [Fact]
    public async Task Suspending_goes_through_the_platforms_own_database_stop_route()
    {
        // Not `docker stop`, for the reason the app half gives: the route resolves the service's
        // server engine, so it works for a remote node, and it is the single place ServiceStatus is
        // written.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        var database = Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        var databases = Harness.Databases(db);
        await Harness.Suspension(db, databases: databases).SuspendAsync(ws, default);

        databases.Stopped.Should().Equal(database);
    }

    [Fact]
    public async Task What_was_running_is_written_down_before_a_database_is_stopped()
    {
        // The node agent's drain rule, applied to a database. A panel that dies between the stop and
        // the write has lost the only record that this database was ever running, and a top-up then
        // brings back everything except the data layer everything else needs.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        var database = Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        var databases = Harness.Databases(db);
        await Harness.Suspension(db, databases: databases).SuspendAsync(ws, default);

        databases.MarkedWhenCalled[database].Should().BeTrue(
            "a stop issued before the record is durable is a stop nothing can undo");
    }

    [Fact]
    public async Task A_database_stop_that_reports_success_without_stopping_anything_is_not_believed()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        var database = Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        var databases = Harness.Databases(db);
        databases.ReportsSuccessWithoutDoingAnything.Add(database);

        var result = await Harness.Suspension(db, databases: databases).SuspendAsync(ws, default);

        result.DatabasesStopped.Should().Be(0);
        result.Failures.Should().ContainSingle(f => f.Contains("tenant-db") && f.Contains("still running"));
    }

    [Fact]
    public async Task A_database_stop_the_node_refused_leaves_what_was_running_written_down()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        var database = Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        var databases = Harness.Databases(db);
        databases.Refuses[database] = "the node is unreachable";

        var result = await Harness.Suspension(db, databases: databases).SuspendAsync(ws, default);

        (await db.ManagedServices.SingleAsync(s => s.Id == database)).WasRunningAtSuspension
            .Should().BeTrue("the stop failed, and what was running is exactly what a retry needs");
        result.Failures.Should().ContainSingle(f => f.Contains("unreachable"));
    }

    [Fact]
    public async Task A_database_still_being_provisioned_is_left_to_the_job_that_is_provisioning_it()
    {
        // The counterpart of leaving a Deploying app alone. A provision is mid-flight on the job
        // queue and writes ServiceStatus itself; stopping the container underneath it would make the
        // suspension a second writer on that path, and the provision would finish into a workspace
        // that already refuses to start anything.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        Harness.AddDatabase(db, ws, "tenant-db", status: ServiceStatus.Provisioning);
        await db.SaveChangesAsync();

        var databases = Harness.Databases(db);
        await Harness.Suspension(db, databases: databases).SuspendAsync(ws, default);

        databases.Stopped.Should().BeEmpty();
        (await db.ManagedServices.SingleAsync()).Status.Should().Be(ServiceStatus.Provisioning);
    }

    [Fact]
    public async Task Resuming_starts_only_the_databases_that_were_running_when_it_was_suspended()
    {
        // The rule that matters to a customer, on the resource they can least afford to have started
        // by accident: a database they stopped themselves must not come back and start spending the
        // money they just put in.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        var live = Harness.AddDatabase(db, ws, "orders");
        var parked = Harness.AddDatabase(db, ws, "archive", status: ServiceStatus.Stopped);
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);
        var result = await Harness.Suspension(db).ResumeAsync(ws, default);

        (await db.ManagedServices.SingleAsync(s => s.Id == live)).Status
            .Should().Be(ServiceStatus.Running);
        (await db.ManagedServices.SingleAsync(s => s.Id == parked)).Status
            .Should().Be(ServiceStatus.Stopped,
                "the customer stopped this one themselves, and a top-up is not a request to start it");
        result.DatabasesStarted.Should().Be(1);
    }

    [Fact]
    public async Task A_suspension_that_is_retrying_does_not_forget_which_databases_the_first_pass_stopped()
    {
        // Task 5's rule, and the reason it exists, arriving on the database. By the second pass the
        // database is Stopped precisely BECAUSE the first pass stopped it. A marker set rebuilt from
        // what is running now would clear it, the top-up would bring back the apps and not the data
        // layer under them, and every pass would report success.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        var database = Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);
        await Harness.Suspension(db).SuspendAsync(ws, default);
        await Harness.Suspension(db).ResumeAsync(ws, default);

        (await db.ManagedServices.SingleAsync(s => s.Id == database)).Status
            .Should().Be(ServiceStatus.Running, "the retry must not erase what the first pass recorded");
    }

    [Fact]
    public async Task A_suspension_that_is_starting_forgets_a_database_mark_stranded_by_an_earlier_one()
    {
        // The other half of the same rule. A marker outlives its suspension whenever the resumption
        // never finished — an operator clearing the flag from the console is the everyday way — and
        // left in place it would make a later top-up start a database the customer stopped months
        // ago and bill them for it.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        var database = Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);

        // The operator lifts it from the console, which clears the flags and not the markers.
        var workspace = await db.Workspaces.SingleAsync(w => w.Id == ws);
        workspace.IsSuspended = false;
        workspace.SuspendedReason = SuspensionReason.None;
        await db.SaveChangesAsync();

        // A fresh suspension months later, with the database still stopped by the customer's choice.
        await Harness.Suspension(db).SuspendAsync(ws, default);
        await Harness.Suspension(db).ResumeAsync(ws, default);

        (await db.ManagedServices.SingleAsync(s => s.Id == database)).Status
            .Should().Be(ServiceStatus.Stopped,
                "it was not running when this suspension started, so nothing owes it a start");
    }

    [Fact]
    public async Task A_database_that_did_not_come_back_keeps_the_workspace_suspended()
    {
        // Clearing the flags anyway would throw away the marker, which is the only record that this
        // database was ever running — and the next suspension, starting rather than retrying, would
        // then never write it again.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        var database = Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);

        var broken = Harness.Databases(db);
        broken.Refuses[database] = "the node is unreachable";
        var result = await Harness.Suspension(db, databases: broken).ResumeAsync(ws, default);

        result.WorkspaceSuspended.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("tenant-db") && f.Contains("did not come back"));
        (await db.ManagedServices.SingleAsync(s => s.Id == database)).WasRunningAtSuspension
            .Should().BeTrue("the marker is the only record there is, and a retry needs it");
    }

    [Fact]
    public async Task A_second_resume_finishes_a_database_the_first_one_could_not_start()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        var database = Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);

        var broken = Harness.Databases(db);
        broken.Refuses[database] = "the node is unreachable";
        await Harness.Suspension(db, databases: broken).ResumeAsync(ws, default);

        var result = await Harness.Suspension(db).ResumeAsync(ws, default);

        result.WorkspaceSuspended.Should().BeFalse();
        result.DatabasesStarted.Should().Be(1);
        (await db.ManagedServices.SingleAsync(s => s.Id == database)).Status
            .Should().Be(ServiceStatus.Running);
    }

    [Fact]
    public async Task Billing_that_is_switched_off_stops_no_databases_either()
    {
        // The switch guards the act that costs somebody their uptime, and a database is the thing on
        // this list a tenant can least afford to lose over a price nobody told them about.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        await Harness.Suspension(db, enabled: false).SuspendAsync(ws, default);

        (await db.ManagedServices.SingleAsync()).Status.Should().Be(ServiceStatus.Running);
    }

    [Fact]
    public async Task A_workspace_an_operator_suspended_keeps_its_databases_running_too()
    {
        // The whitelist covers the database for the same reason it covers the apps: ResumeAsync acts
        // on NoBalance alone, so stopping a database under somebody else's reason leaves it down
        // with nobody who can start it again.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        var workspace = await db.Workspaces.SingleAsync(w => w.Id == ws);
        workspace.IsSuspended = true;
        workspace.SuspendedReason = SuspensionReason.Manual;
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);

        (await db.ManagedServices.SingleAsync()).Status.Should().Be(ServiceStatus.Running);
        (await db.ManagedServices.SingleAsync()).WasRunningAtSuspension.Should().BeFalse();
    }

    [Fact]
    public async Task A_databases_marker_survives_a_pass_that_could_not_reach_the_node()
    {
        // The app half of this is already pinned; the database half is what a customer's data sits
        // on. A marker written and then lost because the stop failed is a database nothing will ever
        // bring back.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        var database = Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        var broken = Harness.Databases(db);
        broken.Refuses[database] = "the node is unreachable";
        await Harness.Suspension(db, databases: broken).SuspendAsync(ws, default);

        // The retry reaches it.
        var result = await Harness.Suspension(db).SuspendAsync(ws, default);

        result.DatabasesStopped.Should().Be(1);
        result.Failures.Should().BeEmpty();
        (await db.ManagedServices.SingleAsync(s => s.Id == database)).Status
            .Should().Be(ServiceStatus.Stopped);
    }

    // --- coming back ------------------------------------------------------------------------

    [Fact]
    public async Task Resuming_starts_only_what_was_running_when_it_was_suspended()
    {
        // The one that matters to a customer. An app they deliberately stopped last week must not
        // come back and start spending the money they just put in.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);
        await Harness.Suspension(db).ResumeAsync(ws, default);

        var apps = await db.Apps.Where(a => a.WorkspaceId == ws).ToListAsync();
        apps.Single(a => a.Slug == "api").Status.Should().Be(AppStatus.Running);
        apps.Single(a => a.Slug == "worker").Status.Should().Be(AppStatus.Stopped,
            "the customer stopped this one themselves, and a top-up is not a request to start it");
    }

    [Fact]
    public async Task Resuming_clears_the_suspension_and_the_marks_it_left()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);
        var result = await Harness.Suspension(db).ResumeAsync(ws, default);

        var workspace = await db.Workspaces.SingleAsync(w => w.Id == ws);
        workspace.IsSuspended.Should().BeFalse();
        workspace.SuspendedReason.Should().Be(SuspensionReason.None);
        result.AppsStarted.Should().Be(1);
        (await db.Apps.AnyAsync(a => a.WorkspaceId == ws && a.WasRunningAtSuspension)).Should().BeFalse();
    }

    [Fact]
    public async Task A_top_up_does_not_lift_a_suspension_an_administrator_made()
    {
        // Without a reason on the suspension, paying a bill would quietly undo an operator's
        // deliberate act — which is the sort of thing nobody notices until it matters.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedSuspendedWorkspace(db, SuspensionReason.Manual);
        await db.SaveChangesAsync();

        await Harness.Suspension(db).ResumeAsync(ws, default);

        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeTrue();
    }

    [Fact]
    public async Task A_top_up_does_not_lift_a_suspension_that_carries_no_reason_either()
    {
        // Every workspace suspended before this column existed reads as None, and so does anything
        // that sets the flag without saying why. Treating "not NoBalance" as the condition — rather
        // than "not Manual" — is what makes those safe, and it is the difference between a guard and
        // a guard-shaped hole.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedSuspendedWorkspace(db, SuspensionReason.None);
        await db.SaveChangesAsync();

        await Harness.Suspension(db).ResumeAsync(ws, default);

        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeTrue();
    }

    [Fact]
    public async Task An_operators_suspension_is_not_relabelled_when_the_money_also_runs_out()
    {
        // Both things are true at once: an operator suspended them and their balance hit zero.
        // Overwriting the reason with NoBalance would hand the customer a way to undo the operator's
        // decision by paying.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedSuspendedWorkspace(db, SuspensionReason.Manual);
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);
        await Harness.Suspension(db).ResumeAsync(ws, default);

        var workspace = await db.Workspaces.SingleAsync(w => w.Id == ws);
        workspace.SuspendedReason.Should().Be(SuspensionReason.Manual);
        workspace.IsSuspended.Should().BeTrue();
    }

    [Fact]
    public async Task An_operators_suspension_does_not_leave_apps_stopped_with_nobody_to_start_them()
    {
        // The four steps in the order a real week produces them. Step 2 is where the harm would be
        // done: stopping the apps under a reason billing does not own marks each of them as owed a
        // start, and then hands them to a resume that only ever acts on NoBalance. Step 3 sets the
        // reason back to None, closing the last path that could have started them — so the marker
        // outlives every reader of it and the containers stay down with nobody responsible.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        // 1. An operator suspends the tenant from the console. Nobody's apps are stopped by this.
        await Console(db).Suspend(ws, suspended: true, default);

        // 2. The balance runs out underneath the operator's decision.
        var result = await Harness.Suspension(db).SuspendAsync(ws, default);

        var api = await db.Apps.SingleAsync(a => a.Slug == "api");
        api.Status.Should().Be(AppStatus.Running,
            "billing stops only what lifting a billing suspension could start again");
        api.WasRunningAtSuspension.Should().BeFalse(
            "a marker nothing will read is worse than no marker at all");
        result.Failures.Should().ContainSingle(f => f.Contains("operator"));
        result.WorkspaceSuspended.Should().BeTrue(
            "billing declined to act, which does not make the workspace any less suspended");

        // 3. The operator lifts their own suspension, which clears the reason with it.
        await Console(db).Suspend(ws, suspended: false, default);

        // 4. A top-up arrives. It has nothing to undo, and the app never stopped.
        await Harness.Suspension(db).ResumeAsync(ws, default);

        (await db.Apps.SingleAsync(a => a.Slug == "api")).Status.Should().Be(AppStatus.Running);
    }

    [Fact]
    public async Task Once_an_operator_lifts_their_suspension_the_balance_stops_and_starts_the_apps_again()
    {
        // Why deferring is safe rather than merely cautious: nothing is stranded by it. The next
        // pass finds the workspace exactly as the operator left it, and does the whole job — stops
        // what is running, records it, and gives a top-up something to bring back. The operator's
        // decision only comes first.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        await Console(db).Suspend(ws, suspended: true, default);
        await Harness.Suspension(db).SuspendAsync(ws, default);
        await Console(db).Suspend(ws, suspended: false, default);

        await Harness.Suspension(db).SuspendAsync(ws, default);
        (await db.Apps.SingleAsync(a => a.Slug == "api")).Status.Should().Be(AppStatus.Stopped);

        await Harness.Suspension(db).ResumeAsync(ws, default);

        var apps = await db.Apps.Where(a => a.WorkspaceId == ws).ToListAsync();
        apps.Single(a => a.Slug == "api").Status.Should().Be(AppStatus.Running);
        apps.Single(a => a.Slug == "worker").Status.Should().Be(AppStatus.Stopped);
        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeFalse();
    }

    [Fact]
    public async Task A_suspension_that_never_said_why_is_not_taken_over_by_the_balance_either()
    {
        // The same guard's other half, and the door the reason column's own rule leaves open.
        // Every workspace suspended before that column existed reads None. Relabelling one
        // NoBalance would let a payment lift a suspension whose reason nobody knows — which is
        // precisely what asking whether the reason IS NoBalance was written to prevent.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedSuspendedWorkspace(db, SuspensionReason.None);
        db.Apps.Add(new App { WorkspaceId = ws, Name = "api", Slug = "api", Status = AppStatus.Running });
        await db.SaveChangesAsync();

        var result = await Harness.Suspension(db).SuspendAsync(ws, default);

        (await db.Workspaces.SingleAsync(w => w.Id == ws)).SuspendedReason.Should().Be(SuspensionReason.None);
        (await db.Apps.SingleAsync(a => a.Slug == "api")).Status.Should().Be(AppStatus.Running);
        result.Failures.Should().ContainSingle(f => f.Contains("did not say why"));
    }

    [Fact]
    public async Task A_resume_that_could_not_start_everything_keeps_the_suspension_and_says_which_app()
    {
        // Declaring the resume done while an app is still down loses it: the marker is the only
        // record that it was ever running, and the next suspension replaces the set. Refusal with a
        // reason beats a clean-looking pass that quietly ends somebody's service.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);
        var api = await db.Apps.AsNoTracking().SingleAsync(a => a.Slug == "api");

        var ops = Harness.Operations(db);
        ops.Refuses[api.Id] = "the node is unreachable";
        var result = await Harness.Suspension(db, ops).ResumeAsync(ws, default);

        result.Failures.Should().ContainSingle(f => f.Contains("api"));
        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeTrue();
        (await db.Apps.SingleAsync(a => a.Id == api.Id)).WasRunningAtSuspension.Should().BeTrue(
            "it is still owed a start, so the resume must remain runnable");
    }

    [Fact]
    public async Task A_resume_that_was_interrupted_can_be_run_again_and_finish()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);
        var api = await db.Apps.AsNoTracking().SingleAsync(a => a.Slug == "api");

        var broken = Harness.Operations(db);
        broken.Refuses[api.Id] = "the node is unreachable";
        await Harness.Suspension(db, broken).ResumeAsync(ws, default);

        var result = await Harness.Suspension(db).ResumeAsync(ws, default);

        result.Failures.Should().BeEmpty();
        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeFalse();
        (await db.Apps.SingleAsync(a => a.Id == api.Id)).Status.Should().Be(AppStatus.Running);
    }

    [Fact]
    public async Task A_start_that_reports_success_without_starting_anything_is_not_believed_either()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);
        var api = await db.Apps.AsNoTracking().SingleAsync(a => a.Slug == "api");

        var ops = Harness.Operations(db);
        ops.ReportsSuccessWithoutDoingAnything.Add(api.Id);
        var result = await Harness.Suspension(db, ops).ResumeAsync(ws, default);

        result.AppsStarted.Should().Be(0);
        result.Failures.Should().ContainSingle(f => f.Contains("api"));
        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeTrue();
    }

    [Fact]
    public async Task Resuming_does_not_restart_an_app_that_is_already_back()
    {
        // Start on a running container is a restart, and a restart is a visible outage. An app that
        // came back on its own — the node returned, or somebody started it — is already where the
        // resume wants it.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);
        var api = await db.Apps.SingleAsync(a => a.Slug == "api");
        api.Status = AppStatus.Running;
        await db.SaveChangesAsync();

        var ops = Harness.Operations(db);
        var result = await Harness.Suspension(db, ops).ResumeAsync(ws, default);

        ops.Started.Should().BeEmpty();
        result.AppsStarted.Should().Be(0, "nothing was started, so nothing may be counted as started");
        (await db.Apps.SingleAsync(a => a.Id == api.Id)).WasRunningAtSuspension.Should().BeFalse();
        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeFalse();
    }

    [Fact]
    public async Task Resuming_does_not_restart_a_database_that_is_already_back()
    {
        // A start on a running database is a restart, and a database's restart is an outage handed
        // to every app attached to it — given to a customer who has just paid. Three things are
        // asserted together because they are one fact: nothing was asked, nothing is counted, and
        // the marker is cleared anyway, because the database is where the resume wanted it.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        var database = Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);

        // The node came back on its own, or somebody started it by hand.
        var service = await db.ManagedServices.SingleAsync(s => s.Id == database);
        service.Status = ServiceStatus.Running;
        await db.SaveChangesAsync();

        var databases = Harness.Databases(db);
        var result = await Harness.Suspension(db, databases: databases).ResumeAsync(ws, default);

        databases.Started.Should().BeEmpty();
        result.DatabasesStarted.Should().Be(0, "nothing was started, so nothing may be counted as started");
        (await db.ManagedServices.SingleAsync(s => s.Id == database)).WasRunningAtSuspension
            .Should().BeFalse("a marker left set would start it again after the next suspension is lifted");
        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeFalse();
    }

    [Fact]
    public async Task Resuming_a_workspace_that_was_never_suspended_starts_nothing()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        var ops = Harness.Operations(db);
        var result = await Harness.Suspension(db, ops).ResumeAsync(ws, default);

        ops.Started.Should().BeEmpty();
        result.AppsStarted.Should().Be(0);
    }

    [Fact]
    public async Task A_resume_still_works_after_billing_has_been_switched_off()
    {
        // Deliberately not symmetrical with suspension. The switch guards the act that costs a
        // customer their uptime; turning billing off afterwards must not strand every workspace the
        // hour before it had already stopped.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);
        await Harness.Suspension(db, enabled: false).ResumeAsync(ws, default);

        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeFalse();
        (await db.Apps.SingleAsync(a => a.Slug == "api")).Status.Should().Be(AppStatus.Running);
    }

    // --- the reason is only worth having if somebody writes it -------------------------------

    [Fact]
    public async Task An_operator_suspending_a_tenant_records_that_a_person_did_it()
    {
        // Without this the console would keep setting the flag and leaving the reason at None, the
        // Manual branch would never be reached in production, and the test above it would be
        // asserting on a state nothing could produce.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();

        await Console(db).Suspend(ws, suspended: true, default);

        var workspace = await db.Workspaces.SingleAsync(w => w.Id == ws);
        workspace.IsSuspended.Should().BeTrue();
        workspace.SuspendedReason.Should().Be(SuspensionReason.Manual);
    }

    [Fact]
    public async Task An_operator_lifting_a_suspension_by_hand_clears_the_reason_with_it()
    {
        // A reason left behind on a workspace that is no longer suspended is a landmine: the next
        // top-up would read NoBalance, or the next audit would read Manual, about nothing.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedSuspendedWorkspace(db, SuspensionReason.Manual);
        await db.SaveChangesAsync();

        await Console(db).Suspend(ws, suspended: false, default);

        var workspace = await db.Workspaces.SingleAsync(w => w.Id == ws);
        workspace.IsSuspended.Should().BeFalse();
        workspace.SuspendedReason.Should().Be(SuspensionReason.None);
    }

    /// <summary>
    /// The provider console, built by hand. Suspending is the only action reached here and it uses
    /// none of the hasher, the quota service, the wallet, the signed-in user or the audit log, so
    /// nothing stands in for those.
    ///
    /// <para>
    /// The suspension is null for the same reason and only while it stays true: neither action
    /// reached from here is a <c>NoBalance</c> resume, which is the one branch that routes through
    /// it. <c>TenantsControllerResumeTests</c> is where that branch is driven, with a real
    /// <see cref="Harbora.Infrastructure.Billing.BillingSuspension"/> over a real engine.
    /// </para>
    /// </summary>
    private static TenantsController Console(BillingContext db)
    {
        var controller = new TenantsController(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.TempData = new TempDataDictionary(controller.HttpContext, new NullTempDataProvider());
        return controller;
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }
}
