using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// A context over an in-memory store, opened at a chosen tenancy scope.
///
/// <para>
/// Written here rather than borrowed from <c>BillingTickTests</c> so the two files can be edited
/// without colliding, and because the scope is the whole point of half of these tests: the gate is
/// asked from a cron tick, a job worker and a webhook, none of which has a session.
/// </para>
/// </summary>
internal sealed class GateContext(string store, IWorkspaceScope scope) : HarboraDbContext(
    new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(store).Options, scope)
{
    public string Store { get; } = store;
}

/// <summary>One place to build a workspace with a balance, and one place to build the gate.</summary>
internal static class GateHarness
{
    /// <summary>A context that sees every tenant — for seeding and for asserting.</summary>
    public static GateContext SystemContext(string? store = null) =>
        new(store ?? "gate-" + Guid.NewGuid(), SystemWorkspaceScope.Instance);

    /// <summary>
    /// The same rows read through the scope a sessionless caller actually gets.
    ///
    /// <para>
    /// <c>HttpWorkspaceScope</c> resolves an unauthenticated request to <see cref="Guid.Empty"/>,
    /// which matches no tenant — and <c>Wallet</c> carries a tenant filter. A gate that read the
    /// balance through this scope would find no wallet on every webhook, cron tick and queued job in
    /// the platform, and answer whatever it answers about a workspace with no wallet.
    /// </para>
    /// </summary>
    public static GateContext SessionlessContext(GateContext db) =>
        new(db.Store, new FixedWorkspaceScope(Guid.Empty));

    /// <summary>A workspace with a wallet holding <paramref name="balanceMinor"/>.</summary>
    public static Guid SeedWorkspace(
        GateContext db,
        long balanceMinor,
        bool suspended = false,
        SuspensionReason reason = SuspensionReason.None,
        bool isDefault = false,
        bool withWallet = true)
    {
        var workspace = new Workspace
        {
            Name = "Acme",
            Slug = "acme-" + Guid.NewGuid().ToString("n")[..8],
            IsDefault = isDefault,
            IsSuspended = suspended,
            SuspendedReason = reason
        };
        db.Workspaces.Add(workspace);

        if (withWallet)
            db.Wallets.Add(new Wallet { WorkspaceId = workspace.Id, BalanceMinor = balanceMinor });

        return workspace.Id;
    }

    public static BillingGate Gate(HarboraDbContext db, bool enabled = true) =>
        new(db, Options.Create(new BillingOptions { Enabled = enabled }));
}

/// <summary>
/// One assertion, asked of every refusal the gate returns.
///
/// <para>
/// The failure mode this guards against is not "ReasonFa is null" — that is a one-line assertion any
/// test could make and a lazy fix could dodge by copying the English string into the Fa slot. Checking
/// for a Persian character is the difference: it fails on null, on empty, and on a translation that
/// never actually happened, the same way it would fail if somebody pasted the English sentence twice.
/// </para>
/// </summary>
internal static class QuotaCheckAssertions
{
    public static void ShouldCarryAPersianReason(this QuotaCheck check)
    {
        check.ReasonFa.Should().NotBeNullOrWhiteSpace(
            "this refusal reaches a customer on a panel that is bilingual everywhere else, and an " +
            "English-only message is the one thing a customer already locked out should not get");

        // The Arabic-script Unicode block (U+0600-U+06FF), spelled as escapes rather than literal
        // right-to-left characters so the regex reads the same in every editor and diff tool.
        check.ReasonFa!.Should().MatchRegex("[\\u0600-\\u06FF]",
            "a Fa slot holding the English sentence again would pass a null-check and still not be " +
            "a translation a Persian speaker could read");
    }
}

