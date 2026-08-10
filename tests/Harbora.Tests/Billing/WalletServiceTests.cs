using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// One place to build a workspace with a bill on it, and one place to build the wallet service.
///
/// <para>
/// Every service built here is given a context scoped to <b>somebody else's workspace</b>, and that
/// is the whole point of the harness. A credit is made from the provider console, by an
/// administrator whose own session belongs to the provider's workspace and not to the customer's —
/// so a wallet service that read through the tenant filter would find no wallet, no ledger and no
/// apps belonging to the tenant it had just been asked about, and would answer confidently about a
/// workspace it could not see. Handing every test the wrong scope makes "reaches the customer
/// anyway" a standing property rather than one test somebody remembers to write.
/// </para>
/// </summary>
internal static class WalletHarness
{
    /// <summary>The workspace the administrator making the credit belongs to. Never the customer's.</summary>
    public static readonly Guid ProviderWorkspace = new("d0c0ffee-0000-0000-0000-00000000000f");

    /// <summary>The instant every test runs at, so a credit's billing hour is a known value.</summary>
    public static readonly DateTimeOffset Now = new(2026, 8, 9, 20, 30, 0, TimeSpan.Zero);

    /// <summary>The top of the hour <see cref="Now"/> falls in — where a credit's line is filed.</summary>
    public static readonly DateTimeOffset Hour = new(2026, 8, 9, 20, 0, 0, TimeSpan.Zero);

    public static readonly FixedClock Clock = new(Now);

    /// <summary>A context that sees every tenant — for seeding the fixture and for asserting on it.</summary>
    public static BillingContext SystemContext(string? store = null) =>
        new(store ?? "wallet-" + Guid.NewGuid(), SystemWorkspaceScope.Instance);

    /// <summary>The same rows, read through the provider administrator's own session.</summary>
    public static BillingContext ProviderContext(BillingContext db) =>
        new(db.Store, new FixedWorkspaceScope(ProviderWorkspace));

    /// <summary>
    /// The wallet service, wired the way the provider console wires it: over a context belonging to
    /// the administrator's workspace, not the customer's.
    /// </summary>
    /// <param name="resumeThrough">
    /// A context for the suspension alone, so a test can make the <i>resume</i> fail while the
    /// credit's own write succeeds. They share one context in production; separating them here is
    /// the only way to stage the case where the money lands and the containers do not.
    /// </param>
    /// <param name="databases">
    /// The database stop/start route. A credit that lifts a suspension starts back the managed
    /// databases the suspension stopped, exactly as it does the apps.
    ///
    /// <para>
    /// Typed as the interface rather than as <see cref="FakeDatabaseOperations"/> so the <b>real</b>
    /// <c>ManagedServiceEngine</c> can be handed in. That matters more than it looks: the fake reads
    /// with <c>IgnoreQueryFilters()</c>, and until the engine did too, every test here that said a
    /// top-up brings the database back was green about something production could not do. See
    /// <c>ManagedServiceEngineTenancyTests</c>, and
    /// <see cref="WalletServiceTests.A_top_up_brings_the_database_back_through_the_engine_the_panel_actually_uses"/>,
    /// which is this path with nothing faked between the credit and the daemon.
    /// </para>
    /// </param>
    public static WalletService Wallets(
        BillingContext db,
        FakeAppOperations? operations = null,
        BillingContext? through = null,
        BillingContext? resumeThrough = null,
        IManagedServiceEngine? databases = null)
    {
        // The service writes through a context of its own, so anything this one still tracks is
        // about to be stale — and EF answers a query from the instance it is already tracking, which
        // would let an assertion read the balance as it was BEFORE the credit.
        db.ChangeTracker.Clear();

        var context = through ?? ProviderContext(db);
        var suspension = new BillingSuspension(
            resumeThrough ?? context,
            operations ?? new FakeAppOperations(ProviderContext(db)),
            databases ?? new FakeDatabaseOperations(ProviderContext(db)),
            Options.Create(new BillingOptions { Enabled = true }),
            NullLogger<BillingSuspension>.Instance);

        return new WalletService(context, suspension, Clock, NullLogger<WalletService>.Instance);
    }

    /// <summary>A customer workspace with a wallet, optionally already suspended and saying why.</summary>
    public static Guid SeedWorkspace(
        BillingContext db,
        long balanceMinor = 0,
        bool suspended = false,
        SuspensionReason reason = SuspensionReason.None,
        bool withWallet = true)
    {
        var workspace = new Workspace
        {
            Name = "Acme",
            Slug = "acme-" + Guid.NewGuid().ToString("n")[..8],
            IsSuspended = suspended,
            SuspendedReason = reason
        };
        db.Workspaces.Add(workspace);

        if (withWallet)
            db.Wallets.Add(new Wallet { WorkspaceId = workspace.Id, BalanceMinor = balanceMinor });

        return workspace.Id;
    }

    /// <summary>An app the suspension stopped and therefore owes a start.</summary>
    public static Guid SeedStoppedAppOwedAStart(BillingContext db, Guid workspaceId, string name)
    {
        var app = new App
        {
            WorkspaceId = workspaceId,
            Name = name,
            Slug = name,
            Status = AppStatus.Stopped,
            WasRunningAtSuspension = true
        };
        db.Apps.Add(app);
        return app.Id;
    }

