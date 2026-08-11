using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Mail;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// Harbora's own database, pointed at one in-memory store, told which tenant it belongs to, and able
/// to refuse one save.
///
/// <para>
/// The scope is the reason this exists rather than <see cref="BrittleContext"/>: the tick has to be
/// given a context that is scoped to a tenant, so that a test can prove it charges everybody anyway.
/// <c>BrittleContext</c> is sealed around a primary constructor that can only reach the
/// system-scoped base constructor, and widening a fake five other test classes depend on to make one
/// point here would be the wrong trade. The store name is kept for the same reason: a test needs to
/// open a SECOND context over the same rows, so the tick and the assertions can disagree about which
/// tenant they can see.
/// </para>
///
/// <para>
/// Every save overload is intercepted, not only the async-with-token one the tick reaches for today.
/// That is <c>BrittleContext</c>'s hard-won rule and it applies unchanged: overriding one overload is
/// correct until somebody writes <c>SaveChanges()</c>, at which point the test keeps passing while
/// covering nothing.
/// </para>
/// </summary>
internal sealed class BillingContext : HarboraDbContext
{
    public BillingContext(string store, IWorkspaceScope scope)
        : base(new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(store).Options, scope)
        => Store = store;

    /// <summary>The in-memory store these rows live in, so another context can be opened over it.</summary>
    public string Store { get; }

    /// <summary>Thrown by the next save, once. Null means saves behave normally.</summary>
    public Exception? FailTheNextSaveWith { get; set; }

    /// <summary>
    /// Runs immediately before that refusal, so the fake can BE the pass that won the race rather
    /// than merely imitate its error. A test that only throws 23505 without the winner's rows
    /// existing is asserting on a database state that could never have produced the exception.
    /// </summary>
    public Action? WhenItRefuses { get; set; }

    /// <summary>
    /// When true, the failing save takes the context with it — reads included. That is the real
    /// difference between the two halves of a failed write: a constraint violation leaves the
    /// connection healthy and code that recovers by reading again recovers fine, while a dropped
    /// connection or a failover does not, and the recovery read throws a second time.
    /// </summary>
    public bool LoseTheConnectionToo { get; set; }

    /// <summary>How many times anything asked this context to write. A retry has to be countable.</summary>
    public int Saves { get; private set; }

    public override int SaveChanges() { Refuse(); return base.SaveChanges(); }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Refuse();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Refuse();
        return await base.SaveChangesAsync(cancellationToken);
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Refuse();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void Refuse()
    {
        Saves++;
        if (FailTheNextSaveWith is not { } failure) return;

        FailTheNextSaveWith = null;

        var winner = WhenItRefuses;
        WhenItRefuses = null;
        winner?.Invoke();

        if (LoseTheConnectionToo) Dispose();

        throw failure;
    }
}

/// <summary>
/// One place to build a priced install, and one place that decides what the tick can see.
/// </summary>
internal static class Harness
{
    /// <summary>
    /// The instant every test runs at. Late enough in the day that <see cref="BillingTickTests.Hour"/>
    /// and the hour after it have both ended, because a test whose second hour is silently refused as
    /// "not over yet" would pass while proving half of what it says.
    /// </summary>
    public static readonly DateTimeOffset Now = new(2026, 8, 9, 20, 0, 0, TimeSpan.Zero);

    public static readonly FixedClock Clock = new(Now);

    /// <summary>A context that sees every tenant — for seeding the fixture and for asserting on it.</summary>
    public static BillingContext SystemContext(string? store = null) =>
        new(store ?? "billing-" + Guid.NewGuid(), SystemWorkspaceScope.Instance);

    /// <summary>
    /// More balance than any fixture here spends, for a test that charges one workspace twice.
    ///
    /// <para>
    /// The pass stops a workspace it has left at or below nothing, which is the feature — and a
    /// fixture seeded with a wallet at zero is a workspace whose first charged hour takes it there.
    /// A test about the ledger's own arithmetic that then charged a second hour would be charging it
    /// against apps this pass had just stopped, and would be quietly about two things. Paying the
    /// fixture up keeps it about one.
    /// </para>
    /// </summary>
    public const long PaidUp = 1_000_000;

    /// <summary>Puts a balance on a workspace, standing in for hours nobody wants to run.</summary>
    public static void SetBalance(BillingContext db, Guid workspaceId, long balanceMinor)
    {
        var wallet = db.Wallets.Single(w => w.WorkspaceId == workspaceId);
        wallet.BalanceMinor = balanceMinor;
        db.SaveChanges();
    }

    /// <summary>
    /// The context the tick is given, over the same rows, scoped to <see cref="Guid.Empty"/>.
    ///
    /// <para>
    /// That is the worst case on purpose, and it is not hypothetical: <c>HttpWorkspaceScope</c>
    /// resolves an unauthenticated or not-yet-onboarded request to <see cref="Guid.Empty"/>, which
    /// matches no tenant's data. Work that reads through it finds an empty database, charges nobody
    /// and reports success. Handing the tick that scope in every test makes "charges everybody
    /// regardless" a standing property rather than something proved once by hand and then lost.
    /// </para>
    /// </summary>
    public static BillingContext TickContext(BillingContext db) =>
        new(db.Store, new FixedWorkspaceScope(Guid.Empty));

