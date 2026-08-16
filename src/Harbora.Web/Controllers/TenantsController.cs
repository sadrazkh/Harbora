using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Provider console: manage the customer workspaces (tenants) hosted on this platform — create
/// them, assign a plan, suspend/resume, and manage their members. Restricted to Owners/Admins.
/// </summary>
[Authorize(Policy = Capabilities.TenantsManage)]
[Route("tenants")]
public sealed partial class TenantsController(
    HarboraDbContext db,
    IPasswordHasher hasher,
    IQuotaService quota,
    Harbora.Infrastructure.Billing.WalletService wallet,
    ICurrentUser currentUser,
    IAuditLogger audit,
    Harbora.Infrastructure.Billing.BillingSuspension suspension,
    IFeatureGate features,
    Microsoft.Extensions.Options.IOptions<Harbora.Infrastructure.Billing.BillingOptions> billing) : Controller
{
    /// <summary>
    /// What to label a figure with when the workspace has no wallet to read a code off. The
    /// install's setting, not the shipped default: a provider selling in something else would
    /// otherwise be shown the wrong money on every tenant the meter has not reached yet.
    /// </summary>
    private string FallbackCurrency => billing.Value.CurrencyOrDefault;

    /// <summary>Where the request came from, for the audit trail on a money movement.</summary>
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Tenants";

        var workspaces = await db.Workspaces.OrderByDescending(w => w.IsDefault).ThenBy(w => w.Name).ToListAsync(ct);
        var plans = await db.Plans.Where(p => p.IsEnabled).OrderBy(p => p.MonthlyPrice).ToListAsync(ct);
        var planName = plans.ToDictionary(p => p.Id, p => p.Name);

        // This page IS the cross-tenant view, so it opts out of the workspace filters explicitly.
        var appCounts = await db.Apps.IgnoreQueryFilters().GroupBy(a => a.WorkspaceId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var svcCounts = await db.ManagedServices.IgnoreQueryFilters().GroupBy(s => s.WorkspaceId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var memCounts = await db.WorkspaceMembers.IgnoreQueryFilters().GroupBy(m => m.WorkspaceId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);

        var vm = new TenantsPageViewModel { Plans = plans };
        foreach (var w in workspaces)
        {
            vm.Tenants.Add(new TenantRow(
                w.Id, w.Name, w.Slug, w.IsDefault, w.PlanId,
                w.PlanId is { } pid && planName.TryGetValue(pid, out var n) ? n : "Default",
                memCounts.GetValueOrDefault(w.Id), appCounts.GetValueOrDefault(w.Id), svcCounts.GetValueOrDefault(w.Id),
                w.IsSuspended,
                w.SuspendedReason == SuspensionReason.NoBalance));
        }
        return View(vm);
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string slug, Guid? planId, CancellationToken ct)
    {
        slug = Slugify(string.IsNullOrWhiteSpace(slug) ? name : slug);
        if (await db.Workspaces.AnyAsync(w => w.Slug == slug, ct))
        {
            TempData["Error"] = "A workspace with this slug already exists.";
            return RedirectToAction(nameof(Index));
        }

        db.Workspaces.Add(new Workspace
        {
            Name = string.IsNullOrWhiteSpace(name) ? slug : name,
            Slug = slug,
            PlanId = planId,
            IsDefault = false
        });
        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"Tenant '{slug}' created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/plan")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignPlan(Guid id, Guid? planId, CancellationToken ct)
    {
        await db.Workspaces.Where(w => w.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.PlanId, planId), ct);
        TempData["Message"] = "Plan updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/suspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suspend(Guid id, bool suspended, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound();
        if (ws.IsDefault) { TempData["Error"] = "The provider workspace cannot be suspended."; return RedirectToAction(nameof(Index)); }

        // A billing suspension is not this action's to lift by hand, and the two field writes below
        // are exactly how it used to try. They clear the reason, and the reason is the only thing
        // BillingSuspension.ResumeAsync will act on — so every app and database the suspension
        // stopped kept WasRunningAtSuspension set with nothing left in the platform that reads it.
        // Down containers, markers saying somebody owes them a start, and nobody who does: the
        // stranding BillingSuspension refuses to cause when it defers to an operator's suspension,
        // caused here instead by the operator lifting billing's.
        if (!suspended && ws.SuspendedReason == SuspensionReason.NoBalance)
            return await LiftBillingSuspensionAsync(ws, ct);

        ws.IsSuspended = suspended;
        // Says who. Billing lifts a suspension only when the reason is NoBalance, so recording
        // Manual here is what stops a customer's payment quietly undoing this decision — and
        // clearing the reason on the way out stops a stale one being read about a workspace that is
        // no longer suspended at all.
        ws.SuspendedReason = suspended ? SuspensionReason.Manual : SuspensionReason.None;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = suspended ? "Tenant suspended." : "Tenant resumed.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Hands a NoBalance resume to the code that made the suspension, and reports what came back.
    ///
    /// <para>
    /// <b>Routed rather than refused</b>, and the refusal is the tempting answer. "Only a top-up
    /// lifts this" is true of the ordinary case and leaves nothing at all for the workspaces that
    /// are actually stuck: one whose credit landed while a node was down, one reconciled by hand
    /// outside the ledger, one suspended before billing was switched off. Every one of those is in
    /// credit, still flagged, and has no button.
    /// </para>
    ///
    /// <para>
    /// Routing loses nothing, because the refusal still happens where it is true. Each start goes
    /// through <c>IBillingGate</c>; on an empty balance every one is refused, nothing is stranded,
    /// no marker is cleared, the reason stays <see cref="SuspensionReason.NoBalance"/> so a later
    /// top-up still recognises this suspension as its own — and the operator is shown, per workload,
    /// what did not come back. The platform works out which of the two answers is honest today
    /// instead of the operator guessing from a button that looks the same either way.
    /// </para>
    /// </summary>
    private async Task<IActionResult> LiftBillingSuspensionAsync(Workspace ws, CancellationToken ct)
    {
        var result = await suspension.ResumeAsync(ws.Id, ct);

        if (result.WorkspaceSuspended)
        {
            // Never dressed up as a partial success. The operator pressed a button labelled "resume"
            // and the workspace is still suspended; anything short of saying so plainly is how a
            // customer gets told their services are back while they are not.
            TempData["Error"] = string.Join(" ", result.Failures.Prepend(
                $"{ws.Name} is still suspended: it was suspended for an empty balance, and the " +
                "workloads it stopped could not all be started again. They are still recorded as " +
                "owed a start, so a top-up — or this button again — will try them once more."));

            return RedirectToAction(nameof(Index));
        }

        // Named apart rather than added together, as the credit screen names them: an administrator
        // told "2 workloads came back" has not been told whether the database is one of them, and
        // that is the half every app beside it depends on.
        TempData["Message"] =
            $"{ws.Name} resumed."
            + (result.AppsStarted > 0 ? $" {result.AppsStarted} app(s) were started again." : "")
            + (result.DatabasesStarted > 0 ? $" {result.DatabasesStarted} database(s) were started again." : "");

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// What crediting this tenant would do, before it does it.
    ///
    /// <para>
    /// A page rather than a box on the details screen, for the reason every destructive action on
    /// this panel already has one: the figure has to be looked at by somebody before the money
    /// moves, and a number typed into a row of other controls is a number nobody re-reads. Money in
    /// is not destructive, but it is permanent: nothing in this ledger is edited or deleted. A
    /// mistake can be offset from the tenant screen with a separately audited Adjustment line, but
    /// the original credit remains visible. That still makes this page the right place to check the
    /// workspace, amount and note before the append-only entry is written.
    /// </para>
    ///
    /// <para>
    /// It also mints the credit's id, which is what makes the whole thing idempotent. One rendering
    /// of this page is one decision: a double-click, a browser's back button and a retried POST all
    /// carry the id it was rendered with and apply once, while an administrator who really means to
    /// credit the same customer twice loads the page again and gets a new one.
    /// </para>
    /// </summary>
    [HttpGet("{id:guid}/credit")]
    public async Task<IActionResult> ConfirmCredit(Guid id, CancellationToken ct)
    {
        // Carried in TempData rather than in the query string. A refused attempt has to come back
        // with what was typed still in the boxes — otherwise the second go is a retype rather than a
        // correction — but an amount and somebody's note about a customer's payment have no business
        // in a URL, where they land in browser history and in every access log on the way.
        var vm = await CreditPageAsync(
            id, TempData["CreditAmount"] as string, TempData["CreditNote"] as string, ct);
        if (vm is null) return NotFound();

        ViewData["Title"] = $"Credit {vm.Name}";
        return View(vm);
    }

    /// <summary>
    /// Puts money on a tenant's account, and brings back whatever an empty balance had stopped.
    ///
    /// <para>
    /// Everything about the money is <see cref="Harbora.Infrastructure.Billing.WalletService"/>'s:
    /// this action parses what a person typed, names who they are, and reports back what happened —
    /// including the half that can fail on its own. A credit that landed while the customer's apps
    /// stayed down is the outcome this screen must not describe as success, because the
    /// administrator has usually just told them otherwise.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/credit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Credit(
        Guid id, Guid creditId, string? amount, string? note, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound();

        // Back to the confirmation page rather than to the tenant, and carrying what was typed: a
        // refusal that empties the form makes the second attempt a retype rather than a correction.
        // Each of these says a different thing on purpose — "that is not a number" and "a credit
        // puts money in" send an administrator to two different fixes, and one generic refusal
        // covering both is the shape that makes somebody guess.
        IActionResult Again(string error)
        {
            TempData["Error"] = error;
            TempData["CreditAmount"] = amount;
            TempData["CreditNote"] = note;
            return RedirectToAction(nameof(ConfirmCredit), new { id });
        }

        // A fresh id here would mean every POST is a new decision, which is the whole failure this
        // is built to prevent. It comes from the page the administrator confirmed on.
        if (creditId == Guid.Empty)
            return Again("That form did not carry a credit id, so it was not applied. Open the page again.");

        if (!Harbora.Web.Infrastructure.MinorUnits.TryParseMajor(amount, out var amountMinor))
            return Again("Enter the amount in figures, for example 250000 or 250000.50.");

        if (amountMinor <= 0)
            return Again(
                "A credit only puts money in. To take money off or correct a mistake, use Adjust " +
                "balance on this tenant's account; the original credit will remain in the ledger.");

        if (string.IsNullOrWhiteSpace(note))
            return Again("Say what this credit is for. It is the only thing on the line that explains why the balance moved.");

        if (currentUser.UserId is not { } byUserId)
            return Again("Sign in again — this credit could not be attributed to anybody.");

        Harbora.Infrastructure.Billing.CreditResult result;
        try
        {
            result = await wallet.CreditAsync(
                new Harbora.Infrastructure.Billing.CreditRequest(creditId, id, amountMinor, note.Trim(), byUserId),
                ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The service's own refusals, shown rather than turned into a 500. It is the last line
            // of defence behind the checks above and behind any other caller, so reaching it means a
            // real disagreement worth reading.
            //
            // Audited before it is shown, and this is the one attempt on this whole screen that is
            // worth auditing. A double-click or a second confirmation page produces a normal "already
            // applied" or a normal second credit, both logged below like any other; reaching THIS
            // catch means an id was reused for a workspace, an amount or a note that does not match
            // what it was first used for — the exact "expensive mistake nobody reports" this design
            // exists to refuse. The administrator who typed it sees the message once and moves on;
            // without a row here, that is the only place it would ever have been written down. The
            // full text goes in rather than a category, because a category would say a refusal
            // happened and nothing about which decision it was or what it collided with — and that is
            // exactly what somebody reading this audit log months later would need.
            await audit.LogAsync("billing.credit.refused", "workspace", id.ToString(), ClientIp,
                metadataJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    creditId, amountMinor, note = note.Trim(), reason = ex.Message
                }), ct: ct);
            return Again(ex.Message);
        }
        catch (DbUpdateException)
        {
            // Not the service's own refusal — the database's. WalletService.WriteAsync's remarks say
            // why: 23505 names a unique index refused the write, not which one, and this write can
            // collide on either the ledger's primary key or Wallets.WorkspaceId. Losing the first
            // race is answered by re-reading and reporting "already applied"; losing the SECOND is
            // what reaches here, because a genuine collision on the wallet row alone means no ledger
            // line was ever written under this id — nothing to find, so WriteAsync correctly throws
            // rather than guesses. No money moved either way, so unlike the catch above there is
            // nothing to name and nothing to audit: the honest answer is "try again", not "here is
            // what went wrong", because nothing did — two legitimate writes just arrived together.
            return Again(
                "Another write reached this account's balance at the same moment as this one, and " +
                "this one was refused rather than guessed at. Nothing was credited — retry it.");
        }

        // Written whether or not this POST was the one that moved the money. An audit trail that
        // only recorded the winning submission would show one line where two people pressed the
        // button, and "who tried" is half of what an audit of money is for.
        await audit.LogAsync("billing.credit", "workspace", id.ToString(), ClientIp,
            metadataJson:
            $"{{\"creditId\":\"{creditId}\",\"amountMinor\":{amountMinor},\"applied\":" +
            $"{result.Applied.ToString().ToLowerInvariant()}}}", ct: ct);

        TempData["Message"] = result.Applied
            ? $"Credited {Harbora.Web.Infrastructure.MinorUnits.Format(amountMinor)}. " +
              $"{ws.Name}'s balance is now {Harbora.Web.Infrastructure.MinorUnits.Format(result.BalanceMinor)}." +
              // Named separately rather than added together. An administrator has usually just told
              // the customer their services are coming back, and "2 workload(s)" does not answer the
              // only question that matters next, which is whether the database is one of them.
              (result.AppsStarted > 0 ? $" {result.AppsStarted} app(s) were started again." : "") +
              (result.DatabasesStarted > 0
                  ? $" {result.DatabasesStarted} database(s) were started again."
                  : "")
            : $"That credit had already been applied. {ws.Name}'s balance is " +
              $"{Harbora.Web.Infrastructure.MinorUnits.Format(result.BalanceMinor)} and no second line was written.";

        // Never folded into the message above. "Credited 500,000" and "their apps are still down"
        // are two different things to have to tell a customer.
        if (result.Failures.Count > 0)
            TempData["Error"] = string.Join(" ", result.Failures);

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>The tenant, their balance, and a freshly minted id for the credit being considered.</summary>
    private async Task<TenantCreditViewModel?> CreditPageAsync(
        Guid id, string? amount, string? note, CancellationToken ct)
    {
        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return null;

        // Unfiltered: this is the provider console, so the administrator's own session belongs to
        // the provider's workspace and a filtered read would report every tenant's balance as zero.
        var wallet = await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == id, ct);

        return new TenantCreditViewModel
        {
            WorkspaceId = ws.Id,
            Name = ws.Name,
            Slug = ws.Slug,
            CreditId = Guid.CreateVersion7(),
            HasWallet = wallet is not null,
            BalanceMinor = wallet?.BalanceMinor ?? 0,
            Currency = wallet?.Currency ?? FallbackCurrency,
            Suspended = ws.IsSuspended,
            SuspendedForNoBalance = ws.SuspendedReason == SuspensionReason.NoBalance,
            Amount = amount,
            Note = note
        };
    }

    [HttpGet("{id:guid}/adjustment")]
    public async Task<IActionResult> ConfirmAdjustment(Guid id, CancellationToken ct)
    {
        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound();
        var walletRow = await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == id, ct);
        ViewData["Title"] = $"Adjust {ws.Name}";
        return View(new TenantAdjustmentViewModel
        {
            WorkspaceId = id,
            AdjustmentId = Guid.CreateVersion7(),
            Name = ws.Name,
            BalanceMinor = walletRow?.BalanceMinor ?? 0,
            Currency = walletRow?.Currency ?? FallbackCurrency,
            Amount = TempData["AdjustmentAmount"] as string,
            Note = TempData["AdjustmentNote"] as string
        });
    }

    [HttpPost("{id:guid}/adjustment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Adjustment(
        Guid id, Guid adjustmentId, string? amount, string? note, CancellationToken ct)
    {
        IActionResult Again(string error)
        {
            TempData["Error"] = error;
            TempData["AdjustmentAmount"] = amount;
            TempData["AdjustmentNote"] = note;
            return RedirectToAction(nameof(ConfirmAdjustment), new { id });
        }

        if (adjustmentId == Guid.Empty) return Again("Open the adjustment page again; its id is missing.");
        if (!Harbora.Web.Infrastructure.MinorUnits.TryParseMajor(amount, out var amountMinor) || amountMinor == 0)
            return Again("Enter a non-zero amount. Positive returns money; negative removes money.");
        if (string.IsNullOrWhiteSpace(note)) return Again("Explain what this correction reverses.");
        if (currentUser.UserId is not { } userId) return Again("Sign in again before adjusting a balance.");

        Harbora.Infrastructure.Billing.AdjustmentResult result;
        try
        {
            result = await wallet.AdjustAsync(new Harbora.Infrastructure.Billing.AdjustmentRequest(
                adjustmentId, id, amountMinor, note.Trim(), userId), ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DbUpdateException)
        {
            await audit.LogAsync("billing.adjustment.refused", "workspace", id.ToString(), ClientIp,
                metadataJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    adjustmentId, amountMinor, reason = ex.Message
                }), ct: ct);
            return Again(ex.Message);
        }

        await audit.LogAsync("billing.adjustment", "workspace", id.ToString(), ClientIp,
            metadataJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                adjustmentId, amountMinor, result.Applied, result.BalanceMinor
            }), ct: ct);
        TempData["Message"] = result.Applied
            ? $"Adjustment applied. Balance is now {Harbora.Web.Infrastructure.MinorUnits.Format(result.BalanceMinor)}."
            : $"That adjustment was already applied. Balance remains {Harbora.Web.Infrastructure.MinorUnits.Format(result.BalanceMinor)}.";
        if (result.Failures.Count > 0) TempData["Error"] = string.Join(" ", result.Failures);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Limits a person to the projects they have been granted, or lifts the limit again. Off by
    /// default and reversible: turning it off restores exactly the access they had before.
    /// </summary>
    [HttpPost("{id:guid}/members/{userId:guid}/scope")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.TenantsManage)]
    public async Task<IActionResult> SetScope(Guid id, Guid userId, bool scoped, CancellationToken ct)
    {
        var membership = await db.WorkspaceMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.WorkspaceId == id && m.UserId == userId, ct);
        if (membership is null) return NotFound();

        // An administrator is never scoped — administering a workspace you can only see half of is
        // not administering it — so saying so is better than a switch that silently does nothing.
        if (membership.Role == WorkspaceRole.Admin && scoped)
        {
            TempData["Error"] = "An owner or admin is not limited to projects; change their role first.";
            return RedirectToAction(nameof(Details), new { id });
        }

        membership.ScopedToProjects = scoped;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = scoped
            ? "Limited to the projects granted below."
            : "This person can reach every project in the workspace again.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/members/{userId:guid}/grants")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.TenantsManage)]
    public async Task<IActionResult> AddGrant(
        Guid id, Guid userId, Guid projectId, Guid? environmentId, SystemRole role, CancellationToken ct)
    {
        if (!await db.WorkspaceMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.WorkspaceId == id && m.UserId == userId, ct)) return NotFound();
        if (role is not (SystemRole.Member or SystemRole.Operator or SystemRole.Viewer))
        {
            TempData["Error"] = "Choose Member, Operator or Viewer for a project grant.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (!await db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Id == projectId && p.WorkspaceId == id, ct))
            return NotFound();

        // The environment has to belong to the project it is being granted within, or the grant
        // would name a pair that never occurs and quietly match nothing.
        if (environmentId is { } e
            && !await db.Environments.IgnoreQueryFilters().AnyAsync(x => x.Id == e && x.ProjectId == projectId, ct))
            return NotFound();

        var existing = await db.ProjectGrants.IgnoreQueryFilters().FirstOrDefaultAsync(
            g => g.WorkspaceId == id && g.UserId == userId
                 && g.ProjectId == projectId && g.EnvironmentId == environmentId, ct);

        // Replaced rather than added twice: two grants for the same place would leave which one
        // applies down to ordering.
        if (existing is not null) existing.Role = role;
        else
            db.ProjectGrants.Add(new Harbora.Domain.Authorization.ProjectGrant
            {
                WorkspaceId = id, UserId = userId, ProjectId = projectId,
                EnvironmentId = environmentId, Role = role
            });

        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Access granted.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/grants/{grantId:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.TenantsManage)]
    public async Task<IActionResult> RemoveGrant(Guid id, Guid grantId, CancellationToken ct)
    {
        var grant = await db.ProjectGrants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.Id == grantId && g.WorkspaceId == id, ct);
        if (grant is null) return NotFound();

        db.ProjectGrants.Remove(grant);
        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Access removed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound();
        ViewData["Title"] = ws.Name;

        // Platform admin acting on another workspace: scoping to their own would return nothing.
        var rows = await db.WorkspaceMembers.IgnoreQueryFilters().Where(m => m.WorkspaceId == id)
            .Join(db.Users, m => m.UserId, u => u.Id,
                  (m, u) => new { u.Id, u.Email, u.DisplayName, Role = u.Role, u.IsActive, m.ScopedToProjects })
            .OrderBy(m => m.Email).ToListAsync(ct);

        // Grants, written out as sentences: a permission nobody can read is a permission nobody
        // audits, and this screen is where an audit would start.
        var projects = await db.Projects.IgnoreQueryFilters().Where(p => p.WorkspaceId == id)
            .Include(p => p.Environments).ToListAsync(ct);
        var projectName = projects.ToDictionary(p => p.Id, p => p.Name);
        var environmentName = projects.SelectMany(p => p.Environments).ToDictionary(e => e.Id, e => e.Name);

        var grants = await db.ProjectGrants.IgnoreQueryFilters().Where(g => g.WorkspaceId == id).ToListAsync(ct);

        var members = rows.Select(r => new TenantMember(r.Id, r.Email, r.DisplayName, r.Role.ToString(), r.IsActive)
        {
            ScopedToProjects = r.ScopedToProjects,
            Grants = grants.Where(g => g.UserId == r.Id)
                .Select(g => (g.Id, Harbora.Domain.Authorization.ProjectAccess.Describe(
                    g,
                    projectName.GetValueOrDefault(g.ProjectId, "(deleted project)"),
                    g.EnvironmentId is { } e ? environmentName.GetValueOrDefault(e, "(deleted environment)") : null)))
                .ToList()
        }).ToList();

        ViewBag.Projects = projects;

        var now = DateTimeOffset.UtcNow;
        var period = new DateOnly(now.Year, now.Month, 1);
        var metered = await db.UsageRecords.AsNoTracking().FirstOrDefaultAsync(r => r.WorkspaceId == ws.Id && r.Period == period, ct);

        // Unfiltered, because this is the provider console: the administrator's own session belongs
        // to the provider's workspace, and a filtered read would show every tenant a balance of zero.
        var walletRow = await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == ws.Id, ct);

        var reconciliation = await wallet.ReconcileAsync(ws.Id, ct);

        // Resolved for this tenant rather than read off their plan: an override is exactly the thing
        // an operator forgets they set, and a page showing the plan's answer would hide it.
        var isFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
        var verdicts = await features.EvaluateAllAsync(ws.Id, ct);
        var entitlements = Harbora.Domain.Features.PlatformFeatures.All
            .Select(f => new TenantFeature(
                f.Key, f.Name(isFa), f.Pitch(isFa),
                verdicts.TryGetValue(f.Key, out var v) ? v.State : f.Default,
                verdicts.TryGetValue(f.Key, out var d) ? d.DecidedBy : Harbora.Domain.Features.FeatureDecision.ShippedDefault))
            .ToList();

        return View(new TenantDetailsViewModel
        {
            WorkspaceId = ws.Id, Name = ws.Name, Slug = ws.Slug, IsDefault = ws.IsDefault, Suspended = ws.IsSuspended,
            HasWallet = walletRow is not null,
            BalanceMinor = walletRow?.BalanceMinor ?? 0,
            Currency = walletRow?.Currency ?? FallbackCurrency,
            LedgerBalanceMinor = reconciliation.LedgerBalanceMinor,
            BalanceDifferenceMinor = reconciliation.DifferenceMinor,
            Usage = await quota.GetUsageAsync(ws.Id, ct),
            MemoryGbHours = metered?.MemoryGbHours ?? 0,
            CpuCoreHours = metered?.CpuCoreHours ?? 0,
            AppCountPeak = metered?.AppCountPeak ?? 0,
            PeriodLabel = period.ToString("yyyy-MM"),
            Members = members,
            Features = entitlements
        });
    }

    /// <summary>
    /// The workspace's monthly usage as CSV, every recorded period. The metering already exists and
    /// is shown a month at a time on the page; this is the same figures in the form an invoice run
    /// or a spreadsheet needs, rather than transcribed by hand.
    /// </summary>
    [HttpGet("{id:guid}/usage.csv")]
    public async Task<IActionResult> UsageCsv(Guid id, CancellationToken ct)
    {
        var ws = await db.Workspaces.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound();

        var records = await db.UsageRecords.AsNoTracking()
            .Where(r => r.WorkspaceId == id)
            .OrderByDescending(r => r.Period)
            .ToListAsync(ct);

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("period,memoryGbHours,cpuCoreHours,appCountPeak");
        foreach (var r in records)
            csv.AppendLine(Harbora.Web.Infrastructure.CsvWriter.Row(
                r.Period.ToString("yyyy-MM"),
                r.MemoryGbHours.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                r.CpuCoreHours.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                r.AppCountPeak.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var bytes = System.Text.Encoding.UTF8.GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return File(bytes, "text/csv", $"harbora-usage-{ws.Slug}.csv");
    }

    /// <summary>Add a customer user to the workspace (create the account if the email is new).</summary>
    [HttpPost("{id:guid}/members")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMember(Guid id, string email, string? displayName, string? password, WorkspaceRole role, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound();

        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email))
        {
            TempData["Error"] = "Email is required.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            {
                TempData["Error"] = "A temporary password (min 8 chars) is required for a new user.";
                return RedirectToAction(nameof(Details), new { id });
            }
            user = new User
            {
                Email = email,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName,
                PasswordHash = hasher.Hash(password),
                Role = SystemRole.Member, // a tenant user, not a platform admin
                EmailVerifiedAt = DateTimeOffset.UtcNow
            };
            db.Users.Add(user);
        }

        if (await db.WorkspaceMembers.IgnoreQueryFilters().AnyAsync(m => m.WorkspaceId == id && m.UserId == user.Id, ct))
        {
            TempData["Error"] = "This user is already a member.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await using var quotaReservation = await quota.AcquireCreationLockAsync(id, ct);
        var seat = await quota.CanAddGovernedResourcesAsync(id,
            new GovernanceQuotaDelta(Members: 1), ct);
        if (!seat.Allowed)
        {
            TempData["Error"] = seat.Reason;
            return RedirectToAction(nameof(Details), new { id });
        }

        db.WorkspaceMembers.Add(new WorkspaceMember { Workspace = ws, User = user, Role = role });
        await db.SaveChangesAsync(ct);
        await quotaReservation.CommitAsync(ct);
        TempData["Message"] = $"Added {email} as {role}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/members/{userId:guid}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken ct)
    {
        await db.WorkspaceMembers.IgnoreQueryFilters()
            .Where(m => m.WorkspaceId == id && m.UserId == userId).ExecuteDeleteAsync(ct);
        TempData["Message"] = "Member removed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private static string Slugify(string value)
    {
        var slug = NonSlug().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "tenant-" + Guid.NewGuid().ToString("N")[..6] : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