    /// <summary>A managed database the suspension stopped and therefore owes a start.</summary>
    public static Guid SeedStoppedDatabaseOwedAStart(BillingContext db, Guid workspaceId, string name)
    {
        var service = new Harbora.Domain.Services.ManagedService
        {
            WorkspaceId = workspaceId,
            Name = name,
            Status = ServiceStatus.Stopped,
            // Named as the engine names one, because a test driving the real engine finds the
            // container by this and would silently find nothing if it were blank.
            ContainerName = $"harbora-svc-{name}",
            VolumeName = $"harbora-svc-{name}-data",
            WasRunningAtSuspension = true
        };
        db.ManagedServices.Add(service);
        return service.Id;
    }

    /// <summary>One line of one workspace's bill, written by hand so a breakdown has something to group.</summary>
    public static BillingLedgerEntry Line(
        Guid workspaceId,
        DateTimeOffset hour,
        long amountMinor,
        LedgerKind kind = LedgerKind.Charge,
        BilledResourceType type = BilledResourceType.App,
        Guid? resourceId = null,
        string name = "api",
        BilledRunState state = BilledRunState.Running,
        int hours = 1) =>
        new()
        {
            WorkspaceId = workspaceId,
            BillingHour = hour,
            Kind = kind,
            AmountMinor = amountMinor,
            ResourceType = type,
            ResourceId = resourceId,
            ResourceName = name,
            RunState = state,
            Hours = hours
        };

    /// <summary>A credit request with everything filled in, so a test only names the part it is about.</summary>
    public static CreditRequest Credit(
        Guid workspaceId,
        long amountMinor = 100_000,
        string note = "card payment",
        Guid? id = null,
        Guid? byUserId = null) =>
        new(id ?? Guid.CreateVersion7(), workspaceId, amountMinor, note, byUserId ?? Admin);

    /// <summary>The administrator every credit here is made by.</summary>
    public static readonly Guid Admin = new("a0000000-0000-0000-0000-00000000000a");
}

/// <summary>
/// Putting money into a workspace, and showing the customer where it went.
///
/// <para>
/// <b>A credit is the one money movement nobody double-checks.</b> A charge that lands twice is
/// reported within the hour by the person it was taken from; a credit that lands twice benefits the
/// only person who would have noticed. So the shape these tests are hunting is not "does the balance
/// go up" — it is "does it go up exactly once for one decision", and "does the money reach the
/// workspace the administrator named rather than the one their own session happens to be in".
/// </para>
/// </summary>
public class WalletServiceTests
{
    // --- what a credit is -------------------------------------------------------------------

    [Fact]
    public async Task A_credit_is_a_ledger_line_with_a_person_and_a_note_on_it()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        var result = await WalletHarness.Wallets(db)
            .CreditAsync(WalletHarness.Credit(ws, 100_000, "card payment"), default);

        result.BalanceMinor.Should().Be(100_000);
        result.Applied.Should().BeTrue();