    public static BillingTick Tick(
        BillingContext db,
        int maxBackfillHours = 72,
        bool enabled = true,
        BillingContext? through = null,
        INotificationService? notifications = null,
        FakeAppOperations? operations = null,
        FakeDatabaseOperations? databases = null,
        Func<BillingSuspension>? suspension = null,
        string? currency = null)
    {
        // Handing the database to the tick means giving up your cached copy of it. The tick writes
        // through a context of its own, so anything this one is still tracking is about to be stale
        // — and EF resolves a query to the instance it is already tracking, so an assertion made
        // afterwards would read the balance as it was BEFORE the charge and call it the answer.
        db.ChangeTracker.Clear();

        // Registered as a singleton instance so the scope the tick opens cannot dispose the context
        // the test is still holding. The tick resolves it exactly as it does in production — through
        // a scope from IServiceScopeFactory.
        var services = new ServiceCollection();
        services.AddSingleton<HarboraDbContext>(through ?? TickContext(db));

        // Registered for every tick, not only the tests that assert on it. The tick resolves this
        // out of the scope it opens, so a harness that supplied it only on request would make every
        // other test in this file fail on a missing service the moment the warning was wired in —
        // and, worse, would invite somebody to "fix" that by making the resolution optional, which
        // is the shape where a warning nobody receives reports itself as sent.
        services.AddSingleton<INotificationService>(notifications ?? new RecordingNotificationService());

        // Registered for every tick, and registered per scope, both for reasons the notification
        // line above gives. Per scope because that is what production does: the sweep resolves a
        // suspension of its own for each workspace it stops, so a workspace whose stop fell over
        // half-written cannot leave anything behind in a context the next workspace's save would
        // commit under its name. A singleton here would share one change tracker across all of them
        // and quietly excuse exactly that.
        var stopApps = operations ?? Operations(db);
        var stopDatabases = databases ?? Databases(db);

        services.AddScoped(_ => suspension?.Invoke() ?? new BillingSuspension(
            TickContext(db),
            stopApps,
            stopDatabases,
            Options.Create(new BillingOptions { Enabled = enabled }),
            NullLogger<BillingSuspension>.Instance));

        var provider = services.BuildServiceProvider();

        return new BillingTick(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BillingOptions
            {
                Enabled = enabled,
                MaxBackfillHours = maxBackfillHours,
                Currency = currency ?? BillingOptions.DefaultCurrency
            }),
            Clock,
            NullLogger<BillingTick>.Instance);
    }

    /// <summary>
    /// A workspace on its own plan, with its own instance size and one app running on it.
    ///
    /// <para>
    /// The plan's floor is a deliberate <c>0</c> rather than left unset: zero is an answer ("this
    /// plan has no minimum") and null is the absence of one, and a fixture that left it null would
    /// make every test report an unpriced plan it was not asking about.
    /// </para>
    /// </summary>
    public static Guid SeedWorkspaceWithOneRunningApp(
        HarboraDbContext db,
        string name,
        long? ratePerHour,
        long? stoppedRatePerHour = null,
        long? planBaseRatePerHourMinor = 0,
        AppStatus status = AppStatus.Running)
    {
        var plan = new Plan { Name = name + "-plan", BaseRatePerHourMinor = planBaseRatePerHourMinor };
        db.Plans.Add(plan);

        var workspace = new Workspace { Name = name, Slug = name, PlanId = plan.Id };
        db.Workspaces.Add(workspace);

        db.InstanceSizes.Add(new InstanceSize
        {
            Key = name + "-size",
            Name = name + " size",
            RunningRatePerHourMinor = ratePerHour,
            StoppedRatePerHourMinor = stoppedRatePerHour,
        });

        db.Apps.Add(new App
        {
            WorkspaceId = workspace.Id,
            Name = name + "-api",
            Slug = name + "-api",
            Status = status,
            InstanceSizeKey = name + "-size",
        });

        db.Wallets.Add(new Wallet { WorkspaceId = workspace.Id });

        return workspace.Id;
    }

    /// <summary>
    /// A second running app in the same workspace, on a size that exists but that nobody has priced.
    ///
    /// <para>
    /// This is the shape of a <i>partly</i> known hour, and it is the only shape that can tell the
    /// two halves of the floor rule apart. With one unpriced app the resource list is empty either
    /// way, so "the floor was withheld" and "there was nothing to charge" produce the same ledger;
    /// with one priced app beside it, a rule that dropped the priced line too has somewhere to show.
    /// </para>
    /// </summary>
    public static void AddRunningAppOnAnUnpricedSize(HarboraDbContext db, Guid workspaceId)
    {
        db.InstanceSizes.Add(new InstanceSize { Key = "later", Name = "Later" });

        db.Apps.Add(new App
        {
            WorkspaceId = workspaceId,
            Name = "tenant-worker",
            Slug = "tenant-worker",
            Status = AppStatus.Running,
            InstanceSizeKey = "later",
        });
    }

    /// <summary>
    /// A workspace holding one running app and one the customer stopped themselves.
    ///
    /// <para>
    /// Two apps in two different states is the whole fixture for suspension: the difference between
    /// "bring back what the outage took" and "start everything you can find" is invisible with one.
    /// </para>
    /// </summary>
    public static Guid SeedWorkspaceWithTwoApps(HarboraDbContext db, string running, string stopped)
    {
        var plan = new Plan { Name = "two-apps-plan", BaseRatePerHourMinor = 0 };
        db.Plans.Add(plan);

        var workspace = new Workspace { Name = "two-apps", Slug = "two-apps", PlanId = plan.Id };
        db.Workspaces.Add(workspace);

        db.Apps.Add(new App
        {
            WorkspaceId = workspace.Id,
            Name = running,
            Slug = running,
            Status = AppStatus.Running,
        });

        db.Apps.Add(new App
        {
            WorkspaceId = workspace.Id,
            Name = stopped,
            Slug = stopped,
            Status = AppStatus.Stopped,
        });

        db.Wallets.Add(new Wallet { WorkspaceId = workspace.Id });

        return workspace.Id;
    }

    /// <summary>A workspace already suspended, and saying why.</summary>
    public static Guid SeedSuspendedWorkspace(HarboraDbContext db, SuspensionReason reason)
    {
        var workspace = new Workspace
        {
            Name = "suspended",
            Slug = "suspended",
            IsSuspended = true,
            SuspendedReason = reason,
        };
        db.Workspaces.Add(workspace);
        db.Wallets.Add(new Wallet { WorkspaceId = workspace.Id });

        return workspace.Id;
    }

    /// <summary>
    /// A running managed database in an existing workspace, with the disk figure the tick needs.
    ///
    /// <para>
    /// <c>VolumeName</c> is set because every path in the product that creates one sets it — the
    /// database form, a template stack, an environment clone — so a fixture that left it blank would
    /// be a state the platform cannot produce.
    /// </para>
    /// </summary>
    public static Guid AddDatabase(
        HarboraDbContext db,
        Guid workspaceId,
        string name,
        ServiceStatus status = ServiceStatus.Running,
        long? storageBytes = null)
    {
        var service = new ManagedService
        {
            WorkspaceId = workspaceId,
            Name = name,
            Status = status,
            VolumeName = $"harbora-svc-{name}-data",
            StorageBytes = storageBytes,
        };

        db.ManagedServices.Add(service);
        return service.Id;
    }

    /// <summary>
    /// A stand-in for the platform's stop/start route, over the same hostile scope the suspension
    /// itself is given. Handed in when a test needs to make the route fail, or lie.
    /// </summary>
    public static FakeAppOperations Operations(BillingContext db) => new(TickContext(db));

    /// <inheritdoc cref="Operations"/>
    public static FakeDatabaseOperations Databases(BillingContext db) => new(TickContext(db));

    /// <summary>
    /// The suspension, wired to a context scoped to <see cref="Guid.Empty"/>.
    ///
    /// <para>
    /// Same reasoning as <see cref="TickContext"/>, and it matters more here than it does for the
    /// tick. Suspension is sessionless background work; under a request scope the app table reads as
    /// empty, so a suspension would stop nothing, write down nothing about what had been running, and
    /// report a clean pass — and the customer would discover months later that a top-up brings back
    /// none of their services. Giving every test the worst scope makes "reaches the apps regardless"
    /// a standing property rather than one test somebody remembers to write.
    /// </para>
    /// </summary>
    public static BillingSuspension Suspension(
        BillingContext db,
        FakeAppOperations? operations = null,
        bool enabled = true,
        FakeDatabaseOperations? databases = null)
    {
        // The suspension writes through a context of its own, so anything this one still tracks is
        // about to be stale — and EF answers a query from the instance it is already tracking, which
        // would let an assertion read the app's status as it was BEFORE the stop.
        db.ChangeTracker.Clear();

        return new BillingSuspension(
            TickContext(db),
            operations ?? Operations(db),
            databases ?? Databases(db),
            Options.Create(new BillingOptions { Enabled = enabled }),
            NullLogger<BillingSuspension>.Instance);
    }
}

