using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Identity;
using Harbora.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// What the credit screen does when <see cref="Harbora.Infrastructure.Billing.WalletService"/> does
/// not simply hand back a result — either because it refused the decision, or because the database
/// refused the write underneath it.
///
/// <para>
/// <see cref="WalletServiceTests"/> already proves the service's own half of each case: a mismatched
/// replay throws <see cref="InvalidOperationException"/>, and a genuine collision on the wallet row
/// throws the raw <see cref="DbUpdateException"/> rather than guessing. What is proved here is what
/// <see cref="TenantsController.Credit"/> does with those two exceptions once they arrive — an
/// administrator's browser has to land somewhere, and "somewhere" was, before this fix, a 500 for one
/// of them and a silent gap in the audit trail for the other.
/// </para>
/// </summary>
public class TenantsControllerCreditTests
{
    // --- fixture --------------------------------------------------------------------------------

    private sealed class Quota : IQuotaService
    {
        // Crediting a tenant never consults usage or capacity — throwing rather than answering
        // politely means a change that starts reading quota here says so by breaking this fixture
        // instead of passing silently over the gap.
        public Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct) =>
            throw new NotSupportedException("crediting a tenant does not read usage");

        public Task<QuotaCheck> CanAddAppAsync(
            Guid workspaceId, string? instanceSizeKey, Guid? excludeAppId, CancellationToken ct) =>
            throw new NotSupportedException("crediting a tenant does not check quota");