        var line = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.Kind == LedgerKind.Credit);
        line.AmountMinor.Should().Be(100_000);
        line.CreatedByUserId.Should().Be(WalletHarness.Admin);
        line.Description.Should().Be("card payment");
        line.WorkspaceId.Should().Be(ws);
    }

    [Fact]
    public async Task A_credit_is_filed_under_the_hour_it_was_made_in_so_it_lands_on_a_statement()
    {
        // BillingHour is what every statement window filters on. A credit left at the default
        // DateTimeOffset would sit in year one and appear on no bill the customer will ever open,
        // while the balance moved — the ledger and the balance would disagree with nothing to show
        // for it.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        await WalletHarness.Wallets(db).CreditAsync(WalletHarness.Credit(ws), default);

        var line = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.Kind == LedgerKind.Credit);
        line.BillingHour.Should().Be(WalletHarness.Hour);
    }

    [Fact]
    public async Task A_credit_adds_to_the_balance_that_was_already_there()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: -25_000);
        await db.SaveChangesAsync();

        var result = await WalletHarness.Wallets(db)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        result.BalanceMinor.Should().Be(75_000);
    }

    [Fact]
    public async Task A_credit_to_a_workspace_that_has_never_been_billed_opens_its_wallet()
    {
        // The tick creates a wallet the first time it charges somebody. An account credited before
        // it has ever been charged has no row yet, and refusing there would mean a customer cannot
        // pay in advance.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, withWallet: false);
        await db.SaveChangesAsync();

        var result = await WalletHarness.Wallets(db)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        result.BalanceMinor.Should().Be(100_000);
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == ws))
            .BalanceMinor.Should().Be(100_000);
    }

    [Fact]
    public async Task A_credit_reaches_a_customer_the_administrators_own_session_cannot_see()
    {
        // The provider console is the only place this is called from, and an administrator's
        // session belongs to the provider's workspace. Read through the tenant filter, the
        // customer's wallet does not exist, their ledger is empty and their apps are gone — so a
        // filtered credit would open a SECOND wallet nobody can read and leave the real balance
        // where it was, having reported a number back to the person who typed it.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: -5_000);
        await db.SaveChangesAsync();

        var result = await WalletHarness.Wallets(db)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        result.BalanceMinor.Should().Be(95_000);
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().Where(w => w.WorkspaceId == ws).ToListAsync())
            .Should().ContainSingle("a second wallet would be a second, unreadable balance")
            .Which.BalanceMinor.Should().Be(95_000);
    }

    // --- applying one decision once ---------------------------------------------------------

    [Fact]
    public async Task The_same_credit_offered_twice_moves_the_money_once()
    {
        // The double-submitted form: one decision, one confirmation page, two POSTs. The id is
        // minted when the page is rendered, so both carry the same one and the second is a replay
        // of the first rather than a second decision.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        var credit = WalletHarness.Credit(ws, 100_000);
        var first = await WalletHarness.Wallets(db).CreditAsync(credit, default);
        var second = await WalletHarness.Wallets(db).CreditAsync(credit, default);

        first.Applied.Should().BeTrue();
        second.Applied.Should().BeFalse("the second POST carried a decision that had already been made");
        second.BalanceMinor.Should().Be(100_000);

        (await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(l => l.Kind == LedgerKind.Credit)).Should().Be(1);
    }

    [Fact]
    public async Task Two_credits_of_the_same_amount_in_the_same_hour_both_land()
    {
        // The other half of the rule, and the reason the ledger's unique index deliberately does not
        // cover credits: an administrator taking two payments from one customer in one hour is an
        // ordinary day. Only a repeated id is a repeat; a repeated amount is not.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        await WalletHarness.Wallets(db).CreditAsync(WalletHarness.Credit(ws, 100_000), default);
        var second = await WalletHarness.Wallets(db).CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        second.Applied.Should().BeTrue();
        second.BalanceMinor.Should().Be(200_000);
    }

    [Fact]
    public async Task A_credit_id_reused_against_a_different_workspace_is_refused_rather_than_swallowed()
    {
        // Silence here is the expensive answer. Reporting "already applied" would tell the
        // administrator the money had reached this customer when it reached another one, and the
        // customer who benefited is not going to raise it.
        await using var db = WalletHarness.SystemContext();
        var acme = WalletHarness.SeedWorkspace(db);
        var other = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        var credit = WalletHarness.Credit(acme, 100_000);
        await WalletHarness.Wallets(db).CreditAsync(credit, default);

        var replay = async () => await WalletHarness.Wallets(db)
            .CreditAsync(credit with { WorkspaceId = other }, default);

        await replay.Should().ThrowAsync<InvalidOperationException>();
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == other))
            .BalanceMinor.Should().Be(0);
    }

    [Fact]
    public async Task A_credit_id_reused_for_a_different_amount_is_refused_rather_than_swallowed()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        var credit = WalletHarness.Credit(ws, 100_000);
        await WalletHarness.Wallets(db).CreditAsync(credit, default);

        var replay = async () => await WalletHarness.Wallets(db)
            .CreditAsync(credit with { AmountMinor = 500_000 }, default);

        await replay.Should().ThrowAsync<InvalidOperationException>();
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == ws))
            .BalanceMinor.Should().Be(100_000);
    }

    [Fact]
    public async Task A_credit_id_reused_for_a_different_note_is_refused_rather_than_silently_dropping_the_correction()
    {
        // The workspace and the amount, wrong, put money in the wrong place — loudly, because
        // AlreadyAppliedAsync throws. A note is not money, but a back button, an edit and a resubmit
        // carrying the same id (a real shape: the page is re-rendered from what the browser already
        // has, not from a fresh GET) is a genuine second decision about what the line should say. Were
        // it accepted quietly the correction would vanish and the ledger would go on telling the first
        // note's story, with nothing on screen to say a second one was ever offered.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        var credit = WalletHarness.Credit(ws, 100_000, note: "first note");
        await WalletHarness.Wallets(db).CreditAsync(credit, default);

        var replay = async () => await WalletHarness.Wallets(db)
            .CreditAsync(credit with { Note = "corrected note" }, default);

        await replay.Should().ThrowAsync<InvalidOperationException>();
        (await db.BillingLedger.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Kind == LedgerKind.Credit))
            .Description.Should().Be("first note", "the refused note must not overwrite the one that was kept");
    }

    [Fact]
    public async Task A_credit_with_no_id_of_its_own_is_refused()
    {
        // An empty id is not an id. Left to the database it would be a real primary key, so exactly
        // one keyless credit could ever be written and every later one would collide with it and
        // read as "already applied" — the whole platform's credits, silently, after the first.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        var credit = async () => await WalletHarness.Wallets(db)
            .CreditAsync(WalletHarness.Credit(ws, id: Guid.Empty), default);

        await credit.Should().ThrowAsync<ArgumentException>();
    }

    // --- when two credits collide for real ---------------------------------------------------

    [Fact]
    public async Task A_unique_violation_from_the_wallet_row_alone_is_not_read_as_this_credit_already_applied()
    {
        // 23505 says a unique index refused this, not which one — and this write touches two: the
        // ledger's own primary key and Wallets.WorkspaceId, which two DIFFERENT first-ever credits
        // landing on one brand-new workspace together would both try to insert. The losing write's
        // recovery read asks the ledger for a row under ITS OWN id — this test never seeds one — so
        // AlreadyAppliedAsync finds nothing and WriteAsync is left with no honest answer but to throw:
        // reading a bare "already applied" here would tell an administrator their money landed while
        // dropping a credit nobody made.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, withWallet: false);
        await db.SaveChangesAsync();

        var hostile = WalletHarness.ProviderContext(db);
        hostile.FailTheNextSaveWith = Refusal(
            PostgresErrorCodes.UniqueViolation, "duplicate key value violates \"IX_Wallets_WorkspaceId\"");

        var credit = async () => await WalletHarness.Wallets(db, through: hostile)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        await credit.Should().ThrowAsync<DbUpdateException>();
        (await db.BillingLedger.IgnoreQueryFilters().AsNoTracking().AnyAsync()).Should().BeFalse();
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    // --- what a credit is not ---------------------------------------------------------------

    [Fact]
    public async Task A_credit_of_a_negative_amount_is_refused_because_that_is_a_charge_in_disguise()
    {
        // A charge is written by the hourly tick, against a resource, into a slot a unique index
        // guards. Letting it through this door would be a charge with none of that behind it, made
        // on the one screen where nobody expects money to leave.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: 50_000);
        await db.SaveChangesAsync();

        var credit = async () => await WalletHarness.Wallets(db)
            .CreditAsync(WalletHarness.Credit(ws, -10_000), default);

        await credit.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == ws))
            .BalanceMinor.Should().Be(50_000);
    }

    [Fact]
    public async Task A_credit_of_nothing_is_refused()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        var credit = async () => await WalletHarness.Wallets(db)
            .CreditAsync(WalletHarness.Credit(ws, 0), default);

        await credit.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await db.BillingLedger.IgnoreQueryFilters().AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_credit_with_no_note_is_refused_because_a_balance_that_moved_has_to_say_why()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        var credit = async () => await WalletHarness.Wallets(db)
            .CreditAsync(WalletHarness.Credit(ws, note: "   "), default);

        await credit.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_credit_to_a_workspace_that_does_not_exist_is_refused()
    {
        // Otherwise it opens a wallet and writes a ledger line for a tenant nobody can navigate to —
        // money that has left the provider's books and arrived nowhere.
        await using var db = WalletHarness.SystemContext();
        await db.SaveChangesAsync();

        var credit = async () => await WalletHarness.Wallets(db)
            .CreditAsync(WalletHarness.Credit(Guid.NewGuid()), default);

        await credit.Should().ThrowAsync<InvalidOperationException>();
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    // --- and what it lifts -------------------------------------------------------------------

    [Fact]
    public async Task A_credit_that_clears_the_debt_lifts_a_no_balance_suspension()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: -5_000, suspended: true, reason: SuspensionReason.NoBalance);
        var app = WalletHarness.SeedStoppedAppOwedAStart(db, ws, "api");
        await db.SaveChangesAsync();

        var operations = new FakeAppOperations(WalletHarness.ProviderContext(db));
        var result = await WalletHarness.Wallets(db, operations)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        result.StillSuspended.Should().BeFalse();
        result.AppsStarted.Should().Be(1);
        result.Failures.Should().BeEmpty();
        operations.Started.Should().Equal(app);

        var workspace = await db.Workspaces.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == ws);
        workspace.IsSuspended.Should().BeFalse();
        workspace.SuspendedReason.Should().Be(SuspensionReason.None);
    }

    [Fact]
    public async Task A_credit_brings_back_the_database_the_suspension_stopped_and_counts_it_apart()
    {
        // The database is the workload a top-up most needs to bring back, because every app the same
        // credit just restarted is talking to it. Counted apart from the apps rather than added to
        // them: an administrator told "2 workloads came back" has been told nothing about the only
        // one whose absence makes the other useless.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: -5_000, suspended: true, reason: SuspensionReason.NoBalance);
        var app = WalletHarness.SeedStoppedAppOwedAStart(db, ws, "api");
        var database = WalletHarness.SeedStoppedDatabaseOwedAStart(db, ws, "orders-db");
        await db.SaveChangesAsync();

        var operations = new FakeAppOperations(WalletHarness.ProviderContext(db));
        var databases = new FakeDatabaseOperations(WalletHarness.ProviderContext(db));
        var result = await WalletHarness.Wallets(db, operations, databases: databases)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        result.AppsStarted.Should().Be(1);
        result.DatabasesStarted.Should().Be(1);
        result.StillSuspended.Should().BeFalse();
        result.Failures.Should().BeEmpty();

        operations.Started.Should().Equal(app);
        databases.Started.Should().Equal(database);
        (await db.ManagedServices.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == database))
            .Status.Should().Be(ServiceStatus.Running);
    }

    [Fact]
    public async Task A_top_up_brings_the_database_back_through_the_engine_the_panel_actually_uses()
    {
        // The test above proves the arithmetic and the counting with FakeDatabaseOperations standing
        // in for the engine — and that fake reads with IgnoreQueryFilters() while, until this branch,
        // ManagedServiceEngine did not. So it was green about precisely the thing production could
        // not do: a credit is made from the provider console, inside a request whose ambient
        // workspace is the PROVIDER's, and the engine's filtered read of the customer's row matched
        // nothing and threw "Sequence contains no elements" before a node was reached. Every managed
        // database of a customer who had just paid stayed down.
        //
        // Nothing stands between CreditAsync and the daemon here. One context serves the credit, the
        // resume and the engine, which is the single scoped context a real request hands all three.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: -5_000, suspended: true, reason: SuspensionReason.NoBalance);
        var database = WalletHarness.SeedStoppedDatabaseOwedAStart(db, ws, "orders-db");
        await db.SaveChangesAsync();

        var console = WalletHarness.ProviderContext(db);
        var docker = new FakeDockerEngine();
        await docker.RunContainerAsync(new DockerRunRequest(
            "postgres:16", "harbora-svc-orders-db", "harbora",
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["harbora.managed"] = "true", ["harbora.service"] = "orders-db" },
            [], 5432, 0, 0, null), default);

        var engine = new Harbora.Infrastructure.Services.ManagedServiceEngine(
            console, new SingleEngineFactory(docker), new PassthroughProtector(), new NoopJobQueue(),
            new BillingGate(console, Options.Create(new BillingOptions { Enabled = true })),
            Options.Create(new HarboraRuntimeOptions()), WalletHarness.Clock,
            NullLogger<Harbora.Infrastructure.Services.ManagedServiceEngine>.Instance);

        var result = await WalletHarness.Wallets(db, through: console, databases: engine)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        result.DatabasesStarted.Should().Be(1);
        result.StillSuspended.Should().BeFalse();
        result.Failures.Should().BeEmpty();

        // The claim the message on the screen makes, checked against the daemon rather than against
        // a counter the resume kept about itself.
        docker.Calls.Should().Contain(
            c => c.Operation == "RestartContainerAsync" && c.Target == "harbora-svc-orders-db",
            "the customer's own container coming back is the whole of what a top-up buys them");

        (await db.ManagedServices.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == database))
            .Status.Should().Be(ServiceStatus.Running);
    }

    [Fact]
    public async Task A_credit_that_could_not_start_the_database_leaves_the_workspace_suspended()
    {
        // The money lands and is kept — that is settled elsewhere — but the suspension stays up,
        // because lifting it would discard the marker that is the only record this database was ever
        // running. The administrator is told, in the same breath as the balance.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: -5_000, suspended: true, reason: SuspensionReason.NoBalance);
        var database = WalletHarness.SeedStoppedDatabaseOwedAStart(db, ws, "orders-db");
        await db.SaveChangesAsync();

        var databases = new FakeDatabaseOperations(WalletHarness.ProviderContext(db));
        databases.Refuses[database] = "the node is unreachable";

        var result = await WalletHarness.Wallets(db, databases: databases)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        result.BalanceMinor.Should().Be(95_000, "the money is committed whatever the containers do");
        result.DatabasesStarted.Should().Be(0);
        result.StillSuspended.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("orders-db") && f.Contains("unreachable"));
    }

    [Fact]
    public async Task A_credit_leaves_a_suspension_the_provider_made_by_hand_exactly_where_it_is()
    {
        // Paying a bill is not a request to undo an operator's decision, and the money still lands.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: -5_000, suspended: true, reason: SuspensionReason.Manual);
        WalletHarness.SeedStoppedAppOwedAStart(db, ws, "api");
        await db.SaveChangesAsync();

        var operations = new FakeAppOperations(WalletHarness.ProviderContext(db));
        var result = await WalletHarness.Wallets(db, operations)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        result.BalanceMinor.Should().Be(95_000);
        result.StillSuspended.Should().BeTrue();
        operations.Started.Should().BeEmpty();
        (await db.Workspaces.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == ws))
            .IsSuspended.Should().BeTrue();
    }

    [Fact]
    public async Task A_credit_too_small_to_clear_the_debt_leaves_the_workspace_suspended()
    {
        // Zero is not a balance — the gate refuses to start anything on one — so a top-up that only
        // reaches zero must not bring the apps back to be charged for an hour nobody can pay for.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: -100_000, suspended: true, reason: SuspensionReason.NoBalance);
        WalletHarness.SeedStoppedAppOwedAStart(db, ws, "api");
        await db.SaveChangesAsync();

        var operations = new FakeAppOperations(WalletHarness.ProviderContext(db));
        var result = await WalletHarness.Wallets(db, operations)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        result.BalanceMinor.Should().Be(0);
        result.StillSuspended.Should().BeTrue();
        operations.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task An_app_that_does_not_come_back_is_reported_rather_than_counted_as_resumed()
    {
        // The shape this branch keeps finding: a start route that returns without an exception and
        // without starting anything. The administrator has just told a customer their services are
        // coming back, so this must reach the screen rather than a log.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: -5_000, suspended: true, reason: SuspensionReason.NoBalance);
        var app = WalletHarness.SeedStoppedAppOwedAStart(db, ws, "api");
        await db.SaveChangesAsync();

        var operations = new FakeAppOperations(WalletHarness.ProviderContext(db));
        operations.ReportsSuccessWithoutDoingAnything.Add(app);

        var result = await WalletHarness.Wallets(db, operations)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        result.BalanceMinor.Should().Be(95_000, "the money landed whatever the containers did");
        result.AppsStarted.Should().Be(0);
        result.StillSuspended.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("api"));
    }

    [Fact]
    public async Task An_app_the_node_refused_to_start_is_reported_and_the_credit_is_kept()
    {
        // Two separate facts, committed separately on purpose. The money arrived; the containers are
        // a second job that can be retried. Rolling the credit back because a node was unreachable
        // would mean a customer who paid has neither their services nor their balance.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: -5_000, suspended: true, reason: SuspensionReason.NoBalance);
        var app = WalletHarness.SeedStoppedAppOwedAStart(db, ws, "api");
        await db.SaveChangesAsync();

        var operations = new FakeAppOperations(WalletHarness.ProviderContext(db));
        operations.Refuses[app] = "the node is unreachable";

        var result = await WalletHarness.Wallets(db, operations)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        result.BalanceMinor.Should().Be(95_000);
        result.Failures.Should().NotBeEmpty();
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == ws))
            .BalanceMinor.Should().Be(95_000);
    }

    [Fact]
    public async Task A_resume_that_throws_outright_does_not_take_the_credit_with_it()
    {
        // A node refusing one app is caught inside the suspension and comes back as a failure line —
        // which is the test above, and it never reaches this class's own catch. This is the other
        // shape: the resume itself dies, connection and all, after the money is already committed.
        // Letting that escape would surface to the administrator as a failed credit that had
        // nevertheless taken the payment.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: -5_000, suspended: true, reason: SuspensionReason.NoBalance);
        WalletHarness.SeedStoppedAppOwedAStart(db, ws, "api");
        await db.SaveChangesAsync();

        await using var resume = WalletHarness.ProviderContext(db);
        resume.FailTheNextSaveWith = new DbUpdateException("the connection to the database went away");

        var result = await WalletHarness.Wallets(db, resumeThrough: resume)
            .CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        result.BalanceMinor.Should().Be(95_000);
        result.Failures.Should().ContainSingle().Which.Should().Contain("went away");
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == ws))
            .BalanceMinor.Should().Be(95_000);
    }

    [Fact]
    public async Task A_credit_rotates_the_stamp_that_keeps_two_writers_from_overwriting_each_other()
    {
        // EF checks a concurrency token by comparing what it read against the row it is updating, so
        // a token nothing ever changes always matches: the hourly pass and this credit both succeed
        // and the second silently overwrites the first. That is last-write-wins on a balance, and it
        // looks exactly like a working lock from the outside — which is why the rotation is asserted
        // directly rather than left to be inferred from a race this lane cannot stage.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        var before = (await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(w => w.WorkspaceId == ws)).ConcurrencyStamp;

        await WalletHarness.Wallets(db).CreditAsync(WalletHarness.Credit(ws), default);

        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == ws))
            .ConcurrencyStamp.Should().NotBe(before);
    }

    [Fact]
    public async Task A_credit_line_carries_no_hours_and_no_rate_because_it_is_neither()
    {
        // The entity defaults Hours to 1, because nearly every line is one hour of one thing. A
        // credit is no hours of nothing, and a line left at the default would put an hour on the
        // bill that nobody spent — invisible today only because credits are kept off the breakdown,
        // and waiting there for the day somebody totals hours across the whole ledger.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        await WalletHarness.Wallets(db).CreditAsync(WalletHarness.Credit(ws), default);

        var line = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(l => l.Kind == LedgerKind.Credit);
        line.Hours.Should().Be(0);
        line.RatePerHourMinor.Should().Be(0);
        line.RunState.Should().Be(BilledRunState.NotApplicable);
        line.ResourceType.Should().Be(BilledResourceType.None);
    }

    [Fact]
    public async Task Offering_the_same_credit_again_finishes_a_resume_that_did_not_finish()
    {
        // A replay writes no money, and that is not the same as doing nothing. The first attempt
        // left the customer paid-up and still down; asking again is how the administrator finishes
        // the job without inventing a second credit to do it with.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: -5_000, suspended: true, reason: SuspensionReason.NoBalance);
        var app = WalletHarness.SeedStoppedAppOwedAStart(db, ws, "api");
        await db.SaveChangesAsync();

        var refusing = new FakeAppOperations(WalletHarness.ProviderContext(db));
        refusing.Refuses[app] = "the node is unreachable";

        var credit = WalletHarness.Credit(ws, 100_000);
        var first = await WalletHarness.Wallets(db, refusing).CreditAsync(credit, default);
        var second = await WalletHarness.Wallets(db, new FakeAppOperations(WalletHarness.ProviderContext(db)))
            .CreditAsync(credit, default);

        first.StillSuspended.Should().BeTrue();
        second.Applied.Should().BeFalse("no second credit was written");
        second.AppsStarted.Should().Be(1);
        second.StillSuspended.Should().BeFalse();
        second.BalanceMinor.Should().Be(95_000);
    }

    // --- the bill ----------------------------------------------------------------------------

    [Fact]
    public async Task The_breakdown_separates_the_hours_an_app_ran_from_the_hours_it_sat_stopped()
    {
        // The question the customer actually asks: this app was up for ten hours and idle for
        // fourteen, so what did each cost me.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var api = Guid.NewGuid();

        for (var h = 0; h < 10; h++)
            db.BillingLedger.Add(WalletHarness.Line(ws, Day.AddHours(h), -1_000, resourceId: api));
        for (var h = 10; h < 24; h++)
            db.BillingLedger.Add(WalletHarness.Line(
                ws, Day.AddHours(h), -100, resourceId: api, state: BilledRunState.Stopped));
        await db.SaveChangesAsync();

        var costs = await WalletHarness.Wallets(db).BreakdownAsync(ws, Day, Day.AddDays(1), default);

        var line = costs.Single(c => c.Name == "api");
        line.RunningHours.Should().Be(10);
        line.StoppedHours.Should().Be(14);
        line.TotalMinor.Should().Be(-(10 * 1_000 + 14 * 100));
    }

    [Fact]
    public async Task A_deleted_app_still_appears_on_the_bill_it_was_charged_on()
    {
        // The reason ResourceName is copied rather than joined. The bill is a record of what was
        // charged, and a join to a row somebody removed last week renders a blank where the customer
        // is looking for a name.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var app = new App { WorkspaceId = ws, Name = "api", Slug = "api" };
        db.Apps.Add(app);
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000, resourceId: app.Id));
        await db.SaveChangesAsync();

        db.Apps.Remove(await db.Apps.IgnoreQueryFilters().SingleAsync(a => a.Slug == "api"));
        await db.SaveChangesAsync();

        var costs = await WalletHarness.Wallets(db).BreakdownAsync(ws, Day, Day.AddDays(1), default);

        costs.Should().Contain(c => c.Name == "api");
    }

    [Fact]
    public async Task The_breakdown_counts_the_hours_a_line_says_it_paid_for_rather_than_the_lines()
    {
        // Hours is a column on the line for exactly this reason: a backfilled or coalesced line pays
        // for more than one hour, and a breakdown that counted rows would tell the customer a
        // three-hour charge was one hour long while charging them for three.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -3_000, hours: 3));
        await db.SaveChangesAsync();

        var costs = await WalletHarness.Wallets(db).BreakdownAsync(ws, Day, Day.AddDays(1), default);

        costs.Single().RunningHours.Should().Be(3);
    }

    [Fact]
    public async Task The_breakdown_takes_the_hour_its_window_starts_on_and_leaves_the_one_it_ends_on()
    {
        // Half-open, so two consecutive statements never both claim the hour on the boundary. An
        // inclusive end would bill that hour on this month's bill and on next month's, and the two
        // together would not add up to the balance.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000, name: "first-hour"));
        db.BillingLedger.Add(WalletHarness.Line(ws, Day.AddDays(1), -1_000, name: "boundary-hour"));
        await db.SaveChangesAsync();

        var costs = await WalletHarness.Wallets(db).BreakdownAsync(ws, Day, Day.AddDays(1), default);

        costs.Select(c => c.Name).Should().Equal("first-hour");
    }

    [Fact]
    public async Task The_plan_minimum_is_its_own_line_rather_than_missing_from_the_bill()
    {
        // It is money that left the wallet. A breakdown that only knew about Charge would show a
        // customer a total smaller than the one they were billed, and there would be nothing on the
        // screen to explain the difference.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000, resourceId: Guid.NewGuid()));
        db.BillingLedger.Add(WalletHarness.Line(
            ws, Day, -4_000, LedgerKind.PlanMinimumTopUp, BilledResourceType.PlanBase,
            resourceId: null, name: "Starter", state: BilledRunState.NotApplicable));
        await db.SaveChangesAsync();

        var costs = await WalletHarness.Wallets(db).BreakdownAsync(ws, Day, Day.AddDays(1), default);

        costs.Should().Contain(c => c.Type == BilledResourceType.PlanBase && c.TotalMinor == -4_000);
        costs.Sum(c => c.TotalMinor).Should().Be(-5_000);
    }

    [Fact]
    public async Task A_correction_lands_on_the_bill_of_the_thing_it_corrects()
    {
        // Nothing in this ledger is ever edited or deleted; a mistake gets an opposing line. If the
        // breakdown did not read those, the bill would go on showing the wrong figure for that app
        // for ever, and the correction would exist only in the balance.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var api = Guid.NewGuid();
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000, resourceId: api));
        db.BillingLedger.Add(WalletHarness.Line(
            ws, Day, 400, LedgerKind.Adjustment, resourceId: api, state: BilledRunState.NotApplicable, hours: 0));
        await db.SaveChangesAsync();

        var costs = await WalletHarness.Wallets(db).BreakdownAsync(ws, Day, Day.AddDays(1), default);

        var line = costs.Single(c => c.Name == "api");
        line.TotalMinor.Should().Be(-600);
        line.RunningHours.Should().Be(1, "a correction is not an hour of anything");
    }

    [Fact]
    public async Task A_credit_is_not_a_resource_cost_and_stays_off_the_breakdown()
    {
        // What the customer paid in and what their apps cost are two different questions, and the
        // screen answers them in two places. Summed into one table, three separate top-ups become
        // one figure — and a top-up applied twice would be invisible in it.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000));
        await db.SaveChangesAsync();

        await WalletHarness.Wallets(db).CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        // The window reaches past the credit's own hour deliberately. Cut short of it, this test
        // would pass with the credit left in the breakdown, and prove only that a window excludes
        // what falls outside it.
        var costs = await WalletHarness.Wallets(db).BreakdownAsync(ws, Day, Day.AddDays(30), default);

        costs.Should().ContainSingle().Which.TotalMinor.Should().Be(-1_000);
    }

    [Fact]
    public async Task The_breakdown_and_the_credits_together_account_for_every_move_of_the_balance()
    {
        // The property that makes the bill checkable rather than merely printed: whatever the
        // breakdown leaves out has to be a credit, or the customer's total and their balance
        // disagree with nothing on screen to explain it.
        await using var db = WalletHarness.SystemContext();
        // The balance the seeded lines add up to, because the wallet is a cached total of the ledger
        // and a fixture where the two disagree could not prove anything about them agreeing.
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: -5_250);
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000, resourceId: Guid.NewGuid()));
        db.BillingLedger.Add(WalletHarness.Line(
            ws, Day, -4_000, LedgerKind.PlanMinimumTopUp, BilledResourceType.PlanBase,
            name: "Starter", state: BilledRunState.NotApplicable));
        db.BillingLedger.Add(WalletHarness.Line(
            ws, Day, -250, type: BilledResourceType.Volume, resourceId: Guid.NewGuid(),
            name: "data", state: BilledRunState.NotApplicable));
        await db.SaveChangesAsync();

        await WalletHarness.Wallets(db).CreditAsync(WalletHarness.Credit(ws, 100_000), default);

        // Wide enough to hold the credit's own hour, so "the breakdown leaves it out" is what is
        // being proved rather than "the window did".
        var costs = await WalletHarness.Wallets(db).BreakdownAsync(ws, Day, Day.AddDays(30), default);
        var credits = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.WorkspaceId == ws && l.Kind == LedgerKind.Credit)
            .SumAsync(l => l.AmountMinor);
        var balance = (await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor;

        (costs.Sum(c => c.TotalMinor) + credits).Should().Be(balance);
    }

    [Fact]
    public async Task An_app_renamed_between_two_hours_is_two_lines_under_the_names_it_was_charged_under()
    {
        // A consequence of copying the name onto the line rather than joining it, and a decision
        // rather than an accident: the bill says what the thing was called at the moment it was
        // charged, which is what somebody reconciling an old statement is looking for.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var app = Guid.NewGuid();
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000, resourceId: app, name: "api"));
        db.BillingLedger.Add(WalletHarness.Line(ws, Day.AddHours(1), -1_000, resourceId: app, name: "gateway"));
        await db.SaveChangesAsync();

        var costs = await WalletHarness.Wallets(db).BreakdownAsync(ws, Day, Day.AddDays(1), default);

        costs.Select(c => c.Name).Should().BeEquivalentTo(["api", "gateway"]);
    }

    [Fact]
    public async Task A_disk_is_on_the_bill_with_a_cost_and_no_hours_running_or_stopped()
    {
        // A volume is neither up nor down; it is held. Printing "0 hours running" against it would
        // read as a disk that was switched off all month and charged for anyway.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        db.BillingLedger.Add(WalletHarness.Line(
            ws, Day, -250, type: BilledResourceType.Volume, resourceId: Guid.NewGuid(),
            name: "data", state: BilledRunState.NotApplicable));
        await db.SaveChangesAsync();

        var costs = await WalletHarness.Wallets(db).BreakdownAsync(ws, Day, Day.AddDays(1), default);

        var disk = costs.Single();
        disk.Type.Should().Be(BilledResourceType.Volume);
        disk.RunningHours.Should().Be(0);
        disk.StoppedHours.Should().Be(0);
        disk.TotalMinor.Should().Be(-250);
    }

    [Fact]
    public async Task The_breakdown_reads_a_customer_the_callers_own_session_cannot_see()
    {
        // Same reason as the credit: the provider console shows a tenant's bill from an
        // administrator's session, and a filtered read would answer "you were charged nothing".
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000));
        await db.SaveChangesAsync();

        var costs = await WalletHarness.Wallets(db).BreakdownAsync(ws, Day, Day.AddDays(1), default);

        costs.Should().ContainSingle();
    }

    [Fact]
    public async Task One_workspaces_bill_never_carries_another_workspaces_lines()
    {
        // The other half of ignoring the filter. Unfiltered reads answer about whichever workspace
        // they are asked about, so the predicate naming it is the only thing left keeping two
        // customers' bills apart.
        await using var db = WalletHarness.SystemContext();
        var acme = WalletHarness.SeedWorkspace(db);
        var other = WalletHarness.SeedWorkspace(db);
        db.BillingLedger.Add(WalletHarness.Line(acme, Day, -1_000, name: "acme-api"));
        db.BillingLedger.Add(WalletHarness.Line(other, Day, -9_000, name: "other-api"));
        await db.SaveChangesAsync();

        var costs = await WalletHarness.Wallets(db).BreakdownAsync(acme, Day, Day.AddDays(1), default);

        costs.Select(c => c.Name).Should().Equal("acme-api");
    }

    [Fact]
    public async Task The_most_expensive_thing_a_customer_ran_is_at_the_top_of_their_bill()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000, resourceId: Guid.NewGuid(), name: "small"));
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -9_000, resourceId: Guid.NewGuid(), name: "large"));
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -5_000, resourceId: Guid.NewGuid(), name: "medium"));
        await db.SaveChangesAsync();

        var costs = await WalletHarness.Wallets(db).BreakdownAsync(ws, Day, Day.AddDays(1), default);

        costs.Select(c => c.Name).Should().Equal("large", "medium", "small");
    }

    // --- the route the resume actually goes through ------------------------------------------

    [Fact]
    public async Task The_platform_stop_start_route_finds_an_app_belonging_to_another_workspace()
    {
        // The credit above is made from the provider console, so the resume it triggers runs inside
        // a request whose session belongs to the PROVIDER's workspace. BillingSuspension reads every
        // app unfiltered and then hands each id to IAppOperationsService — which read db.Apps through
        // the tenant filter, found nothing, and threw "Sequence contains no elements" before reaching
        // a node. Every app would be reported as one that did not come back, the workspace would stay
        // suspended, and a customer who had just paid would still be down.
        //
        // BillingSuspension's own remarks name this and name the fix: IgnoreQueryFilters on that
        // service's ResolveAsync AND on its SetStatusAsync, together and never one alone, because
        // unfiltering only the read turns a throw into an ExecuteUpdate that matches no rows and
        // reports success. GetLogsAsync is the verb asserted on here because it is the one that goes
        // through ResolveAsync without also writing a status — ExecuteUpdate is not implemented by
        // this lane's provider at all, so the write half is pinned in the Postgres lane instead.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var app = WalletHarness.SeedStoppedAppOwedAStart(db, ws, "api");
        await db.SaveChangesAsync();

        await using var providerSession = WalletHarness.ProviderContext(db);
        var operations = OperationsOver(providerSession);

        var logs = await operations.GetLogsAsync(app, 10, default);

        logs.Should().BeEmpty("no container is running for it — but the app itself was found");
    }

    /// <summary>
    /// The real <see cref="AppOperationsService"/> over the given context, with the node, the proxy
    /// and the port book stood in for. Only the database scope is real, because the scope is the
    /// whole question.
    /// </summary>
    private static AppOperationsService OperationsOver(BillingContext db)
    {
        var docker = new FakeDockerEngine();
        var ingress = new NodeIngressRegistry(
            Options.Create(new NodeAgentControlPlaneOptions()), NullLogger<NodeIngressRegistry>.Instance);

        return new AppOperationsService(
            db,
            new FakeServerEngineFactory(docker),
            new RecordingProxyEngine(() => []),
            new BillingGate(db, Options.Create(new BillingOptions())),
            new HostPortAllocator(db, ingress, NullLogger<HostPortAllocator>.Instance),
            NullLogger<AppOperationsService>.Instance);
    }

    /// <summary>The day every breakdown here is taken over, so a window has something to cut.</summary>
    private static readonly DateTimeOffset Day = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The refusal PostgreSQL would raise, wrapped the way EF delivers it. Built rather than mocked
    /// for the same reason BillingTickTests builds its own: the SQLSTATE is the whole subject of the
    /// test that uses it.
    /// </summary>
    private static DbUpdateException Refusal(string sqlState, string message) =>
        new(message, new PostgresException(message, "ERROR", "ERROR", sqlState));
}