/// <summary>
/// What the one gate answers. Every start path in the platform asks this and nothing else, so the
/// whole of "who may start something" is the truth table below.
/// </summary>
public class BillingGateTests
{
    [Fact]
    public async Task A_workspace_with_no_balance_cannot_start_anything()
    {
        await using var db = GateHarness.SystemContext();
        var ws = GateHarness.SeedWorkspace(db, balanceMinor: 0);
        await db.SaveChangesAsync();

        var check = await GateHarness.Gate(db).CanStartAsync(ws, default);

        check.Allowed.Should().BeFalse();
        check.Reason.Should().NotBeNullOrWhiteSpace("a refusal a customer cannot read is a support ticket");
        check.ShouldCarryAPersianReason();
    }

    [Fact]
    public async Task A_workspace_in_credit_can_start()
    {
        await using var db = GateHarness.SystemContext();
        var ws = GateHarness.SeedWorkspace(db, balanceMinor: 5_000);
        await db.SaveChangesAsync();

        (await GateHarness.Gate(db).CanStartAsync(ws, default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_workspace_that_has_spent_past_zero_cannot_start()
    {
        await using var db = GateHarness.SystemContext();
        var ws = GateHarness.SeedWorkspace(db, balanceMinor: -1);
        await db.SaveChangesAsync();

        (await GateHarness.Gate(db).CanStartAsync(ws, default)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task A_workspace_that_has_never_been_billed_has_no_wallet_and_no_balance()
    {
        await using var db = GateHarness.SystemContext();
        var ws = GateHarness.SeedWorkspace(db, balanceMinor: 0, withWallet: false);
        await db.SaveChangesAsync();

        // The same answer the tick gives it: BillingTick creates the missing wallet holding zero,
        // so "no wallet" and "a wallet holding nothing" must not be two different amounts of money.
        (await GateHarness.Gate(db).CanStartAsync(ws, default)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Nothing_is_refused_while_billing_is_switched_off()
    {
        await using var db = GateHarness.SystemContext();
        var ws = GateHarness.SeedWorkspace(db, balanceMinor: 0, suspended: true, reason: SuspensionReason.NoBalance);
        await db.SaveChangesAsync();

        // Off is the shipped default. An install that upgraded into billing unasked must not start
        // refusing to run a tenant's workloads over a balance nobody ever told them existed.
        (await GateHarness.Gate(db, enabled: false).CanStartAsync(ws, default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Archived_workspace_is_refused_even_when_billing_is_switched_off()
    {
        await using var db = GateHarness.SystemContext();
        var id = GateHarness.SeedWorkspace(db, balanceMinor: 5_000);
        await db.SaveChangesAsync();
        var workspace = await db.Workspaces.SingleAsync(w => w.Id == id);
        workspace.ArchivedAt = DateTimeOffset.UtcNow;
        workspace.IsSuspended = true;
        workspace.SuspendedReason = SuspensionReason.Archived;
        await db.SaveChangesAsync();

        (await GateHarness.Gate(db, enabled: false).CanStartAsync(id, default)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task A_manually_suspended_workspace_cannot_start_however_much_it_pays()
    {
        await using var db = GateHarness.SystemContext();
        var ws = GateHarness.SeedWorkspace(db, balanceMinor: 1_000_000,
            suspended: true, reason: SuspensionReason.Manual);
        await db.SaveChangesAsync();

        var check = await GateHarness.Gate(db).CanStartAsync(ws, default);

        check.Allowed.Should().BeFalse("paying a bill is not a way to undo an operator's decision");
        check.Reason.Should().NotBeNullOrWhiteSpace();
        check.Reason.Should().NotContain("Top it up",
            "sending somebody to the payment page for a suspension no payment lifts is the refusal " +
            "that generates the support ticket it was written to prevent");
        check.ShouldCarryAPersianReason();
        // The Fa side must draw the same distinction the English one does: paying does not undo an
        // operator's decision, so the Fa text must not point at a top-up either.
        check.ReasonFa.Should().NotContain("شارژ");
    }

    [Fact]
    public async Task A_workspace_suspended_before_the_reason_existed_cannot_start()
    {
        await using var db = GateHarness.SystemContext();
        var ws = GateHarness.SeedWorkspace(db, balanceMinor: 1_000_000,
            suspended: true, reason: SuspensionReason.None);
        await db.SaveChangesAsync();

        // None on a suspended workspace is every workspace suspended before the column existed. It
        // is not a NoBalance suspension, so money does not lift it — the same asymmetry
        // BillingSuspension.ResumeAsync draws, and for the same rows.
        var check = await GateHarness.Gate(db).CanStartAsync(ws, default);

        check.Allowed.Should().BeFalse();
        check.ShouldCarryAPersianReason();
    }

    [Fact]
    public async Task A_workspace_suspended_for_an_empty_balance_can_start_once_it_has_paid()
    {
        await using var db = GateHarness.SystemContext();
        var ws = GateHarness.SeedWorkspace(db, balanceMinor: 5_000,
            suspended: true, reason: SuspensionReason.NoBalance);
        await db.SaveChangesAsync();

        // This exact state is the window BillingSuspension.ResumeAsync works inside: the top-up has
        // landed, the flag is not cleared until every app it remembers is running again, and the
        // starts it makes go through IAppOperationsService — which asks this gate. Refusing here
        // would mean a customer pays, the resume asks the platform to start their apps, and the
        // platform refuses on the grounds that they have not paid.
        (await GateHarness.Gate(db).CanStartAsync(ws, default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_workspace_suspended_for_an_empty_balance_that_is_still_empty_cannot_start()
    {
        await using var db = GateHarness.SystemContext();
        var ws = GateHarness.SeedWorkspace(db, balanceMinor: 0,
            suspended: true, reason: SuspensionReason.NoBalance);
        await db.SaveChangesAsync();

        (await GateHarness.Gate(db).CanStartAsync(ws, default)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task The_providers_own_workspace_is_never_refused_for_money()
    {
        await using var db = GateHarness.SystemContext();
        var ws = GateHarness.SeedWorkspace(db, balanceMinor: -50_000, isDefault: true);
        await db.SaveChangesAsync();

        // The tick charges the default workspace like any other, and BillingSuspension refuses to
        // suspend it because the panel itself lives there. Without the same exemption here, the
        // platform's own balance running out would take down the only screen anybody could use to
        // put it right.
        (await GateHarness.Gate(db).CanStartAsync(ws, default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_workspace_that_does_not_exist_may_not_start_anything()
    {
        await using var db = GateHarness.SystemContext();

        var check = await GateHarness.Gate(db).CanStartAsync(Guid.NewGuid(), default);

        check.Allowed.Should().BeFalse();
        check.ShouldCarryAPersianReason();
    }

    [Fact]
    public async Task The_answer_does_not_depend_on_whose_session_asked()
    {
        await using var db = GateHarness.SystemContext();
        var broke = GateHarness.SeedWorkspace(db, balanceMinor: 0);
        var paid = GateHarness.SeedWorkspace(db, balanceMinor: 5_000);
        await db.SaveChangesAsync();

        // The callers that matter most have no session at all: the job worker running a queued
        // deployment, the cron tick, the webhook that queued it. Wallet carries a tenant filter, so
        // a gate that read the balance through the ambient scope would find no wallet on every one
        // of those paths — and answer "no balance" for a workspace in credit, or worse, wave one
        // through. Both answers below are read through Guid.Empty, which matches no tenant.
        await using var sessionless = GateHarness.SessionlessContext(db);

        (await GateHarness.Gate(sessionless).CanStartAsync(broke, default)).Allowed.Should().BeFalse();
        (await GateHarness.Gate(sessionless).CanStartAsync(paid, default)).Allowed.Should().BeTrue(
            "a queued deployment for a workspace in credit must not be refused because the worker has no session");
    }
}

/// <summary>
/// The gate installed, watched refusing.
///
/// <para>
/// The census below reads the source and can only prove that a file mentions the gate. These prove
/// that each of the four does something with the answer — and, in every case, that it does it before
/// the server engine is reached. A refusal that costs a round trip to an unreachable node is a
/// refusal that arrives as a timeout.
/// </para>
/// </summary>
public class BillingGateEnforcementTests
{
    /// <summary>
    /// A server engine factory that fails the test if anything resolves it.
    ///
    /// <para>
    /// It is the assertion, not scaffolding: "refused" and "refused before touching the node" are
    /// different claims, and only the second one holds when the node is down — which, for a
    /// workspace whose containers were stopped an hour ago, is a normal state of affairs.
    /// </para>
    /// </summary>
    private sealed class NoServerShouldBeAsked : IServerEngineFactory
    {
        public IDockerEngine Local => throw new InvalidOperationException(Message);

        public Task<IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct) =>
            throw new InvalidOperationException(Message);

        private const string Message =
            "The container engine was resolved for a workspace that may not start anything.";
    }

    private static (Guid Workspace, Guid App) SeedStoppedApp(GateContext db, long balanceMinor)
    {
        var workspaceId = GateHarness.SeedWorkspace(db, balanceMinor);
        var app = new Harbora.Domain.Apps.App
        {
            WorkspaceId = workspaceId,
            ServerId = Guid.NewGuid(),
            Name = "Blog",
            Slug = "blog",
            Status = Harbora.Domain.Common.AppStatus.Stopped
        };
        db.Apps.Add(app);
        db.SaveChanges();
        return (workspaceId, app.Id);
    }

    private static AppOperationsService Operations(HarboraDbContext db, HarboraDbContext? gateOver = null) =>
        new(db,
            new NoServerShouldBeAsked(),
            new RecordingProxyEngine(() => []),
            GateHarness.Gate(gateOver ?? db),
            new HostPortAllocator(db, TestIngress.Registry(), NullLogger<HostPortAllocator>.Instance),
            NullLogger<AppOperationsService>.Instance);

    /// <summary>
    /// The refusal, told apart from the node falling over.
    ///
    /// <para>
    /// Asserted as <see cref="QuotaRefusedException"/> specifically rather than the plain
    /// <see cref="InvalidOperationException"/> it used to be: a test that only asserted the base type
    /// passes just as happily when the gate has been deleted and the message is really
    /// <see cref="NoServerShouldBeAsked"/> saying a server was asked. Mutation testing found exactly
    /// that: removing the gate from <c>RestartAsync</c> killed no test at all. Requiring the specific
    /// type additionally pins that the refusal still carries <see cref="QuotaCheck.ReasonFa"/> through
    /// to the request-scoped caller — every use of this helper is one of the four request-scoped
    /// starts that throw it (<c>AppOperationsService.StartAsync</c>/<c>RestartAsync</c> and
    /// <c>ManagedServiceEngine.StartAsync</c>, sessioned or not).
    /// </para>
    /// </summary>
    private static async Task<string> RefusalFrom(Func<Task> act)
    {
        var thrown = await Assert.ThrowsAsync<QuotaRefusedException>(act);
        thrown.Message.Should().Contain("balance",
            "this must be the gate refusing, not the container engine being reached and failing");
        thrown.ReasonFa.Should().NotBeNullOrWhiteSpace(
            "a request-scoped refusal is exactly the case QuotaRefusedException exists to carry both " +
            "languages through — losing ReasonFa here is the same gap the Fa slot was added to close, " +
            "one layer further out");
        thrown.ReasonFa!.Should().MatchRegex("[\\u0600-\\u06FF]",
            "a Fa slot holding the English sentence again would pass a null-check and still not be a " +
            "translation a Persian speaker could read");
        return thrown.Message;
    }

    [Fact]
    public async Task Pressing_start_on_a_workspace_with_no_balance_never_reaches_the_container_engine()
    {
        await using var db = GateHarness.SystemContext();
        var (_, appId) = SeedStoppedApp(db, balanceMinor: 0);

        await RefusalFrom(() => Operations(db).StartAsync(appId, default));

        db.Apps.Single().Status.Should().Be(AppStatus.Stopped,
            "a start that writes Running without starting anything hands the hourly tick an hour to bill");
    }

    [Fact]
    public async Task Pressing_restart_on_a_workspace_with_no_balance_never_reaches_the_container_engine()
    {
        await using var db = GateHarness.SystemContext();
        var (_, appId) = SeedStoppedApp(db, balanceMinor: 0);

        await RefusalFrom(() => Operations(db).RestartAsync(appId, default));

        db.Apps.Single().Status.Should().Be(AppStatus.Stopped);
    }

    [Fact]
    public async Task A_start_asked_for_by_something_with_no_session_is_still_refused()
    {
        await using var db = GateHarness.SystemContext();
        var (_, appId) = SeedStoppedApp(db, balanceMinor: 0);

        // The resume after a top-up reaches StartAsync with no session at all, and so would any
        // recovery command or admin job added later. Under the tenant filter the lookup of which
        // workspace this app belongs to comes back empty — and a gate that cannot name the workspace
        // is a gate that waves the start through while every test on a system context stays green.
        await using var sessionless = GateHarness.SessionlessContext(db);

        await RefusalFrom(() => Operations(sessionless).StartAsync(appId, default));
    }

    [Fact]
    public async Task A_scheduled_job_that_cannot_be_paid_for_records_why_it_did_not_run()
    {
        await using var db = GateHarness.SystemContext();
        var (workspaceId, _) = SeedStoppedApp(db, balanceMinor: 0);

        var job = new Harbora.Domain.Apps.App
        {
            WorkspaceId = workspaceId,
            ServerId = Guid.NewGuid(),
            Name = "Nightly",
            Slug = "nightly",
            Kind = Harbora.Domain.Common.ServiceKind.Cron,
            CronExpression = "0 3 * * *",
            Command = "php artisan backup:run"
        };
        db.Apps.Add(job);
        await db.SaveChangesAsync();

        var runner = new CronJobRunner(
            db, new NoServerShouldBeAsked(), new PassthroughProtector(), GateHarness.Gate(db),
            Options.Create(new HarboraRuntimeOptions()),
            new FixedClock(new DateTimeOffset(2026, 8, 10, 3, 0, 0, TimeSpan.Zero)),
            NullLogger<CronJobRunner>.Instance);

        await runner.RunAsync(job, manual: false, default);

        // A schedule that quietly stops firing is the hardest kind of outage to notice, so the
        // refusal goes where somebody looks for it.
        var run = db.CronRuns.IgnoreQueryFilters().Single();
        run.Error.Should().Contain("balance");
        run.FinishedAt.Should().NotBeNull(
            "an unfinished row is read as still running, and the guard at the top of RunAsync would " +
            "then refuse this job for ever");
    }

    [Fact]
    public async Task Provisioning_a_database_after_the_balance_ran_out_fails_it_rather_than_starting_it()
    {
        await using var db = GateHarness.SystemContext();
        var workspaceId = GateHarness.SeedWorkspace(db, balanceMinor: 0);
        var service = new Harbora.Domain.Services.ManagedService
        {
            WorkspaceId = workspaceId,
            ServerId = Guid.NewGuid(),
            Name = "orders",
            Type = Harbora.Domain.Common.ManagedServiceType.PostgreSql,
            Version = "16",
            ContainerName = "harbora-svc-orders",
            VolumeName = "harbora-svc-orders-data",
            Status = Harbora.Domain.Common.ServiceStatus.Provisioning
        };
        db.ManagedServices.Add(service);
        await db.SaveChangesAsync();

        await Engine(db).ProvisionAsync(service.Id, default);

        // The queue is durable: this request may have been made an hour ago, when there was money.
        db.ManagedServices.IgnoreQueryFilters().Single().Status
            .Should().Be(Harbora.Domain.Common.ServiceStatus.Failed,
                "a database that will not appear must say so rather than read Provisioning for ever");
    }

    [Fact]
    public async Task Starting_a_database_by_hand_on_an_empty_balance_is_refused()
    {
        await using var db = GateHarness.SystemContext();
        var workspaceId = GateHarness.SeedWorkspace(db, balanceMinor: 0);
        var service = new Harbora.Domain.Services.ManagedService
        {
            WorkspaceId = workspaceId,
            ServerId = Guid.NewGuid(),
            Name = "orders",
            Type = Harbora.Domain.Common.ManagedServiceType.PostgreSql,
            Version = "16",
            ContainerName = "harbora-svc-orders",
            VolumeName = "harbora-svc-orders-data",
            Status = Harbora.Domain.Common.ServiceStatus.Stopped
        };
        db.ManagedServices.Add(service);
        await db.SaveChangesAsync();

        await RefusalFrom(() => Engine(db).StartAsync(service.Id, default));

        db.ManagedServices.IgnoreQueryFilters().Single().Status
            .Should().Be(Harbora.Domain.Common.ServiceStatus.Stopped);
    }

    private static Harbora.Infrastructure.Services.ManagedServiceEngine Engine(GateContext db) =>
        new(db, new NoServerShouldBeAsked(), new PassthroughProtector(), new NoopJobQueue(),
            GateHarness.Gate(db), Options.Create(new HarboraRuntimeOptions()),
            new FixedClock(DateTimeOffset.UnixEpoch),
            NullLogger<Harbora.Infrastructure.Services.ManagedServiceEngine>.Instance);

    [Fact]
    public async Task A_deployment_claimed_after_the_balance_ran_out_fails_before_anything_is_built()
    {
        using var harness = new PipelineHarness();
        harness.Db.Wallets.Add(new Wallet { WorkspaceId = harness.Workspace.Id, BalanceMinor = 0 });
        harness.Db.SaveChanges();
        harness.Gate = new BillingGate(harness.Db, Options.Create(new BillingOptions { Enabled = true }));

        var previous = harness.WithPreviousDeployment();
        // The state the suspension left this app in: stopped, but still carrying the deployment that
        // was active when it was running.
        harness.App.Status = AppStatus.Stopped;
        harness.Db.SaveChanges();

        var queued = harness.QueueDeployment();

        await harness.BuildPipeline().ExecuteAsync(queued.Id, default);

        var deployment = harness.Db.Deployments.IgnoreQueryFilters().Single(d => d.Id == queued.Id);
        deployment.Status.Should().Be(DeploymentStatus.Failed);
        deployment.ErrorMessage.Should().Contain("balance");

        harness.Docker.Calls.Should().BeEmpty("nothing may be pulled, built or started for a workspace that cannot pay");

        // The whole reason the refusal sits OUTSIDE the pipeline's try. The failure path inside it
        // writes `app.Status = ActiveDeploymentId is null ? Failed : Running`, which is right for a
        // deploy that broke halfway and a lie for one that never started: this app has an active
        // deployment and no container, so a refusal thrown into that catch would record it Running
        // and hand the hourly tick an hour to bill for nothing.
        var app = harness.Db.Apps.IgnoreQueryFilters().Single();
        app.Status.Should().Be(AppStatus.Stopped,
            "a deployment that was refused before it began must not report the app as running");
        app.ActiveDeploymentId.Should().Be(previous.Id, "a refused deployment never became the active one");
    }

    [Fact]
    public async Task A_deployment_for_a_workspace_in_credit_is_not_refused()
    {
        using var harness = new PipelineHarness();
        harness.Db.Wallets.Add(new Wallet { WorkspaceId = harness.Workspace.Id, BalanceMinor = 500_000 });
        harness.Db.SaveChanges();
        harness.Gate = new BillingGate(harness.Db, Options.Create(new BillingOptions { Enabled = true }));

        var queued = harness.QueueDeployment(number: 1);

        await harness.BuildPipeline().ExecuteAsync(queued.Id, default);

        // Paired with the test above so neither can pass by the gate simply refusing everything —
        // the failure this pair rules out is a gate that is really a switch nobody can turn off.
        harness.Db.Deployments.IgnoreQueryFilters().Single(d => d.Id == queued.Id)
            .Status.Should().Be(DeploymentStatus.Succeeded);
    }
}

/// <summary>
/// The census: every container this control plane starts is either behind the gate or written down
/// as not being a workload, with a reason.
///
/// <para>
/// This is deliberately the inverse of a list of known starters. A list of starters is checked by a
/// reviewer noticing that a new one is missing from it, and the reviewer is exactly what fails: this
/// codebase has already shipped a rule with four minters and one call site. Here the source is asked
/// what starts containers, and every answer must have been thought about. A new file that runs a
/// container fails this test on the day it is written, naming itself.
/// </para>
/// </summary>
public class StartPathCensusTests
{
    /// <summary>
    /// The calls that leave a container running, or run one to completion. Anything reaching the
    /// container runtime for a tenant goes through one of these three.
    /// </summary>
    private static readonly string[] StartsAContainer =
        ["RunContainerAsync(", "RestartContainerAsync(", "RunOneOffAsync("];

    /// <summary>
    /// The whole of the gate's installation. Each of these is the LAST place before a container
    /// runs — not the button that asked for it — so the count is four however many controllers,
    /// webhooks, CLI routes and background services grow on top of them.
    /// </summary>
    private static readonly string[] MustAskTheGate =
    [
        "src/Harbora.Infrastructure/Deployments/DeploymentPipeline.cs",
        "src/Harbora.Infrastructure/Deployments/AppOperationsService.cs",
        "src/Harbora.Infrastructure/Deployments/CronJobRunner.cs",
        "src/Harbora.Infrastructure/Services/ManagedServiceEngine.cs"
    ];

    /// <summary>
    /// Containers that are not a tenant's workload, and why each one is not.
    ///
    /// <para>
    /// The shared test every entry here passes: <c>BillingTick</c> does not charge for it. It bills
    /// running apps by the hour and a managed database's disk, and none of these is either. A
    /// container the platform never charges for cannot be refused for an empty balance without
    /// refusing somebody access to data they have already paid to store.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> NotATenantWorkload = new()
    {
        ["src/Harbora.Infrastructure/Backups/BackupEngine.cs"] =
            "backup and restore helpers, plus the restart of the database container it stopped itself " +
            "to restore into — a customer must be able to get their data out of a suspended workspace",
        ["src/Harbora.Infrastructure/Backups/BackupStorage.cs"] =
            "moves an existing backup artifact between volumes and destinations",
        ["src/Harbora.Infrastructure/Backups/UpgradeSafetyService.cs"] =
            "the pre-upgrade dump of the platform's own database",
        ["src/Harbora.Infrastructure/Tenancy/StorageMeasurer.cs"] =
            "walks a volume to add up what is in it — it is the hourly charge's own input, so gating " +
            "it on the balance would stop the platform being able to work out the bill",
        ["src/Harbora.Infrastructure/Storage/VolumeFileService.cs"] =
            "reads and writes files inside a volume the customer already owns",
        ["src/Harbora.Infrastructure/Storage/ObjectStorageAdmin.cs"] =
            "administers buckets through the storage server's own client",
        ["src/Harbora.Infrastructure/Storage/BucketObjectService.cs"] =
            "lists and moves objects the customer already stores",
        ["src/Harbora.Infrastructure/Services/DatabaseGrantExecutor.cs"] =
            "runs one SQL statement inside a database that is already running",
        ["src/Harbora.Infrastructure/Services/AdminerService.cs"] =
            "a one-hour web view onto an existing database, removed by its own sweeper; refusing it " +
            "would deny a suspended customer a look at their own rows at the moment they most need one",
        ["src/Harbora.Infrastructure/Services/DockerTcpGateway.cs"] =
            "a per-grant proxy onto an existing database, so the database itself never publishes a " +
            "port; it starts no workload of its own",
        ["src/Modules/Backup/Harbora.Modules.Backup.Infrastructure/ContainerDatabaseBackupProvider.cs"] =
            "runs the database's own dump client against a database that is already running",
        ["src/Modules/Backup/Harbora.Modules.Backup.Infrastructure/BackupTargetResolver.cs"] =
            "inspects what a backup target actually contains before one is taken",
        ["src/Modules/Backup/Harbora.Modules.Backup.Infrastructure/ApplicationTargetStager.cs"] =
            "stages an application's files for a backup that is already under way"
    };

    /// <summary>
    /// The runtime itself, and the far side of the wire. These <em>are</em> the primitive — a gate
    /// inside them would refuse the platform's own housekeeping — and the node agent takes orders
    /// rather than deciding whether to obey them.
    /// </summary>
    private static readonly string[] IsTheRuntime =
    [
        "src/Harbora.Application/Abstractions/IDockerEngine.cs",
        "src/Harbora.Infrastructure/Docker/DockerEngine.cs",
        "src/Harbora.Infrastructure/Docker/RemoteDockerEngine.cs",
        "src/Harbora.Infrastructure/Nodes/NodeWorkloadEngine.cs"
    ];

    [Fact]
    public void Every_file_that_starts_a_container_either_asks_the_gate_or_says_why_it_need_not()
    {
        var unaccounted = StartersOnDisk()
            .Where(path => !MustAskTheGate.Contains(path) && !NotATenantWorkload.ContainsKey(path))
            .ToList();

        unaccounted.Should().BeEmpty(
            "each of these runs a container and nothing has said whether a workspace with no balance " +
            "may. Add the gate to it, or add it to NotATenantWorkload with the reason it is not a " +
            "tenant's workload. Found: " + string.Join(", ", unaccounted));
    }

    [Fact]
    public void Every_path_that_can_start_a_workload_asks_the_gate()
    {
        foreach (var path in MustAskTheGate)
        {
            var full = Path.Combine(RepoRoot, path);

            File.Exists(full).Should().BeTrue(
                $"{path} is on the starter list but not on disk — if it moved, update this list; " +
                "if it was deleted, remove it");

            File.ReadAllText(full).Should().Contain("CanStartAsync",
                $"{path} can start a workload, so it must ask IBillingGate first. " +
                "Without it a workspace with no balance gets free hosting through this path.");
        }
    }

    [Fact]
    public void No_file_is_both_gated_and_excused_from_the_gate()
    {
        // The contradiction is what makes it worth a test of its own. A file in both lists still
        // passes the two tests above — one says it is accounted for, the other says it asks the gate
        // — so the next person can quietly drop it from the gated list and the exemption will carry
        // it, with nothing going red. Found by mutation: adding a gated file to NotATenantWorkload
        // killed nothing.
        var both = MustAskTheGate.Where(NotATenantWorkload.ContainsKey).ToList();

        both.Should().BeEmpty(
            "a file cannot both have to ask the gate and be excused from it. Found: " +
            string.Join(", ", both));
    }

    [Fact]
    public void Nothing_is_excused_that_no_longer_starts_a_container()
    {
        var starters = StartersOnDisk();

        var stale = NotATenantWorkload.Keys.Concat(MustAskTheGate)
            .Where(path => !starters.Contains(path))
            .ToList();

        stale.Should().BeEmpty(
            "these are written down as container starters and no longer are one. A list that keeps " +
            "entries after the code moved is a list nobody can read the current answer off. Found: " +
            string.Join(", ", stale));
    }

    /// <summary>Every control-plane file that reaches the container runtime, repo-relative.</summary>
    private static List<string> StartersOnDisk() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(RepoRoot, f).Replace('\\', '/'))
            .Where(p => !p.Contains("/bin/") && !p.Contains("/obj/") && !p.Contains("/Migrations/"))
            // The node agent and the thin agent host are the far side of the wire: they carry out
            // what the control plane has already decided, and the decision is this side's to make.
            .Where(p => !p.StartsWith("src/Harbora.NodeAgent") && !p.StartsWith("src/Harbora.Agent"))
            .Where(p => !IsTheRuntime.Contains(p))
            .Where(p => StartsAContainer.Any(call => File.ReadAllText(Path.Combine(RepoRoot, p)).Contains(call)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>The repository root, from wherever the runner happens to start.</summary>
    private static string RepoRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harbora.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Harbora.slnx from the test output directory.");
    }
}