        public Task<QuotaCheck> CanAddServiceAsync(
            Guid workspaceId, string? instanceSizeKey, CancellationToken ct) =>
            throw new NotSupportedException("crediting a tenant does not check quota");
    }

    private sealed class Hasher : IPasswordHasher
    {
        public string Hash(string password) =>
            throw new NotSupportedException("crediting a tenant does not touch a password");

        public bool Verify(string password, string hash) =>
            throw new NotSupportedException("crediting a tenant does not touch a password");
    }

    /// <summary>The provider administrator making the credit — same identity WalletHarness uses.</summary>
    private sealed class Caller : ICurrentUser
    {
        public Guid? UserId => WalletHarness.Admin;
        public string? Email => "provider-admin@example.test";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId => WalletHarness.ProviderWorkspace;
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<(string Action, string? TargetType, string? TargetId, string? Metadata)> Entries { get; } = [];

        public Task LogAsync(
            string action, string? targetType = null, string? targetId = null, string? ipAddress = null,
            string? actorEmailOverride = null, Guid? userIdOverride = null, string? metadataJson = null,
            CancellationToken ct = default)
        {
            Entries.Add((action, targetType, targetId, metadataJson));
            return Task.CompletedTask;
        }
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private sealed record Fixture(BillingContext Db, TenantsController Controller, RecordingAudit Audit)
    {
        public string? Error => Controller.TempData["Error"] as string;

        /// <summary>What the administrator is told went right, as opposed to what went wrong.</summary>
        public string? Message => Controller.TempData["Message"] as string;
    }

    /// <summary>
    /// Wires the controller the way the provider console wires it: a <c>WalletService</c> over a
    /// context scoped to the administrator's own (provider) workspace, exactly as <see
    /// cref="WalletHarness.Wallets"/> does for <see cref="WalletServiceTests"/> — with the option of
    /// swapping in a context that can be made to refuse one save, which is the only way a unit test
    /// stages a genuine two-writers-collide race.
    /// </summary>
    private static Fixture Build(BillingContext db, BillingContext? walletContext = null)
    {
        var audit = new RecordingAudit();
        var wallet = WalletHarness.Wallets(db, through: walletContext);
        // The suspension is null here and nowhere else: crediting reaches ResumeAsync through
        // WalletService, which holds its own. The console's resume button is the only caller that
        // holds one directly, and TenantsControllerResumeTests drives that.
        var controller = new TenantsController(
            db, new Hasher(), new Quota(), wallet, new Caller(), audit, suspension: null!,
            features: null!,
            // Support impersonation starts from this same console; neither of its actions is reached
            // from here, and the HTTP tests drive that half through a real request.
            supportSessions: null!, accountSessions: null!,
            billing: Microsoft.Extensions.Options.Options.Create(new Harbora.Infrastructure.Billing.BillingOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.TempData = new TempDataDictionary(controller.HttpContext, new NullTempDataProvider());

        return new Fixture(db, controller, audit);
    }

    private static DbUpdateException Refusal(string sqlState, string message) =>
        new(message, new PostgresException(message, "ERROR", "ERROR", sqlState));

    // --- Important 1: a database collision is a refusal to retry, not a 500 -------------------

    [Fact]
    public async Task A_genuine_collision_on_the_wallet_row_sends_the_administrator_back_to_retry_rather_than_crashing()
    {
        // Two different credits landing on one brand-new workspace's very first wallet row together:
        // WalletService.WriteAsync cannot tell from 23505 alone which of its two unique constraints
        // refused the write, re-reads the ledger under THIS credit's own id, finds nothing, and
        // correctly throws rather than reports "already applied". Before this fix nothing downstream
        // caught that throw, so it reached the administrator's browser as an unhandled 500 instead of
        // the same kind of answer every other refusal on this form already gets.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, withWallet: false);
        await db.SaveChangesAsync();

        var hostile = WalletHarness.ProviderContext(db);
        hostile.FailTheNextSaveWith = Refusal(
            PostgresErrorCodes.UniqueViolation, "duplicate key value violates \"IX_Wallets_WorkspaceId\"");
        var f = Build(db, walletContext: hostile);

        // An unhandled DbUpdateException here — the bug this test is pinned against — would surface
        // as an exception out of this await, failing the test the same way it failed a real request:
        // by crashing rather than by landing somewhere.
        var result = await f.Controller.Credit(ws, Guid.CreateVersion7(), "1000", "card payment", default);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(TenantsController.ConfirmCredit));
        f.Error.Should().Contain("retry", "retrying is genuinely the right action once nothing was written");
        (await db.BillingLedger.IgnoreQueryFilters().AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    // --- Important 2: the refusal worth auditing is the one that was not ----------------------

    [Fact]
    public async Task An_id_reused_against_a_different_workspace_is_written_to_the_audit_trail()
    {
        // This is the "expensive mistake nobody reports" the whole idempotency design exists to
        // catch: an id already used for one customer's credit, offered again for another's. The
        // service refuses it and the screen shows the refusal once to whoever is looking — but before
        // this fix nothing recorded that the attempt happened at all, so a bug or a bad-faith reuse
        // would leave no trace anywhere but a page somebody already closed.
        await using var db = WalletHarness.SystemContext();
        var acme = WalletHarness.SeedWorkspace(db);
        var other = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        var f = Build(db);
        var creditId = Guid.CreateVersion7();

        var first = await f.Controller.Credit(acme, creditId, "1000", "card payment", default);
        first.Should().BeOfType<RedirectToActionResult>("the first attempt is an ordinary successful credit");

        var second = await f.Controller.Credit(other, creditId, "1000", "card payment", default);

        second.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(TenantsController.ConfirmCredit));
        f.Error.Should().Contain(other.ToString(), "the refusal names the workspace this attempt asked for");

        var refusal = f.Audit.Entries.Should().ContainSingle(e => e.Action == "billing.credit.refused").Subject;
        refusal.TargetId.Should().Be(other.ToString(), "the audit row is filed under the workspace this attempt was FOR");
        refusal.Metadata.Should().Contain(creditId.ToString())
            .And.Contain("100000", "the amount this attempt carried, on the entry as its own field")
            .And.Contain(acme.ToString(), "the collision names the workspace the id actually belongs to, not just the one refused");

        // The successful first attempt is audited too, under the ordinary action — refusing the
        // second must not have taken the first attempt's own row with it.
        f.Audit.Entries.Should().ContainSingle(e => e.Action == "billing.credit" && e.TargetId == acme.ToString());
    }

    [Fact]
    public async Task An_id_reused_for_a_different_amount_is_written_to_the_audit_trail_with_both_amounts()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        var f = Build(db);
        var creditId = Guid.CreateVersion7();

        await f.Controller.Credit(ws, creditId, "1000", "card payment", default);
        await f.Controller.Credit(ws, creditId, "5000", "card payment", default);

        var refusal = f.Audit.Entries.Should().ContainSingle(e => e.Action == "billing.credit.refused").Subject;
        // The attempted amount is on the entry as its own field (500000 minor units); the amount the
        // id was first used for is inside the reason text, because that is the ledger's own answer to
        // "already applied for what". Both have to be readable months later without a second query.
        refusal.Metadata.Should().Contain("500000").And.Contain("100000");
    }

    // --- an ordinary refusal below the service is never mistaken for one of these -------------

    [Fact]
    public async Task A_typed_amount_that_is_not_a_number_is_neither_a_500_nor_an_audited_refusal()
    {
        // The service is never reached for this one — MinorUnits.TryParseMajor refuses first — so
        // this refusal must keep behaving exactly as it always has: shown, and NOT written to the
        // audit trail the money-movement refusals above now use. Auditing every typo would bury the
        // one entry that is actually worth reading under noise from ordinary mistakes.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();
        var f = Build(db);

        await f.Controller.Credit(ws, Guid.CreateVersion7(), "not a number", "card payment", default);

        f.Error.Should().Contain("Enter the amount in figures");
        f.Audit.Entries.Should().BeEmpty();
    }

    // --- what the administrator is actually told ----------------------------------------------

    [Fact]
    public async Task The_screen_names_the_database_that_came_back_as_well_as_the_apps()
    {
        // An administrator crediting an account has, by this point, usually just told the customer
        // their services are coming back. "1 app(s) were started again" on a workspace whose database
        // also came back is an answer that leaves out the half they will be asked about first — and
        // if the database had NOT come back, the same sentence would read exactly the same way.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: -5_000, suspended: true, reason: SuspensionReason.NoBalance);
        WalletHarness.SeedStoppedAppOwedAStart(db, ws, "api");
        WalletHarness.SeedStoppedDatabaseOwedAStart(db, ws, "orders-db");
        await db.SaveChangesAsync();

        var f = Build(db);

        await f.Controller.Credit(ws, Guid.CreateVersion7(), "1000", "card payment", default);

        f.Message.Should().Contain("1 app(s) were started again")
            .And.Contain("1 database(s) were started again");
        f.Error.Should().BeNull();
    }
}