/// <summary>
/// The hour that moves money.
///
/// <para>
/// Every clock here is fixed, and every assertion is made through a context that can see every
/// tenant while the tick's own context cannot see any. Those two facts carry most of what this file
/// is for.
/// </para>
/// </summary>
public class BillingTickTests
{
    internal static readonly DateTimeOffset Hour = new(2026, 8, 9, 14, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The refusal PostgreSQL would raise, wrapped the way EF delivers it. The SQLSTATE is the whole
    /// subject of two tests below, so it is built rather than mocked.
    /// </summary>
    private static DbUpdateException Refusal(string sqlState, string message) =>
        new(message, new PostgresException(message, "ERROR", "ERROR", sqlState));

    // --- the pass itself -------------------------------------------------------------------

    [Fact]
    public async Task Every_workspace_is_charged_not_just_the_one_in_scope()
    {
        // The guard on the trap this platform has already been bitten by: the EF tenancy filters
        // are session-scoped, so work that runs without a session sees an EMPTY database, charges
        // nobody, and reports success. Two workspaces in, two workspaces charged, or this is that.
        await using var db = Harness.SystemContext();
        var a = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant-a", ratePerHour: 500);
        var b = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant-b", ratePerHour: 700);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.WorkspacesCharged.Should().Be(2);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == a)).BalanceMinor.Should().Be(-500);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == b)).BalanceMinor.Should().Be(-700);
    }

    [Fact]
    public async Task A_retried_tick_charges_once()
    {
        // The durable queue retries. Without the unique index behind this, a retry is a second bill.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);
        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-500);
        (await db.BillingLedger.CountAsync(l => l.WorkspaceId == ws)).Should().Be(1);
    }

    [Fact]
    public async Task Ready_mail_domains_and_mailboxes_are_charged_at_their_price_snapshots()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "mail-tenant", ratePerHour: 0);
        await db.SaveChangesAsync();
        Harness.SetBalance(db, ws, Harness.PaidUp);
        var server = new MailServer
        {
            ServerId = Guid.CreateVersion7(), PublicHostname = "mail.example.com",
            ApiBaseUrl = "https://mail.example.com", EncryptedAdminUser = "x",
            EncryptedAdminPassword = "x", Status = MailServerStatus.Ready
        };
        var domain = new MailDomain
        {
            WorkspaceId = ws, MailServerId = server.Id, Domain = "example.com",
            Status = MailResourceStatus.Ready, RatePerHourMinor = 80
        };
        var mailbox = new MailMailbox
        {
            WorkspaceId = ws, MailDomainId = domain.Id, LocalPart = "hello",
            Status = MailResourceStatus.Ready, RatePerHourMinor = 20
        };
        db.AddRange(server, domain, mailbox);
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.BillingLedger.Where(x => x.WorkspaceId == ws &&
            x.ResourceType == BilledResourceType.MailDomain).SingleAsync()).AmountMinor.Should().Be(-80);
        (await db.BillingLedger.Where(x => x.WorkspaceId == ws &&
            x.ResourceType == BilledResourceType.Mailbox).SingleAsync()).AmountMinor.Should().Be(-20);
        (await db.Wallets.SingleAsync(x => x.WorkspaceId == ws)).BalanceMinor
            .Should().Be(Harness.PaidUp - 100);
    }

    [Fact]
    public async Task The_wallet_equals_the_sum_of_its_ledger()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        // Paid up, so the second hour is charged for an app that is still running. Left at zero the
        // first pass would stop it, and this would be a test about suspension wearing a ledger's
        // clothes.
        Harness.SetBalance(db, ws, Harness.PaidUp);

        await Harness.Tick(db).ChargeHourAsync(Hour, default);
        await Harness.Tick(db).ChargeHourAsync(Hour.AddHours(1), default);

        var wallet = await db.Wallets.SingleAsync(w => w.WorkspaceId == ws);
        var ledger = await db.BillingLedger.Where(l => l.WorkspaceId == ws).SumAsync(l => l.AmountMinor);
        wallet.BalanceMinor.Should().Be(Harness.PaidUp + ledger);

        // Named, because "they agree" is also true of two zeros. Two hours at 500 is 1000.
        ledger.Should().Be(-1000);
    }

    [Fact]
    public async Task An_hour_that_has_not_ended_is_not_charged()
    {
        // Charging forward means a customer pays for an hour they might spend stopped.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        var future = Harness.Clock.UtcNow.AddHours(2);
        var result = await Harness.Tick(db).ChargeHourAsync(future, default);

        result.LinesWritten.Should().Be(0);
    }

    [Fact]
    public async Task The_hour_in_progress_is_not_charged_either()
    {
        // The boundary the test above steps well clear of. The hour containing "now" has not ended,
        // and an off-by-one here bills every customer for an hour they are still living through.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Harness.Clock.UtcNow, default);

        result.LinesWritten.Should().Be(0);
    }

    [Fact]
    public async Task An_hour_is_billed_at_the_top_of_the_hour_whatever_minute_it_is_asked_about()
    {
        // Two callers naming the same hour differently — a timer at :00 and a catch-up at :37 —
        // must land on one row, or the unique index has nothing to collide on and the retry that
        // index exists to make harmless becomes a second bill.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);
        await Harness.Tick(db).ChargeHourAsync(Hour.AddMinutes(37), default);

        (await db.BillingLedger.CountAsync(l => l.WorkspaceId == ws)).Should().Be(1);
        (await db.BillingLedger.Where(l => l.WorkspaceId == ws).Select(l => l.BillingHour).SingleAsync())
            .Should().Be(Hour);
    }

    [Fact]
    public async Task Billing_that_is_switched_off_charges_nobody()
    {
        // Off is the shipped default. An install that upgraded into billing unasked would start
        // charging tenants who were never told there was a price, so the switch is checked by the
        // method that moves the money and not only by whatever schedules it.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db, enabled: false).ChargeHourAsync(Hour, default);

        result.LinesWritten.Should().Be(0);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(0);
        (await db.BillingLedger.CountAsync()).Should().Be(0);
    }

    // --- what gets a line, and what does not -----------------------------------------------

    [Fact]
    public async Task A_stopped_app_is_charged_the_reserved_rate_rather_than_nothing()
    {
        // The customer stopped it but did not delete it, so the slot, the image and the disk are
        // still theirs. Charging nothing would let a workspace park a hundred gigabytes for free.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: 500, stoppedRatePerHour: 100, status: AppStatus.Stopped);
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        var line = await db.BillingLedger.SingleAsync(l => l.WorkspaceId == ws);
        line.AmountMinor.Should().Be(-100);
        line.RunState.Should().Be(BilledRunState.Stopped);
    }

    [Theory]
    [InlineData(AppStatus.Created)]
    [InlineData(AppStatus.Deploying)]
    public async Task An_app_that_has_never_run_is_not_charged(AppStatus status)
    {
        // Nothing is reserved yet: no container, no image on disk, no port. Billing the moment a
        // row exists charges for a form somebody half filled in.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500, status: status);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.LinesWritten.Should().Be(0);
        (await db.BillingLedger.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_status_this_code_has_never_heard_of_is_reported_rather_than_billed_at_nothing()
    {
        // AppStatus is append-only, so one day it grows an arm. A default case that quietly returns
        // "not chargeable" would host every workload in the new state for free, for ever, with every
        // tick reporting success — which is the exact failure this whole branch exists to remove.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: 500, status: (AppStatus)99);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.LinesWritten.Should().Be(0);
        result.Failures.Should().ContainSingle(f => f.Contains("99"));
    }

    [Fact]
    public async Task A_managed_database_is_charged_for_its_hour_like_an_app_is()
    {
        // Databases were left out of the memory quota once and a tenant sat inside their plan while
        // the host ran out of RAM. Leaving them out of the bill is the same omission, priced.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        db.ManagedServices.Add(new ManagedService
        {
            WorkspaceId = ws,
            Name = "tenant-db",
            Status = ServiceStatus.Running,
            InstanceSizeKey = "tenant-size",
        });
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        var line = await db.BillingLedger.SingleAsync(l => l.ResourceType == BilledResourceType.Service);
        line.AmountMinor.Should().Be(-500);
        line.ResourceName.Should().Be("tenant-db");
    }

    [Fact]
    public async Task A_measured_volume_is_charged_by_the_gibibyte_it_holds()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        (await db.Plans.SingleAsync()).DiskGbHourMinor = 20;
        db.Volumes.Add(new Volume
        {
            AppId = (await db.Apps.SingleAsync()).Id,
            Name = "uploads",
            StorageBytes = 3L * 1024 * 1024 * 1024 + 1,   // one byte into the fourth gibibyte
        });
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        var line = await db.BillingLedger.SingleAsync(l => l.ResourceType == BilledResourceType.Volume);
        line.AmountMinor.Should().Be(-80);
        line.RunState.Should().Be(BilledRunState.NotApplicable);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-580);
    }

    [Fact]
    public async Task A_volume_nobody_has_measured_is_not_billed_as_empty()
    {
        // "Unmeasured is not zero" is a rule this platform already prints on every metric it shows.
        // A volume with no reading is not a volume holding nothing, and charging it for nothing is
        // how a hundred gibibytes are hosted free until somebody happens to measure them.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        (await db.Plans.SingleAsync()).DiskGbHourMinor = 20;
        db.Volumes.Add(new Volume { AppId = (await db.Apps.SingleAsync()).Id, Name = "uploads" });
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.BillingLedger.AnyAsync(l => l.ResourceType == BilledResourceType.Volume)).Should().BeFalse();
        result.Failures.Should().ContainSingle(f => f.Contains("uploads"));
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-500);
    }

    // --- the disk a managed database holds ----------------------------------------------------
    //
    // A managed service carries its own VolumeName and StorageBytes and has no row in the Volumes
    // table at all, which is keyed by AppId alone. The disk loop above therefore cannot reach a
    // database's storage by any route, and a workspace paid for its database's size and then held as
    // much data on it as it liked, for nothing.

    /// <summary>A running database with a measured disk, in the workspace the harness seeded.</summary>
    private static ManagedService Database(
        Guid workspaceId,
        string name = "tenant-db",
        long? storageBytes = null,
        ServiceStatus status = ServiceStatus.Running,
        DateTimeOffset? measuredAt = null) =>
        new()
        {
            WorkspaceId = workspaceId,
            Name = name,
            Status = status,
            InstanceSizeKey = "tenant-size",
            // Always set by every path that creates one — the database form, a template stack, an
            // environment clone — so a fixture that left it blank would be a state the product
            // cannot produce.
            VolumeName = $"harbora-svc-{name}-data",
            StorageBytes = storageBytes,
            StorageMeasuredAt = measuredAt,
        };

    [Fact]
    public async Task A_managed_databases_disk_is_charged_by_the_gibibyte_it_holds()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        (await db.Plans.SingleAsync()).DiskGbHourMinor = 20;
        db.ManagedServices.Add(Database(ws, storageBytes: 3L * 1024 * 1024 * 1024 + 1));
        await db.SaveChangesAsync();

        var service = await db.ManagedServices.AsNoTracking().SingleAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        var line = await db.BillingLedger.SingleAsync(l => l.ResourceType == BilledResourceType.ServiceVolume);

        // One byte into the fourth gibibyte, at 20 a gibibyte-hour.
        line.AmountMinor.Should().Be(-80);
        line.RunState.Should().Be(BilledRunState.NotApplicable);

        // Asserted rather than assumed. Task 3 found that a charge line whose identity nothing checks
        // lets a hard-coded id pass every test, and these are the two columns the unique index that
        // makes a retry harmless is keyed on.
        line.ResourceId.Should().Be(service.Id);
        line.ResourceName.Should().Be("tenant-db");

        // 500 for the app, 500 for the database's size, 80 for the disk under it.
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-1080);
    }

    [Fact]
    public async Task A_databases_disk_and_an_app_volume_are_two_lines_a_customer_can_tell_apart()
    {
        // The whole of the ledger-key decision, in one assertion. The two disks must not share a
        // (ResourceType, ResourceId) — that pair is the unique index's key for the hour — and a
        // reader of the bill has to be able to say which of them is the database's without parsing
        // a name the customer chose.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        (await db.Plans.SingleAsync()).DiskGbHourMinor = 20;
        db.Volumes.Add(new Volume
        {
            AppId = (await db.Apps.SingleAsync()).Id,
            Name = "uploads",
            StorageBytes = 1L * 1024 * 1024 * 1024,
        });
        db.ManagedServices.Add(Database(ws, storageBytes: 2L * 1024 * 1024 * 1024));
        await db.SaveChangesAsync();

        var volume = await db.Volumes.AsNoTracking().SingleAsync();
        var service = await db.ManagedServices.AsNoTracking().SingleAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        var appDisk = await db.BillingLedger.SingleAsync(l => l.ResourceType == BilledResourceType.Volume);
        var dbDisk = await db.BillingLedger.SingleAsync(l => l.ResourceType == BilledResourceType.ServiceVolume);

        appDisk.ResourceId.Should().Be(volume.Id);
        appDisk.AmountMinor.Should().Be(-20);

        dbDisk.ResourceId.Should().Be(service.Id);
        dbDisk.AmountMinor.Should().Be(-40);

        // Said out loud rather than left to follow from the two ids above: reusing Volume with a
        // service id would leave the bill unable to tell them apart, and would make the index's
        // correctness rest on two tables happening never to mint the same Guid.
        appDisk.ResourceType.Should().NotBe(dbDisk.ResourceType);
    }

    [Fact]
    public async Task A_databases_disk_is_charged_once_however_often_the_hour_is_retried()
    {
        // The key has to be stable across passes or the durable queue's retry is a second bill for
        // the same gibibytes.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        (await db.Plans.SingleAsync()).DiskGbHourMinor = 20;
        db.ManagedServices.Add(Database(ws, storageBytes: 2L * 1024 * 1024 * 1024));
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);
        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.BillingLedger.CountAsync(l => l.ResourceType == BilledResourceType.ServiceVolume))
            .Should().Be(1);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-1040);
    }

    [Fact]
    public async Task A_stopped_database_still_pays_for_the_disk_its_data_is_sitting_on()
    {
        // The clearest case of the agreed rate model. The container is down, so the size is charged
        // at the reserved rate — but the data has not gone anywhere, and the bytes are still held.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: 500, stoppedRatePerHour: 100);
        await db.SaveChangesAsync();

        (await db.Plans.SingleAsync()).DiskGbHourMinor = 20;
        db.ManagedServices.Add(Database(
            ws, storageBytes: 2L * 1024 * 1024 * 1024, status: ServiceStatus.Stopped));
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        var disk = await db.BillingLedger.SingleAsync(l => l.ResourceType == BilledResourceType.ServiceVolume);
        disk.AmountMinor.Should().Be(-40, "the disk is charged whatever the container is doing");

        var size = await db.BillingLedger.SingleAsync(l => l.ResourceType == BilledResourceType.Service);
        size.AmountMinor.Should().Be(-100);
        size.RunState.Should().Be(BilledRunState.Stopped);
    }

    [Fact]
    public async Task A_database_nobody_has_measured_is_not_billed_as_holding_nothing()
    {
        // Item 18 of the do-not-change list, arriving on a bill. A database with no reading is not a
        // database holding nothing, and a zero line would take its slot in the unique index for the
        // hour — so the corrected pass after somebody measures it would collide and be discarded as
        // "already charged". No line leaves the slot open and the hour still payable.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        (await db.Plans.SingleAsync()).DiskGbHourMinor = 20;
        db.ManagedServices.Add(Database(ws));
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.BillingLedger.AnyAsync(l => l.ResourceType == BilledResourceType.ServiceVolume))
            .Should().BeFalse();
        result.Failures.Should().ContainSingle(f => f.Contains("tenant-db") && f.Contains("never"));

        // The size is still charged. An unknown withholds the thing it could not price, never a
        // charge that was priced.
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-1000);
    }

    [Fact]
    public async Task A_database_nobody_measured_withholds_the_hours_plan_minimum()
    {
        // An unmeasured disk is not merely a missing line — it makes the hour's total unknown, and
        // the floor is the difference between that total and the plan's minimum. Charging the floor
        // anyway would let the corrected pass, once somebody measures the database, add its disk line
        // on top of a top-up that had already covered it: two passes, each looking right, adding up
        // to an overcharge. Reporting the disk without counting it as an unknown is exactly that bug.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: 500, planBaseRatePerHourMinor: 2_000);
        await db.SaveChangesAsync();

        (await db.Plans.SingleAsync()).DiskGbHourMinor = 20;
        db.ManagedServices.Add(Database(ws));
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.BillingLedger.AnyAsync(l => l.Kind == LedgerKind.PlanMinimumTopUp)).Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("plan minimum"));

        // And the floor is all that is withheld: the app and the database's size were priced, so
        // they are still charged. 500 + 500, and no top-up to 2,000.
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-1_000);
    }

    [Fact]
    public async Task A_measurement_that_ran_and_came_back_empty_handed_says_so_rather_than_never_measured()
    {
        // Two different states with two different things for an operator to do. "Nobody has measured
        // it" is a button somebody has to press; "the measurement ran and returned nothing" is a
        // broken measuring path, and telling them to go and press the button again wastes the one
        // warning they were going to read. ManagedService writes the timestamp even when the figure
        // is null precisely so the two can be told apart.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        (await db.Plans.SingleAsync()).DiskGbHourMinor = 20;
        db.ManagedServices.Add(Database(ws, measuredAt: Hour.AddHours(-3)));
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.Failures.Should().ContainSingle(f =>
            f.Contains("tenant-db") && f.Contains("did not come back with a figure"));
        result.Failures.Should().NotContain(f => f.Contains("never"));
    }

    [Fact]
    public async Task A_plan_with_no_gibibyte_price_writes_no_line_for_a_databases_disk_either()
    {
        // Null is not zero here as it is everywhere else on this branch, and the refusal is keyed on
        // the plan rather than on the disk: an operator who forgot to price a gibibyte needs one line
        // naming the plan, not one per thing sitting on it.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        db.Volumes.Add(new Volume
        {
            AppId = (await db.Apps.SingleAsync()).Id,
            Name = "uploads",
            StorageBytes = 1L * 1024 * 1024 * 1024,
        });
        db.ManagedServices.Add(Database(ws, storageBytes: 2L * 1024 * 1024 * 1024));
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.BillingLedger.AnyAsync(l => l.ResourceType == BilledResourceType.ServiceVolume))
            .Should().BeFalse();
        result.Failures.Should().ContainSingle(f => f.Contains("gibibyte-hour"));
    }

    [Fact]
    public async Task A_database_still_being_provisioned_is_not_reported_as_an_unmeasured_disk()
    {
        // One rule about whether the hour reserved anything, not two free to drift apart. A
        // provisioning service is not charged for its size because nothing is reserved yet, and its
        // disk — being created as the pass runs, and never measured — must not be reported either:
        // that report would count as an unknown and cost the whole workspace its plan minimum for
        // the hour somebody happened to create a database.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        (await db.Plans.SingleAsync()).DiskGbHourMinor = 20;
        db.ManagedServices.Add(Database(ws, status: ServiceStatus.Provisioning));
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.Failures.Should().BeEmpty();
        (await db.BillingLedger.AnyAsync(l => l.ResourceType == BilledResourceType.ServiceVolume))
            .Should().BeFalse();
    }

    // --- an unset price ---------------------------------------------------------------------

    [Fact]
    public async Task A_size_nobody_has_priced_writes_no_line_at_all_and_says_so()
    {
        // Null is not zero. A line of zero would be indistinguishable on the bill from a
        // deliberately free tier, and — worse — it would occupy that resource's slot in the unique
        // index for the hour, so the corrected run after somebody sets the price would collide and
        // be silently discarded as "already charged". No line leaves the slot open.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: null);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.LinesWritten.Should().Be(0);
        (await db.BillingLedger.CountAsync()).Should().Be(0);
        result.Failures.Should().ContainSingle(f => f.Contains("tenant-size"));
        result.AccountingComplete.Should().BeFalse("the durable scheduler must retry this hour after pricing is fixed");
    }

    [Fact]
    public async Task A_price_set_after_the_hour_can_still_be_backfilled_into_it()
    {
        // The other half of writing no line: the hour is recoverable. If the unpriced resource had
        // taken its index slot with a zero, this second pass would collide and change nothing, and
        // the money would be gone with every tick still reporting success.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: null);
        await db.SaveChangesAsync();

        // Paid up, so the app is still running when the corrected pass reaches it. A wallet at zero
        // is a workspace the first pass suspends, and the price set afterwards would then be a
        // stopped app's price — a different question from the one this test is asking.
        Harness.SetBalance(db, ws, Harness.PaidUp);

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.InstanceSizes.SingleAsync()).RunningRatePerHourMinor = 500;
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.LinesWritten.Should().Be(1);
        result.AccountingComplete.Should().BeTrue("the missing price was supplied and the hour is now fully accounted");
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor
            .Should().Be(Harness.PaidUp - 500);
    }

    [Fact]
    public async Task A_correction_moves_the_wallet_by_the_lines_it_added_not_by_the_whole_hour()
    {
        // The half-corrected hour, which is where a plausible implementation double-charges: one app
        // was priced and paid for on the first pass, the other was not. Once its size has a price,
        // the second pass must write ONE line and move the balance by ONE line — decrementing by the
        // hour's whole plan would bill the first app twice, and the ledger would still add up
        // against a wallet that no longer matches it.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        Harness.AddRunningAppOnAnUnpricedSize(db, ws);
        await db.SaveChangesAsync();

        // Paid up, so both apps are still running when the correction arrives. A wallet at zero is a
        // workspace the first pass suspends, and there is no correcting a running app's price on an
        // app this test has had stopped underneath it.
        Harness.SetBalance(db, ws, Harness.PaidUp);

        await Harness.Tick(db).ChargeHourAsync(Hour, default);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor
            .Should().Be(Harness.PaidUp - 500);

        (await db.InstanceSizes.SingleAsync(s => s.Key == "later")).RunningRatePerHourMinor = 300;
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.LinesWritten.Should().Be(1);
        (await db.BillingLedger.CountAsync(l => l.WorkspaceId == ws)).Should().Be(2);

        var wallet = await db.Wallets.SingleAsync(w => w.WorkspaceId == ws);
        wallet.BalanceMinor.Should().Be(Harness.PaidUp - 800);
        wallet.BalanceMinor.Should().Be(Harness.PaidUp +
            await db.BillingLedger.Where(l => l.WorkspaceId == ws).SumAsync(l => l.AmountMinor));
    }

    [Fact]
    public async Task One_unpriced_size_does_not_cost_the_rest_of_the_platform_its_hour()
    {
        // One forgotten price must not stop everybody else being billed, for the same reason one
        // locked table does not end the retention sweep.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "unpriced", ratePerHour: null);
        var paying = Harness.SeedWorkspaceWithOneRunningApp(db, "paying", ratePerHour: 700);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.WorkspacesCharged.Should().Be(1);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == paying)).BalanceMinor.Should().Be(-700);
    }

    [Fact]
    public async Task An_hour_that_could_not_be_priced_in_full_does_not_pay_the_plan_floor_yet()
    {
        // The floor is the difference between what the hour cost and the plan's minimum, so it can
        // only be worked out once the hour's cost is known. Charging it while a resource is missing
        // would make the corrected backfill add its line ON TOP of a floor that already covered it —
        // an overcharge, arrived at by two passes that each looked correct.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: null, planBaseRatePerHourMinor: 1000);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.BillingLedger.CountAsync()).Should().Be(0);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(0);
        result.Failures.Should().Contain(f => f.Contains("minimum"));
    }

    [Fact]
    public async Task An_hour_that_withholds_its_floor_still_charges_everything_it_could_price()
    {
        // The guarantee the rule above is only safe because of — and the one it could quietly break.
        // Withholding the floor must withhold the FLOOR, not the hour. Today an unknown decides one
        // argument, the floor handed to BillingHourPlan, while the billable list is built beside it
        // and never consulted about it; but nothing goes red if somebody couples the two, and the
        // result would be a workspace losing a charge it genuinely owed because a DIFFERENT resource
        // was unpriced. Silent, and in the direction of the platform's own pocket.
        //
        // One priced app and one unpriced one is the smallest fixture where that is visible, and the
        // plan's minimum is deliberately non-zero: at zero, "the floor was withheld" and "the floor
        // was nothing" write exactly the same ledger, which is why the correction test above cannot
        // stand in for this one.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: 500, planBaseRatePerHourMinor: 1000);
        Harness.AddRunningAppOnAnUnpricedSize(db, ws);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        // One: the app that HAD a price is charged, at its own rate and under its own name.
        result.LinesWritten.Should().Be(1);
        var line = await db.BillingLedger.SingleAsync();
        line.Kind.Should().Be(LedgerKind.Charge);
        line.ResourceName.Should().Be("tenant-api");
        line.AmountMinor.Should().Be(-500);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-500);

        // Two: the floor is not. A top-up of 1000 - 500 written here is the overcharge the rule
        // exists to prevent — the corrected pass would add the second app's line on top of a floor
        // that had already covered it, two passes each looking right and billing the hour twice.
        (await db.BillingLedger.AnyAsync(l => l.Kind == LedgerKind.PlanMinimumTopUp)).Should().BeFalse();

        // Three: and the shortfall nobody paid is said out loud, because an under-charge that
        // reports nothing is indistinguishable from an hour that was simply cheap.
        result.Failures.Should().ContainSingle(f => f.Contains("minimum"));
        result.Failures.Should().ContainSingle(f => f.Contains("later"));
    }

    [Theory]
    [InlineData(999L, true)]    // a top-up of 1 was genuinely owed, and genuinely withheld
    [InlineData(1000L, false)]  // the known charges meet the floor exactly: the shortfall is nothing
    [InlineData(1500L, false)]  // and past it
    public async Task The_withheld_floor_is_reported_only_when_a_top_up_could_have_been_due(
        long ratePerHour, bool reported)
    {
        // Withholding is only worth saying when there was something to withhold. Every rate is
        // non-negative, so an hour whose KNOWN charges already reach the floor would have reached it
        // with the unpriced ones added too: the shortfall was always going to be nothing and no
        // top-up was ever due. Announcing one anyway reports a charge that never existed, down the
        // same channel that carries the real ones — which is precisely what the per-entity
        // de-duplication in `Pass` was built to stop, arriving by another route.
        //
        // The unpriced app is still named in every row. This narrows which warning fires, never
        // whether the thing that caused it gets mentioned.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: ratePerHour, planBaseRatePerHourMinor: 1000);
        Harness.AddRunningAppOnAnUnpricedSize(db, ws);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.Failures.Any(f => f.Contains("minimum")).Should().Be(reported);
        result.Failures.Should().ContainSingle(f => f.Contains("later"));

        // Unchanged by the narrowing, and asserted so it stays that way: the floor line is still
        // withheld in all three rows, and the priced app still pays. This decides what is SAID, not
        // what is charged.
        (await db.BillingLedger.AnyAsync(l => l.Kind == LedgerKind.PlanMinimumTopUp)).Should().BeFalse();
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-ratePerHour);
    }

    [Fact]
    public async Task A_workspace_with_nothing_running_still_pays_its_plan_floor()
    {
        // The floor is what a plan IS. A workspace that stopped everything still holds its images,
        // its domains and its place, and the plan says what that costs.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: 0, planBaseRatePerHourMinor: 1000);
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        var line = await db.BillingLedger.SingleAsync(l => l.WorkspaceId == ws);
        line.Kind.Should().Be(LedgerKind.PlanMinimumTopUp);
        line.AmountMinor.Should().Be(-1000);
        line.ResourceId.Should().BeNull();
    }

    [Fact]
    public async Task A_plan_nobody_has_priced_charges_no_floor_and_says_so()
    {
        // Null is not zero here either. A plan with no minimum typed into it has not been declared
        // free — nobody has answered the question, and the operator is the only one who can.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: 500, planBaseRatePerHourMinor: null);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.BillingLedger.AnyAsync(l => l.Kind == LedgerKind.PlanMinimumTopUp)).Should().BeFalse();
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-500);
        result.Failures.Should().ContainSingle(f => f.Contains("tenant-plan"));
    }

    [Fact]
    public async Task A_size_deliberately_priced_at_zero_is_free_and_reported_by_nobody()
    {
        // The half of the distinction that must NOT be loud. Somebody typed a zero on purpose; a
        // free tier is a legitimate thing to sell, and an operator who is told about it every hour
        // learns to ignore the channel that also carries the real mistakes.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 0);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.Failures.Should().BeEmpty();
        (await db.BillingLedger.CountAsync()).Should().Be(0);
    }

    // --- the wallet -------------------------------------------------------------------------

    [Fact]
    public async Task A_workspace_that_has_never_had_a_wallet_gets_one_the_first_time_it_is_charged()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        db.Wallets.Remove(await db.Wallets.SingleAsync(w => w.WorkspaceId == ws));
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-500);
    }

    [Fact]
    public async Task The_wallet_the_meter_opens_is_denominated_in_the_currency_the_install_sells_in()
    {
        // The tick and a top-up are the only two places a wallet is ever opened, and they have to
        // agree: a customer whose first contact with billing was the meter would otherwise carry a
        // different currency from one who paid in advance, on the same install.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        db.Wallets.Remove(await db.Wallets.SingleAsync(w => w.WorkspaceId == ws));
        await db.SaveChangesAsync();

        await Harness.Tick(db, currency: "EUR").ChargeHourAsync(Hour, default);

        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).Currency.Should().Be("EUR");
    }

    [Fact]
    public async Task The_wallet_gets_a_new_stamp_every_time_the_tick_moves_it()
    {
        // The stamp is a concurrency token: EF checks the value it read against the row it is
        // updating. A token nothing ever changes always matches, so two writers both succeed and the
        // second silently overwrites the first — a lock that is present, configured and holds
        // nothing. The tick is the first thing to move a balance, so rotating it is its job.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();
        var before = (await db.Wallets.AsNoTracking().SingleAsync(w => w.WorkspaceId == ws)).ConcurrencyStamp;

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Wallets.AsNoTracking().SingleAsync(w => w.WorkspaceId == ws)).ConcurrencyStamp
            .Should().NotBe(before);
    }

    [Fact]
    public async Task A_wallet_somebody_else_moved_first_is_reread_rather_than_losing_the_hour()
    {
        // An administrator's credit and the tick can land in the same second. The ledger lines are
        // the truth and they are already worked out, so the answer is to read the balance again and
        // re-apply the same movement — not to drop the hour, and not to overwrite the credit.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        var hostile = Harness.TickContext(db);
        hostile.FailTheNextSaveWith = new DbUpdateConcurrencyException("somebody credited this wallet");

        var result = await Harness.Tick(db, through: hostile).ChargeHourAsync(Hour, default);

        result.Failures.Should().BeEmpty();
        result.LinesWritten.Should().Be(1);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-500);
        (await db.BillingLedger.CountAsync(l => l.WorkspaceId == ws)).Should().Be(1);
    }

    // --- what happens when the database says no ----------------------------------------------

    [Fact]
    public async Task Losing_the_race_to_the_unique_index_means_already_charged_not_charged_twice()
    {
        // The pre-read cannot win a race with another tick. The index is the authority, and losing
        // to it means the hour is already paid for — so the wallet must not move a second time.
        //
        // The other pass writes its row at the moment of the refusal, not before it: seeded earlier
        // the pre-read would find it and this test would never reach the catch it is named after,
        // and seeded never at all the 23505 would be an error no database could actually have
        // raised, which is a test asserting on an impossible state.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();
        var app = await db.Apps.SingleAsync();

        var hostile = Harness.TickContext(db);
        hostile.FailTheNextSaveWith = Refusal(PostgresErrorCodes.UniqueViolation, "duplicate key");
        hostile.WhenItRefuses = () =>
        {
            using var other = Harness.SystemContext(db.Store);
            other.BillingLedger.Add(new BillingLedgerEntry
            {
                WorkspaceId = ws,
                BillingHour = Hour,
                Kind = LedgerKind.Charge,
                AmountMinor = -500,
                ResourceType = BilledResourceType.App,
                ResourceId = app.Id,
                ResourceName = app.Name,
                RunState = BilledRunState.Running,
                RatePerHourMinor = 500,
            });
            other.SaveChanges();
        };

        var result = await Harness.Tick(db, through: hostile).ChargeHourAsync(Hour, default);

        result.LinesWritten.Should().Be(0);
        result.Failures.Should().BeEmpty("losing to the index is the index working, not a fault");

        // One line — the winner's — and a balance this pass did not touch.
        (await db.BillingLedger.CountAsync(l => l.WorkspaceId == ws)).Should().Be(1);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(0);
    }

    [Fact]
    public async Task A_unique_violation_from_some_other_index_is_not_read_as_a_paid_hour()
    {
        // 23505 says A unique index refused this, not which one — and this write touches two. Two
        // passes reaching a workspace's very first charge together both try to insert its wallet,
        // and reading that as "already charged" would drop an hour nobody billed while reporting
        // success. The hour is not on the bill afterwards, so the answer must not be "paid".
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        var hostile = Harness.TickContext(db);
        hostile.FailTheNextSaveWith = Refusal(PostgresErrorCodes.UniqueViolation, "duplicate key value violates \"IX_Wallets_WorkspaceId\"");

        var result = await Harness.Tick(db, through: hostile).ChargeHourAsync(Hour, default);

        result.LinesWritten.Should().Be(0);
        result.Failures.Should().ContainSingle(f => f.Contains("tenant"));
        (await db.BillingLedger.CountAsync()).Should().Be(0);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(0);
    }

    [Fact]
    public async Task A_write_that_failed_for_any_other_reason_is_reported_rather_than_read_as_paid()
    {
        // The catch is qualified on 23505 and nothing else. A bare DbUpdateException catch would
        // read a dropped connection, a check constraint or a serialisation failure as "already
        // charged" — an hour nobody billed, recorded as an hour already billed, with the tick
        // reporting success.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        var hostile = Harness.TickContext(db);
        hostile.FailTheNextSaveWith = Refusal(PostgresErrorCodes.AdminShutdown, "terminating connection");

        var result = await Harness.Tick(db, through: hostile).ChargeHourAsync(Hour, default);

        result.LinesWritten.Should().Be(0);
        result.Failures.Should().ContainSingle(f => f.Contains("tenant"));
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(0);
    }

    [Fact]
    public async Task A_write_that_took_the_connection_with_it_is_reported_as_itself()
    {
        // The other half of a failed write, and the reason the unique-violation catch is qualified
        // on 23505 rather than on DbUpdateException. A constraint violation leaves the connection
        // healthy, so the recovery read behind that catch can ask whether the hour is on the bill.
        // A dropped connection does not — asking there raises a second, unrelated exception that
        // replaces the first, and an operator goes hunting a disposed object while the fault that
        // actually stopped the billing goes unrecorded.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        var hostile = Harness.TickContext(db);
        hostile.FailTheNextSaveWith = Refusal(PostgresErrorCodes.AdminShutdown, "terminating connection");
        hostile.LoseTheConnectionToo = true;

        var result = await Harness.Tick(db, through: hostile).ChargeHourAsync(Hour, default);

        result.Failures.Should().ContainSingle(f => f.Contains("terminating connection"));
        (await db.BillingLedger.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task One_workspace_that_fails_does_not_stop_the_others_being_charged()
    {
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "broken", ratePerHour: 500);
        var paying = Harness.SeedWorkspaceWithOneRunningApp(db, "paying", ratePerHour: 700);
        await db.SaveChangesAsync();

        var hostile = Harness.TickContext(db);
        hostile.FailTheNextSaveWith = new DbUpdateException("no", new InvalidOperationException("no"));

        var result = await Harness.Tick(db, through: hostile).ChargeHourAsync(Hour, default);

        result.Failures.Should().ContainSingle();
        result.WorkspacesCharged.Should().Be(1);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == paying)).BalanceMinor.Should().Be(-700);
    }

    // --- catching up ---------------------------------------------------------------------------

    [Fact]
    public async Task A_missed_hour_is_backfilled_and_the_bound_is_reported()
    {
        // A panel that was down for a day must not have hosted for free, and must not silently
        // decide how much free hosting is acceptable either.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();

        var tick = Harness.Tick(db, maxBackfillHours: 3);
        var result = await tick.CatchUpAsync(lastChargedHour: Hour.AddHours(-10), default);

        result.HoursBackfilled.Should().Be(3);

        // 05:00 through 19:00 were owed — fifteen hours. Three were paid, so twelve were left, and
        // they start at 08:00 because the oldest are paid first. The numbers are asserted rather
        // than the word alone: a warning that says the bound was reached without saying WHICH hours
        // it left behind is a warning an operator cannot act on, and one naming the wrong hours
        // sends them looking in the wrong place while reading as if it worked.
        var warning = result.Failures.Should().ContainSingle(f => f.Contains("backfill")).Subject;
        warning.Should().Contain("12 hour(s)").And.Contain("08:00");
    }

    [Fact]
    public async Task A_catch_up_pays_the_oldest_hour_first_and_pays_each_one_once()
    {
        // Oldest first, because the bound drops the newest hours rather than the oldest: an hour
        // dropped from the far end is one nobody has been billed for yet, and the next catch-up
        // reaches it. Dropping the oldest instead would lose it for ever.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db, maxBackfillHours: 3)
            .CatchUpAsync(lastChargedHour: Hour.AddHours(-10), default);

        result.HoursBackfilled.Should().Be(3);
        result.WorkspacesCharged.Should().Be(1, "one workspace three times is still one workspace");

        var hours = await db.BillingLedger.Where(l => l.WorkspaceId == ws)
            .OrderBy(l => l.BillingHour).Select(l => l.BillingHour).ToListAsync();
        hours.Should().Equal(Hour.AddHours(-9), Hour.AddHours(-8), Hour.AddHours(-7));
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-300);
    }

    [Fact]
    public async Task A_catch_up_that_reaches_the_present_reports_no_bound()
    {
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();

        // 16:00 was the last hour charged, so 17:00 is the next one owed.
        var result = await Harness.Tick(db).CatchUpAsync(Harness.Clock.UtcNow.AddHours(-4), default);

        // 17:00, 18:00 and 19:00 have ended; the hour containing 20:00 has not.
        result.HoursBackfilled.Should().Be(3);
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task One_forgotten_price_is_reported_once_for_the_whole_catch_up_not_once_an_hour()
    {
        // A day of backfill on a size nobody priced is one mistake, not twenty-four. Repeating it
        // per hour is how the channel that carries real faults becomes the one nobody reads.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: null);
        await db.SaveChangesAsync();

        // 14:00 was the last hour charged, so 15:00 through 19:00 are owed — five hours, five
        // chances for the same forgotten price to be said five times.
        var result = await Harness.Tick(db).CatchUpAsync(Hour, default);

        result.HoursBackfilled.Should().Be(5);
        result.Failures.Should().ContainSingle(f => f.Contains("tenant-size"));
    }
}
