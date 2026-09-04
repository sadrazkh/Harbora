using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Harbora.Infrastructure.Billing;

/// <summary>
/// One administrator's decision to put money into one workspace.
///
/// <para>
/// It is a record rather than five parameters because three of its five fields would otherwise be
/// adjacent <see cref="Guid"/>s — the credit, the workspace it is for, and the person making it —
/// and a credit landing on the wrong account is the mistake on this screen that nobody reports. A
/// caller that swaps two names here does not compile; a caller that swaps two positional arguments
/// does, and takes money from one customer's provider and gives it to another's.
/// </para>
/// </summary>
/// <param name="Id">
/// <b>What makes a credit idempotent.</b> It becomes the ledger line's primary key, so applying the
/// same decision twice is not merely detected — it is impossible, refused by the strongest index in
/// the database rather than by a rule somebody has to remember.
///
/// <para>
/// The ledger's unique index on (WorkspaceId, ResourceType, ResourceId, BillingHour) deliberately
/// does not cover credits, and could not do this job: an administrator taking two payments from one
/// customer within one hour is an ordinary day, and a key made of the workspace and the hour would
/// refuse the second one. Nor can the amount and the note stand in — two identical top-ups are as
/// legitimate as two different ones. Only the caller knows whether a second request is a second
/// decision or the same decision arriving twice, so only the caller can say, and it says it here.
/// The panel mints this when it renders the confirmation page: a double-click, a browser's back
/// button and a retried POST all carry the id that page was rendered with, while an administrator
/// who really means to credit twice loads the page again and gets a new one.
/// </para>
/// </param>
/// <param name="AmountMinor">
/// Positive minor units. Signed the way the ledger stores it — a credit is money in — and refused
/// at zero or below, because a negative "credit" is a charge made through the one door with none of
/// a charge's ceremony behind it.
/// </param>
/// <param name="Note">Why the money moved, in the words of the person who moved it. Required.</param>
public sealed record CreditRequest(Guid Id, Guid WorkspaceId, long AmountMinor, string Note, Guid ByUserId);

/// <summary>A compensating ledger line. Positive returns money; negative removes an erroneous credit.</summary>
public sealed record AdjustmentRequest(Guid Id, Guid WorkspaceId, long AmountMinor, string Note, Guid ByUserId);

public sealed record AdjustmentResult(
    long BalanceMinor,
    bool Applied,
    bool StillSuspended,
    int AppsStarted,
    int DatabasesStarted,
    int AppsStopped,
    int DatabasesStopped,
    IReadOnlyList<string> Failures);

public sealed record WalletReconciliation(
    bool HasWallet,
    long WalletBalanceMinor,
    long LedgerBalanceMinor,
    long DifferenceMinor)
{
    public bool IsBalanced => DifferenceMinor == 0;
}

/// <summary>
/// What one credit did — the money, and then what the money was supposed to switch back on.
///
/// <para>
/// The two are reported separately because they are committed separately and can disagree. Returning
/// only the new balance would let the panel tell an administrator "credited 500,000" while every one
/// of that customer's apps stayed down and nothing said so — and the administrator has, by then,
/// usually just told the customer their services are coming back.
/// </para>
/// </summary>
/// <param name="Applied">
/// False when this credit was already on the ledger and nothing new was written. Not a failure: it
/// is the correct answer to the same decision arriving twice, and the balance beside it is real.
/// </param>
/// <param name="StillSuspended">The workspace's state after the call, not before it.</param>
/// <param name="DatabasesStarted">
/// Counted apart from <paramref name="AppsStarted"/> rather than folded into one total, because they
/// are two different pieces of news for the person reading them. An administrator told "3 workloads
/// came back" cannot tell whether the data layer is among them, and a database that did not is the
/// one absence that makes the other two useless.
/// </param>
/// <param name="Failures">
/// Every workload the top-up was meant to bring back and did not, plus anything that went wrong
/// reaching them. Empty is the only clean answer; nothing here throws, so this is the only place an
/// unfinished resume is visible.
/// </param>
public sealed record CreditResult(
    long BalanceMinor,
    bool Applied,
    bool StillSuspended,
    int AppsStarted,
    int DatabasesStarted,
    IReadOnlyList<string> Failures);

/// <summary>
/// One line of a customer's bill: what a single thing they held cost them, and how long it was up.
/// </summary>
/// <param name="Name">
/// The name copied onto the ledger lines at the moment they were written, never joined to the row it
/// came from. An app deleted last week still reads as a name here, which is the whole reason the
/// column exists — and, as a consequence, a resource renamed mid-period appears under both names,
/// because the bill says what a thing was called when it was charged.
/// </param>
/// <param name="TotalMinor">
/// Signed minor units, in the ledger's own convention: <b>negative is money out</b>. The sign is not
/// flipped here for the same reason it is not flipped on the ledger — so that summing is the whole
/// of the arithmetic and no reader needs a table of which kinds subtract. A correction in the
/// customer's favour lands in this figure as a positive number and makes the line smaller, which is
/// exactly what it did to their balance.
/// </param>
public sealed record ResourceCost(
    BilledResourceType Type,
    Guid? Id,
    string Name,
    int RunningHours,
    int StoppedHours,
    long TotalMinor)
{
    /// <summary>Every hour this resource was charged for, whichever state it was in.</summary>
    public int Hours => RunningHours + StoppedHours;

    /// <summary>
    /// What an hour of this resource averaged over the period, or <c>null</c> when it was charged for
    /// no hours at all.
    ///
    /// <para>
    /// The average actually charged rather than a rate read off the newest line. A rate can change
    /// mid-month, and a resource can spend part of it running and part stopped at a different rate —
    /// so the newest line's figure would describe one hour of the month and be presented as the
    /// month's. This is arithmetic on what the ledger really took.
    /// </para>
    ///
    /// <para>
    /// Null, not zero, when there are no hours: the disk and plan-minimum lines carry none — a disk
    /// is held rather than switched on — and dividing by nothing to get a nought would put an hourly
    /// rate on a line that has no hours. That is the same "unread is not empty" rule the rest of this
    /// codebase keeps.
    /// </para>
    /// </summary>
    public long? AverageHourlyMinor => Hours > 0 ? Math.Abs(TotalMinor) / Hours : null;
}

/// <summary>
/// One project's slice of a workspace's bill for a period, split further by environment
/// (<see cref="Environments"/>) — see <see cref="WalletService.BreakdownByProjectAsync"/> for how it
/// is built and why its total can never drift from <see cref="WalletService.BreakdownAsync"/>'s own.
/// </summary>
/// <param name="ProjectId">
/// Null for the one synthetic bucket that is not a real project — see <see cref="IsUnassigned"/>.
/// </param>
/// <param name="ProjectName">Null exactly when <see cref="ProjectId"/> is null; the view names that
/// bucket in whichever language it is rendering, the same way it names every other unmeasured state.</param>
/// <param name="Forecast">
/// This project's own slice of <see cref="CostForecast"/> — the same computation
/// <see cref="WalletService.ForecastAsync(Guid,DateTimeOffset,DateTimeOffset,CancellationToken)"/>
/// makes for the whole workspace, restricted to this project's resources, never a second formula.
/// Null when the caller did not ask for one (a closed period, or a suspended workspace — the same gate
/// <see cref="Harbora.Web.ViewModels.BillingPageViewModel.Forecast"/> already applies).
/// </param>
public sealed record ProjectCostGroup(
    Guid? ProjectId,
    string? ProjectName,
    IReadOnlyList<EnvironmentCostGroup> Environments,
    CostForecast? Forecast)
{
    /// <summary>The same figure summed twice: once here across environments, once inside each
    /// environment across resources. Both additions are over the exact <see cref="ResourceCost"/> rows
    /// <see cref="WalletService.BreakdownAsync"/> returned, so this can never disagree with the
    /// workspace total — it is a partition of the same numbers, not a second query.</summary>
    public long TotalMinor => Environments.Sum(e => e.TotalMinor);

    /// <summary>
    /// True for the one bucket that is not a project: the plan-minimum top-up (no resource at all),
    /// a mail domain or mailbox (workspace-level, no project of their own), or a resource whose row
    /// has since been deleted and so has no "current placement" left to ask. Never dropped from the
    /// report — named and visible instead, so the groups still sum to the workspace total.
    /// </summary>
    public bool IsUnassigned => ProjectId is null;
}

/// <summary>One environment's slice of a <see cref="ProjectCostGroup"/> — see its own remarks.</summary>
public sealed record EnvironmentCostGroup(
    Guid? EnvironmentId,
    string? EnvironmentName,
    IReadOnlyList<ResourceCost> Costs,
    CostForecast? Forecast)
{
    public long TotalMinor => Costs.Sum(c => c.TotalMinor);
}

/// <summary>
/// Money in, and the bill that says where the money went.
///
/// <para>
/// <b>Every read here ignores the tenant filter, and has to.</b> Both of this class's callers are
/// asking about a workspace that is not the one their session belongs to: a provider administrator
/// crediting a customer is signed in to the provider's own workspace, and the hourly pass has no
/// session at all. Read through the filter, the customer's wallet does not exist, their ledger is
/// empty and their apps are gone — so a credit would open a second, unreadable wallet and a bill
/// would report that a customer had been charged nothing. This is the same call
/// <see cref="BillingTick"/>, <see cref="BillingSuspension"/>, <see cref="BillingGate"/> and the
/// retention sweeper all make, for the same reason.
/// </para>
///
/// <para>
/// The consequence is stated plainly rather than left implicit: unfiltered reads answer about
/// whichever workspace they are given, so the <c>workspaceId</c> predicate on every query below is
/// the only thing keeping two customers' money apart. It is a parameter on every public method here
/// and is never defaulted from ambient state.
/// </para>
///
/// <para>
/// <b>Nothing here is switched off by <c>Billing:Enabled</c>.</b> The switch guards the acts that
/// take a customer's money or their uptime; accepting money and showing somebody what they were
/// charged do neither. An install that turns billing off after a suspension must still be able to
/// take a payment and lift it — which is the same asymmetry <see cref="BillingSuspension"/> draws
/// between suspending and resuming, arriving at the same answer.
/// </para>
/// </summary>
public sealed class WalletService(
    HarboraDbContext db,
    BillingSuspension suspension,
    ISystemClock clock,
    IOptions<BillingOptions> options,
    ILogger<WalletService> logger)
{
    /// <inheritdoc cref="BillingTick"/>
    /// <summary>
    /// How many times the wallet write is re-read and re-applied before the credit is abandoned.
    /// Small on purpose, and for the mirror image of the tick's reason: a conflict here means the
    /// hourly pass moved the balance in the same second, which happens once and not in a loop.
    /// </summary>
    private const int WalletWriteAttempts = 3;

    /// <summary>
    /// Puts money into a workspace, and lifts the suspension its absence caused.
    ///
    /// <para>
    /// The ledger line and the balance go in one <c>SaveChanges</c>, which is one transaction: the
    /// wallet is a cached total whose truth is <c>SUM(AmountMinor)</c>, and committing the two halves
    /// separately leaves a window in which the cache is a lie — plus a crash window in which it stays
    /// one until somebody reconciles.
    /// </para>
    ///
    /// <para>
    /// The resume is deliberately <b>outside</b> that transaction and cannot undo it. Starting
    /// containers reaches nodes that time out, refuse, and occasionally are not there; rolling the
    /// credit back because one of them was unreachable would leave a customer who has paid with
    /// neither their services nor their balance. So the money is committed first and kept, and what
    /// the resume could not do is reported.
    /// </para>
    ///
    /// <para>
    /// Safe to call again with the same <see cref="CreditRequest.Id"/>, and that is not only a
    /// safeguard against a double-click. A replay writes no second line and still runs the resume, so
    /// an administrator whose first attempt left a node unreachable finishes the job by asking again
    /// rather than by inventing a second credit to do it with.
    /// </para>
    /// </summary>
    public async Task<CreditResult> CreditAsync(CreditRequest credit, CancellationToken ct)
    {
        // An empty id is not an id, and letting one through would be worse than sloppy: Guid.Empty is
        // a perfectly good primary key, so exactly one keyless credit could ever be written and every
        // later one on the whole install would collide with it and be reported as "already applied".
        if (credit.Id == Guid.Empty)
            throw new ArgumentException(
                "A credit needs an id of its own — it is what stops the same decision being applied " +
                "twice, and Guid.Empty would be the same id for every credit ever made.",
                nameof(credit));

        if (credit.AmountMinor <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(credit), credit.AmountMinor,
                "A credit puts money in. A charge is written by the hourly pass, against a resource, " +
                "into a slot a unique index guards — it is not made through this door.");

        var note = credit.Note?.Trim();
        if (string.IsNullOrEmpty(note))
            throw new ArgumentException(
                "A credit needs a note. It is the only thing on the line that says why the balance " +
                "moved; every other column here is filled in by the machine.",
                nameof(credit));

        // Read before anything is written, and refused rather than tolerated. Crediting a workspace
        // that does not exist would open a wallet and write a ledger line for a tenant nobody can
        // navigate to — money out of the provider's books and arrived nowhere.
        var workspace = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
                            .FirstOrDefaultAsync(w => w.Id == credit.WorkspaceId, ct)
                        ?? throw new InvalidOperationException(
                            $"There is no workspace with id {credit.WorkspaceId}, so nothing can be " +
                            "credited to it.");

        var (applied, balanceMinor) = await WriteAsync(new Movement(
            credit.Id, credit.WorkspaceId, credit.AmountMinor, note, credit.ByUserId, LedgerKind.Credit), ct);

        var startedApps = 0;
        var startedDatabases = 0;
        var stillSuspended = workspace.IsSuspended;
        var failures = new List<string>();

        // Zero is not a balance — the gate refuses to start anything on one — so a top-up that only
        // reaches zero must not bring the apps back to be charged for an hour nobody can pay for.
        //
        // Asked without also testing the suspension's reason, on purpose. ResumeAsync acts on
        // SuspensionReason.NoBalance and on nothing else and returns quietly otherwise; repeating
        // that test here would be a second copy of the rule, free to disagree with the one that
        // actually decides. Paying a bill does not undo an operator's decision, and it is ResumeAsync
        // that says so.
        if (balanceMinor > 0)
        {
            try
            {
                var resumed = await suspension.ResumeAsync(credit.WorkspaceId, ct);
                stillSuspended = resumed.WorkspaceSuspended;
                startedApps = resumed.AppsStarted;
                startedDatabases = resumed.DatabasesStarted;
                failures.AddRange(resumed.Failures);
            }
            // Caught, named and kept — never rethrown. The money is already committed, and an
            // exception escaping here would surface to the administrator as a failed credit that
            // nevertheless took the payment.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failures.Add(
                    $"Workspace \"{workspace.Name}\" was credited, but lifting its suspension failed: " +
                    $"{ex.Message}. The money is on the account; crediting again with the same id " +
                    "writes nothing and tries the resume once more.");
                logger.LogError(ex,
                    "Resuming workspace {Workspace} after a credit failed; the credit itself was kept.",
                    credit.WorkspaceId);
            }
        }

        return new CreditResult(
            balanceMinor, applied, stillSuspended, startedApps, startedDatabases, failures);
    }

    /// <summary>
    /// What each thing this workspace held cost it between two instants, and how long each was up.
    ///
    /// <para>
    /// The window is <b>half-open</b> — <c>[from, to)</c> — so two consecutive statements never both
    /// claim the hour on the boundary. An inclusive end would put that hour on this month's bill and
    /// on next month's, and the two together would no longer add up to the balance.
    /// </para>
    ///
    /// <para>
    /// This is a <c>GROUP BY</c> and not a second metering system, which is the whole reason every
    /// ledger line carries the resource it charged, a copy of that resource's name, and whether it
    /// was running. Nothing is recomputed from the apps table, so an app deleted last week still
    /// reads as a name on the bill it was charged on.
    /// </para>
    ///
    /// <para>
    /// Only machine-written charges and the plan minimum are resource costs. Credits and append-only
    /// adjustments are money movements with their own dated tables on the bill; folding either into
    /// a resource row would hide its note and make a correction look like a cheaper workload.
    /// </para>
    ///
    /// <para>
    /// A credit is left out because it is not the cost of anything the customer ran. Folded into this
    /// table it would make the app it happened to land beside look cheaper than it was, and three
    /// separate top-ups would become one figure — in which a top-up applied twice is invisible. The
    /// screen lists them instead, dated, with the note and the person, which is what a record of
    /// payments has to be. What that leaves behind is checkable and is meant to be checked: this
    /// breakdown plus credits and signed adjustments in the same window is exactly the balance's
    /// movement across it.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ResourceCost>> BreakdownAsync(
        Guid workspaceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var grouped = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId
                        && l.BillingHour >= from
                        && l.BillingHour < to
                        && (l.Kind == LedgerKind.Charge || l.Kind == LedgerKind.PlanMinimumTopUp))
            .GroupBy(l => new { l.ResourceType, l.ResourceId, l.ResourceName })
            .Select(g => new
            {
                g.Key.ResourceType,
                g.Key.ResourceId,
                g.Key.ResourceName,
                // The hours the LINE says it paid for, not the number of lines. Hours is a column
                // precisely because a backfilled or coalesced line can pay for more than one, and
                // counting rows would tell a customer a three-hour charge lasted an hour while
                // charging them for three.
                RunningHours = g.Sum(l => l.RunState == BilledRunState.Running ? l.Hours : 0),
                StoppedHours = g.Sum(l => l.RunState == BilledRunState.Stopped ? l.Hours : 0),
                TotalMinor = g.Sum(l => l.AmountMinor)
            })
            .ToListAsync(ct);

        // Most expensive first, because "what is costing me the most" is the question a bill is
        // opened to answer. Ordered here rather than in SQL so the tie-break is the same string
        // comparison on every provider and collation — a bill whose rows move about between two
        // readings of the same period looks like the figures moved too.
        return grouped
            .Select(g => new ResourceCost(
                g.ResourceType, g.ResourceId, g.ResourceName,
                g.RunningHours, g.StoppedHours, g.TotalMinor))
            .OrderBy(c => c.TotalMinor)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Where a resource currently sits — which project and which environment — or nothing when that
    /// cannot be answered. Not persisted anywhere: this is resolved fresh, every call, from whichever
    /// App/Volume/ManagedService rows still exist.
    /// </summary>
    private readonly record struct ResourcePlacement(
        Guid? ProjectId, string? ProjectName, Guid? EnvironmentId, string? EnvironmentName)
    {
        public static readonly ResourcePlacement Unassigned = new(null, null, null, null);
    }

    /// <summary>
    /// <see cref="ResourceCost.Id"/> to where that resource is placed <b>today</b>, for every id in
    /// <paramref name="costs"/> that resolves to one.
    ///
    /// <para>
    /// Keyed on the id alone, not on <c>(ResourceType, Id)</c> the way the ledger's own retry index
    /// is. That pair exists on the ledger because <see cref="BilledResourceType.ServiceVolume"/>
    /// deliberately reuses a <see cref="Harbora.Domain.Services.ManagedService"/>'s own id (see that
    /// enum member's remarks) — and the two resolve to the identical placement anyway, since it is the
    /// same row. Every other id here is a different table's own <c>Guid</c>, so a collision across
    /// types is not a real possibility this reporting feature needs to guard against.
    /// </para>
    ///
    /// <para>
    /// An id with no entry is not an error: it is a plan-minimum line (no resource at all), a mail
    /// domain or mailbox (workspace-level, no project), or a resource whose row has since been
    /// deleted. <see cref="BreakdownByProjectAsync"/> reads a missing entry as "Unassigned" rather
    /// than throwing.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, ResourcePlacement>> ResolvePlacementAsync(
        Guid workspaceId, IReadOnlyList<ResourceCost> costs, CancellationToken ct)
    {
        var appIds = costs.Where(c => c.Type == BilledResourceType.App && c.Id is not null)
            .Select(c => c.Id!.Value).Distinct().ToList();
        var volumeIds = costs.Where(c => c.Type == BilledResourceType.Volume && c.Id is not null)
            .Select(c => c.Id!.Value).Distinct().ToList();
        var serviceIds = costs.Where(c => c.Type is BilledResourceType.Service or BilledResourceType.ServiceVolume
                                           && c.Id is not null)
            .Select(c => c.Id!.Value).Distinct().ToList();

        var environmentIdByResource = new Dictionary<Guid, Guid>();

        if (appIds.Count > 0)
            foreach (var a in await db.Apps.IgnoreQueryFilters().AsNoTracking()
                         .Where(a => a.WorkspaceId == workspaceId && appIds.Contains(a.Id))
                         .Select(a => new { a.Id, a.EnvironmentId })
                         .ToListAsync(ct))
                environmentIdByResource[a.Id] = a.EnvironmentId;

        if (volumeIds.Count > 0)
            // A volume has no workspace or environment of its own — both live on the app it hangs off.
            foreach (var v in await db.Volumes.IgnoreQueryFilters().AsNoTracking()
                         .Where(v => volumeIds.Contains(v.Id) && v.App!.WorkspaceId == workspaceId)
                         .Select(v => new { v.Id, EnvironmentId = v.App!.EnvironmentId })
                         .ToListAsync(ct))
                environmentIdByResource[v.Id] = v.EnvironmentId;

        if (serviceIds.Count > 0)
            foreach (var s in await db.ManagedServices.IgnoreQueryFilters().AsNoTracking()
                         .Where(s => s.WorkspaceId == workspaceId && serviceIds.Contains(s.Id))
                         .Select(s => new { s.Id, s.EnvironmentId })
                         .ToListAsync(ct))
                environmentIdByResource[s.Id] = s.EnvironmentId;

        if (environmentIdByResource.Count == 0) return new Dictionary<Guid, ResourcePlacement>();

        var environmentIds = environmentIdByResource.Values.Distinct().ToList();
        var environments = await db.Environments.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && environmentIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Name, e.ProjectId, ProjectName = e.Project!.Name })
            .ToListAsync(ct);
        var environmentById = environments.ToDictionary(e => e.Id);

        var placement = new Dictionary<Guid, ResourcePlacement>();
        foreach (var (resourceId, environmentId) in environmentIdByResource)
        {
            // The environment itself missing would mean an App/ManagedService points at a row that no
            // longer exists — App.EnvironmentId is a required foreign key (see App's own remarks), so
            // this should not happen. Resolved to Unassigned rather than thrown on: a report answering
            // a data-integrity question it was not asked is worse than one that names the gap as
            // "could not be placed" and keeps running.
            if (environmentById.TryGetValue(environmentId, out var env))
                placement[resourceId] = new ResourcePlacement(env.ProjectId, env.ProjectName, env.Id, env.Name);
        }
        return placement;
    }

    /// <summary>
    /// The exact rows <see cref="BreakdownAsync"/> already returns, partitioned by the project and
    /// environment each resource is placed in <b>today</b> — never re-summed, so a group's
    /// <see cref="ProjectCostGroup.TotalMinor"/> can never drift from the workspace total: it is the
    /// same numbers, sorted into buckets. <c>groups.Sum(g =&gt; g.TotalMinor)</c> always equals
    /// <c>(await BreakdownAsync(...)).Sum(c =&gt; c.TotalMinor)</c>.
    ///
    /// <para>
    /// <b>Attribution follows where a resource sits now, not where it sat when the hour was charged.</b>
    /// A <see cref="BillingLedgerEntry"/> carries a resource type, an id and a copy of the resource's
    /// name — deliberately never a project or an environment, the same reason it never joins back to
    /// the resource for its name (see <see cref="BreakdownAsync"/>'s own remarks: the row might not
    /// exist by the time anybody reads the bill). So there is no record of which project an hour
    /// belonged to at the moment it was billed, only of which project the resource belongs to right
    /// now. A workload moved from staging to production carries its whole history into production the
    /// next time this is read — the view says so beside the section, the same way the forecast card
    /// says its own number is an estimate. Recording project and environment on every ledger line at
    /// write time would answer the other question, but that is a schema change to
    /// <see cref="BillingLedgerEntry"/> this reporting feature does not make.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing is dropped.</b> Three kinds of spend cannot be attributed to a project: the
    /// plan-minimum top-up (no resource at all — <see cref="ResourceCost.Id"/> is null), a mail domain
    /// or mailbox (workspace-level, no project of their own), and a resource whose row has since been
    /// deleted. All three land in exactly one <see cref="ProjectCostGroup"/> with a null
    /// <see cref="ProjectCostGroup.ProjectId"/> — visible, named "Unassigned" by the view, and counted
    /// in the sum above — rather than quietly missing from a report whose whole value is adding up.
    /// </para>
    ///
    /// <para>
    /// <paramref name="includeForecast"/> gates the same, more expensive question
    /// <see cref="ForecastAsync(Guid,DateTimeOffset,DateTimeOffset,CancellationToken)"/> already gates
    /// for the whole workspace — a closed period or a suspended workspace has nothing to project — so
    /// the caller passes exactly the condition it already computed rather than this method repeating
    /// it. Each group's forecast is the identical <see cref="BurnRate"/>/<see cref="CostForecast"/>
    /// arithmetic, restricted to that group's own resources; never a second formula for "what will
    /// this cost".
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ProjectCostGroup>> BreakdownByProjectAsync(
        Guid workspaceId, DateTimeOffset from, DateTimeOffset to, bool includeForecast, CancellationToken ct)
    {
        var costs = await BreakdownAsync(workspaceId, from, to, ct);
        if (costs.Count == 0) return [];

        var placement = await ResolvePlacementAsync(workspaceId, costs, ct);
        var resolvedIds = placement.Keys.ToHashSet();

        ResourcePlacement KeyOf(ResourceCost c) =>
            c.Id is { } id && placement.TryGetValue(id, out var p) ? p : ResourcePlacement.Unassigned;

        var environmentBuckets = costs
            .GroupBy(KeyOf)
            .Select(g => (Key: g.Key, Costs: (IReadOnlyList<ResourceCost>)g
                .OrderBy(c => c.TotalMinor).ThenBy(c => c.Name, StringComparer.Ordinal).ToList()))
            .ToList();

        var projectGroups = new List<ProjectCostGroup>();
        foreach (var projectBucket in environmentBuckets.GroupBy(e => (e.Key.ProjectId, e.Key.ProjectName)))
        {
            var isUnassignedProject = projectBucket.Key.ProjectId is null;
            var environments = new List<EnvironmentCostGroup>();

            foreach (var env in projectBucket)
            {
                CostForecast? environmentForecast = null;
                if (includeForecast)
                {
                    environmentForecast = isUnassignedProject
                        ? await ForecastAsync(workspaceId, from, to, UnassignedResourceFilter(resolvedIds), ct)
                        : await ForecastAsync(workspaceId, from, to,
                            ResourceIdFilter(env.Costs.Where(c => c.Id is not null).Select(c => c.Id!.Value)), ct);
                }
                environments.Add(new EnvironmentCostGroup(env.Key.EnvironmentId, env.Key.EnvironmentName, env.Costs, environmentForecast));
            }

            CostForecast? projectForecast = null;
            if (includeForecast)
            {
                projectForecast = isUnassignedProject
                    ? await ForecastAsync(workspaceId, from, to, UnassignedResourceFilter(resolvedIds), ct)
                    : await ForecastAsync(workspaceId, from, to,
                        ResourceIdFilter(environments.SelectMany(e => e.Costs)
                            .Where(c => c.Id is not null).Select(c => c.Id!.Value)), ct);
            }

            projectGroups.Add(new ProjectCostGroup(
                projectBucket.Key.ProjectId, projectBucket.Key.ProjectName, environments, projectForecast));
        }

        // Unassigned last, however large — "here is the money nothing else accounts for" is a
        // footnote a customer reads after the projects they recognise, not the headline of their bill.
        return projectGroups
            .OrderBy(p => p.IsUnassigned)
            .ThenByDescending(p => p.TotalMinor)
            .ThenBy(p => p.ProjectName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A filter for <see cref="ForecastAsync(Guid,DateTimeOffset,DateTimeOffset,Func{IQueryable{BillingLedgerEntry},IQueryable{BillingLedgerEntry}},CancellationToken)"/>
    /// that keeps only lines charged against one of <paramref name="resourceIds"/>.</summary>
    private static Func<IQueryable<BillingLedgerEntry>, IQueryable<BillingLedgerEntry>> ResourceIdFilter(
        IEnumerable<Guid> resourceIds)
    {
        var set = resourceIds.ToHashSet();
        return q => q.Where(l => l.ResourceId != null && set.Contains(l.ResourceId.Value));
    }

    /// <summary>
    /// The Unassigned group's own filter: a line with no resource at all (the plan minimum), or one
    /// charged against a resource that never resolved to a project (a mail domain, a mailbox, or a
    /// deleted app/database) — <paramref name="resolvedResourceIds"/> is every id that DID resolve,
    /// so this is exactly its complement.
    /// </summary>
    private static Func<IQueryable<BillingLedgerEntry>, IQueryable<BillingLedgerEntry>> UnassignedResourceFilter(
        IEnumerable<Guid> resolvedResourceIds)
    {
        var set = resolvedResourceIds.ToHashSet();
        return q => q.Where(l => l.ResourceId == null || !set.Contains(l.ResourceId.Value));
    }

    /// <summary>
    /// The least number of distinct hours this workspace has to have been billed for before
    /// <see cref="ForecastAsync"/> will project anything.
    ///
    /// <para>
    /// One full day. A single hour is not a pattern — it can be a resource created ten minutes ago,
    /// a deploy still finishing, or the one hour of a day's backfill that happened to be priced
    /// differently — and extrapolating it across a month is exactly the shape of confident wrong
    /// number this feature exists to refuse. A day is the smallest window that could plausibly show a
    /// workspace's own rhythm rather than a single moment of it, and it is the unit the low-balance
    /// warning already thinks in — <see cref="Wallet.LowBalanceHours"/> defaults to the same 24.
    /// </para>
    /// </summary>
    public const int MinimumHistoryHours = 24;

    /// <summary>
    /// What the current billing period is heading towards, and when the balance runs out at that
    /// rate — both derived from hours the real hourly pass already priced and wrote, never
    /// recomputed from rates here. See <see cref="CostForecast"/> for what each figure means and why
    /// it is a claim about the future rather than a record of the past.
    ///
    /// <para>
    /// <b>The burn rate is the most recently completed billing hour, not a month-to-date average.</b>
    /// An average blends whatever a workspace was doing across the whole period, so a workspace that
    /// ran heavily for three weeks and was stopped yesterday would still show a high average today —
    /// which is precisely the "forecast a stopped workload as though it were still burning" failure
    /// this feature is required not to make. The one hour is what <see cref="BillingTick"/> priced
    /// against whatever was actually running through it, so a suspension or a stopped app is reflected
    /// the next time the tick runs, without this method needing to know suspension exists at all.
    /// </para>
    ///
    /// <para>
    /// <b>That hour is chosen by the clock, not by which hour last has a row.</b> An hour nothing was
    /// chargeable in writes no ledger line at all — <see cref="BillingTick"/> still evaluates the
    /// workspace, it simply has nothing to bill — so reaching backward for the last hour that DID
    /// leave a row would read a workspace that stopped everything three days ago as though it were
    /// still burning at Tuesday's rate. Once a workspace has cleared
    /// <see cref="MinimumHistoryHours"/> of real charges, an hour with no row for it is trusted as a
    /// genuine zero rather than treated as missing data.
    /// </para>
    ///
    /// <para>
    /// <b>Every read here ignores the tenant filter</b>, for the reason every other read in this class
    /// does: a provider administrator viewing a customer's forecast, and a background job with no
    /// session at all, both need to see past their own scope. <paramref name="workspaceId"/> is the
    /// only thing keeping two customers' figures apart.
    /// </para>
    /// </summary>
    public Task<CostForecast> ForecastAsync(
        Guid workspaceId, DateTimeOffset periodFrom, DateTimeOffset periodTo, CancellationToken ct) =>
        // Every resource the workspace has, which is what "no filter" means — see the private
        // overload below, the one place this arithmetic actually lives.
        ForecastAsync(workspaceId, periodFrom, periodTo, static q => q, ct);

    /// <summary>
    /// Same computation as the public overload above, restricted to whichever ledger lines
    /// <paramref name="resourceFilter"/> lets through. This is the whole of how
    /// <see cref="BreakdownByProjectAsync"/> gives each project and environment group its own burn
    /// rate and projection: it calls this, once per group, with a filter naming that group's own
    /// resources — never a second copy of the arithmetic above it. The public overload is exactly
    /// this method asked with a filter that keeps everything, which is why it is a one-line wrapper
    /// rather than its own implementation.
    /// </summary>
    private async Task<CostForecast> ForecastAsync(
        Guid workspaceId, DateTimeOffset periodFrom, DateTimeOffset periodTo,
        Func<IQueryable<BillingLedgerEntry>, IQueryable<BillingLedgerEntry>> resourceFilter,
        CancellationToken ct)
    {
        var now = clock.UtcNow;

        // Already a fact, not a forecast, so it is read and returned even when there is not enough
        // history to project the rest of the period.
        var spentSoFarMinor = -(await resourceFilter(db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId
                        && l.BillingHour >= periodFrom && l.BillingHour < periodTo
                        && (l.Kind == LedgerKind.Charge || l.Kind == LedgerKind.PlanMinimumTopUp)))
            .SumAsync(l => (long?)l.AmountMinor, ct) ?? 0);

        // Every hour this workspace has ever actually been charged for, not only this period's — a
        // wallet that has run for months has earned the platform's confidence on the first day of a
        // new one, and gating on the period alone would make every workspace on the install look
        // brand new at midnight on the 1st. Scoped to the group's own resources for the same reason:
        // a project created yesterday has not earned that confidence just because the workspace it
        // lives in has.
        var chargedHours = await resourceFilter(db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId
                        && (l.Kind == LedgerKind.Charge || l.Kind == LedgerKind.PlanMinimumTopUp)))
            .Select(l => l.BillingHour)
            .Distinct()
            .ToListAsync(ct);

        if (chargedHours.Count < MinimumHistoryHours)
            return new CostForecast(
                HasEnoughHistory: false, chargedHours.Count, MinimumHistoryHours,
                spentSoFarMinor, BurnRateHourlyMinor: 0,
                ProjectedPeriodTotalMinor: null, RunwayHours: null, RunwayDate: null);

        // The hour immediately before the one in progress — the newest hour BillingTick could
        // possibly have priced by now, whether or not anything of it ended up on the ledger. Mirrors
        // BillingTick.TopOfHour/HasEnded exactly, because an hour named differently by the two would
        // let this method ask about an hour the tick has not reached yet and call the silence "free".
        var lastEndedHour = TopOfHour(now).AddHours(-1);

        // What that hour actually cost, read the same way BillingTick itself reads "what did this
        // hour come to" when it decides whether to warn — see ReviewLowBalanceAsync's hourCostMinor.
        // No rows for the hour sums to null, and null becomes zero here rather than "unknown": once
        // MinimumHistoryHours has been cleared, a silent hour is trusted as a real one that cost
        // nothing, not as data this method failed to find.
        var burnRateMinor = Math.Max(0L, -(await resourceFilter(db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId && l.BillingHour == lastEndedHour
                        && (l.Kind == LedgerKind.Charge || l.Kind == LedgerKind.PlanMinimumTopUp)))
            .SumAsync(l => (long?)l.AmountMinor, ct) ?? 0));

        // Whole hours between now and the end of the period. The current, still-running hour is
        // deliberately not part of "spent so far" — BillingTick only charges an hour once it has
        // ended — so it belongs on the projected side, not double-counted on both.
        var hoursRemaining = Math.Max(0L, (long)Math.Floor((periodTo - now).TotalHours));

        // Guarded the way MonthlyEstimate guards its own multiplication: this project compiles
        // unchecked, so an overflow here would not throw — it would wrap to a large negative and
        // present a runaway workspace's bill as a refund, which is worse than refusing a figure.
        long? projectedTotalMinor = spentSoFarMinor;
        if (burnRateMinor > 0 && hoursRemaining > 0)
        {
            projectedTotalMinor = hoursRemaining > (long.MaxValue - spentSoFarMinor) / burnRateMinor
                ? null
                : spentSoFarMinor + burnRateMinor * hoursRemaining;
        }

        // The whole wallet's balance, unfiltered, even when this call is for one group. There is one
        // pool of money, not one per project — a group's RunwayHours/RunwayDate answer "how long would
        // the whole balance last at only this group's rate", which stays a true statement about a
        // shared balance; it is not a segmented sub-balance nothing here invented.
        var balanceMinor = await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.WorkspaceId == workspaceId)
            .Select(w => (long?)w.BalanceMinor).FirstOrDefaultAsync(ct) ?? 0;

        return new CostForecast(
            HasEnoughHistory: true, chargedHours.Count, MinimumHistoryHours,
            spentSoFarMinor, burnRateMinor, projectedTotalMinor,
            BurnRate.RunwayHours(balanceMinor, burnRateMinor),
            BurnRate.RunwayDate(now, balanceMinor, burnRateMinor));
    }

    /// <summary>
    /// Writes a compensating line without changing the old one. Positive returns money; negative
    /// removes an erroneous credit. The resulting balance drives the same suspend/resume authority
    /// as an hourly charge or a normal credit.
    /// </summary>
    public async Task<AdjustmentResult> AdjustAsync(AdjustmentRequest adjustment, CancellationToken ct)
    {
        if (adjustment.Id == Guid.Empty)
            throw new ArgumentException("An adjustment needs an id.", nameof(adjustment));
        if (adjustment.AmountMinor == 0)
            throw new ArgumentOutOfRangeException(nameof(adjustment), "An adjustment must move the balance.");
        var note = adjustment.Note?.Trim();
        if (string.IsNullOrWhiteSpace(note))
            throw new ArgumentException("An adjustment needs a note explaining the correction.", nameof(adjustment));
        if (!await db.Workspaces.IgnoreQueryFilters().AnyAsync(w => w.Id == adjustment.WorkspaceId, ct))
            throw new InvalidOperationException($"There is no workspace with id {adjustment.WorkspaceId}.");

        var (applied, balance) = await WriteAsync(new Movement(
            adjustment.Id, adjustment.WorkspaceId, adjustment.AmountMinor, note,
            adjustment.ByUserId, LedgerKind.Adjustment), ct);

        BillingSuspensionResult outcome;
        try
        {
            outcome = balance > 0
                ? await suspension.ResumeAsync(adjustment.WorkspaceId, ct)
                : await suspension.SuspendAsync(adjustment.WorkspaceId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex,
                "Applying the workload state after adjustment {Adjustment} failed; the money movement was kept.",
                adjustment.Id);
            return new AdjustmentResult(
                balance, applied, balance <= 0, 0, 0, 0, 0,
                [$"The adjustment was applied, but updating the workspace's suspension failed: {ex.Message}"]);
        }

        return new AdjustmentResult(
            balance, applied, outcome.WorkspaceSuspended,
            outcome.AppsStarted, outcome.DatabasesStarted,
            outcome.AppsStopped, outcome.DatabasesStopped, outcome.Failures);
    }

    /// <summary>Compares the cached wallet with the append-only source of truth without mutating either.</summary>
    public async Task<WalletReconciliation> ReconcileAsync(Guid workspaceId, CancellationToken ct)
    {
        var wallet = await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.WorkspaceId == workspaceId)
            .Select(w => (long?)w.BalanceMinor)
            .FirstOrDefaultAsync(ct);
        var ledger = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId)
            .SumAsync(l => (long?)l.AmountMinor, ct) ?? 0;
        var cached = wallet ?? 0;
        return new WalletReconciliation(wallet is not null, cached, ledger, cached - ledger);
    }

    /// <summary>
    /// Writes the line and moves the balance, once. False means this credit was already on the
    /// ledger and nothing new was written; the balance returned beside it is the real one either way.
    /// </summary>
    private async Task<(bool Applied, long BalanceMinor)> WriteAsync(
        Movement movement, CancellationToken ct)
    {
        // The ordinary repeat is not a race — it is one person's decision submitted twice — so it is
        // answered by a read. The primary key below is what settles the race the read cannot win.
        if (await AlreadyAppliedAsync(movement, ct))
            return (false, await BalanceAsync(movement.WorkspaceId, ct));

        var wallet = await db.Wallets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.WorkspaceId == movement.WorkspaceId, ct);

        if (wallet is null)
        {
            // The tick opens a wallet the first time it charges somebody, so an account credited
            // before it has ever been billed has no row yet. Refusing here would mean a customer
            // cannot pay in advance.
            //
            // The currency comes from the setting rather than from the entity's default, and this is
            // one of exactly two places a wallet is ever opened. A provider selling in something
            // other than the shipped code has to be able to say so once; a column defaulted in the
            // domain and settable nowhere is a column that lies on every install but one.
            wallet = new Wallet
            {
                WorkspaceId = movement.WorkspaceId,
                Currency = options.Value.CurrencyOrDefault
            };
            db.Wallets.Add(wallet);
        }

        db.BillingLedger.Add(new BillingLedgerEntry
        {
            // The caller's id, not a fresh one. This is the idempotency key — see CreditRequest.Id.
            Id = movement.Id,
            WorkspaceId = movement.WorkspaceId,
            // Filed under the hour it was made in. BillingHour is what every statement window filters
            // on, so a credit left at the default instant would sit in year one, appear on no bill
            // the customer will ever open, and leave the ledger and the balance disagreeing.
            BillingHour = TopOfHour(clock.UtcNow),
            Kind = movement.Kind,
            AmountMinor = movement.AmountMinor,
            ResourceType = BilledResourceType.None,
            ResourceId = null,
            ResourceName = movement.Kind == LedgerKind.Adjustment ? "Balance adjustment" : string.Empty,
            RunState = BilledRunState.NotApplicable,
            // Neither an hour nor a rate. The entity defaults Hours to 1 because nearly every line is
            // one hour of one thing; a manual money movement is no hours of nothing, and leaving the default would
            // put an hour on the bill that nobody spent.
            RatePerHourMinor = 0,
            Hours = 0,
            Description = movement.Note,
            // A person's money movement has a person on it.
            CreatedByUserId = movement.ByUserId,
        });

        Apply(wallet, movement.AmountMinor);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return (true, wallet.BalanceMinor);
            }
            // The hourly pass moved the balance between the read and the write. The credit is still
            // correct, so the balance is read again and the same movement re-applied on top of
            // theirs. DbUpdateConcurrencyException derives from DbUpdateException, so this clause
            // must come first or the unique-violation catch below would swallow it.
            catch (DbUpdateConcurrencyException)
                when (attempt < WalletWriteAttempts && db.Entry(wallet).State != EntityState.Added)
            {
                var entry = db.Entry(wallet);
                await entry.ReloadAsync(ct);

                // Reload detaches an entity whose row has gone. Re-applying to a detached wallet
                // would change a value nothing is tracking and then report a clean save that moved
                // no money at all.
                if (entry.State == EntityState.Detached) throw;

                Apply(wallet, movement.AmountMinor);
            }
            catch (DbUpdateException e)
                when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Everything in hand is discarded first, the wallet increment included: the write was
                // refused as a whole, so nothing here happened.
                db.ChangeTracker.Clear();

                // 23505 says "a unique index refused this", not which one, and this write touches two
                // — the ledger's primary key and Wallets.WorkspaceId, which two first-ever credits
                // arriving together would both try to insert. Reading the second as "already applied"
                // would drop a credit nobody has made while telling the administrator it landed, so
                // the question the code actually needs answering is asked directly. A constraint
                // violation leaves the connection healthy, so it can be asked.
                if (await AlreadyAppliedAsync(movement, ct))
                    return (false, await BalanceAsync(movement.WorkspaceId, ct));

                throw;
            }
        }
    }

    /// <summary>
    /// Whether this exact credit is already on the ledger.
    ///
    /// <para>
    /// A row under this id that describes a <i>different</i> credit throws rather than reporting
    /// "already applied", and that distinction is the expensive one. Answering quietly would tell an
    /// administrator the money had reached this customer when it reached another one — and the
    /// customer it did reach is not going to raise it.
    /// </para>
    ///
    /// <para>
    /// The note is compared alongside the kind, the workspace and the amount — not because two
    /// credits with different notes move different money, but because a note edited after its id was
    /// already used (a back button, a correction, a resubmit) is a real second decision about what
    /// the line should say, and it is the one of the four a silent "already applied" would make
    /// disappear without moving a cent. Get the workspace or the amount wrong and money sits in the
    /// wrong place, loudly. Get the note wrong and the ledger keeps telling the old story for as long
    /// as anyone reads it, with nothing on screen to say a second story was ever offered. Refusing
    /// costs an administrator a reload of the confirmation page for a fresh id; keeping the first
    /// note silently costs nobody anything they would ever think to complain about — which is exactly
    /// the failure shape this class exists to refuse rather than absorb.
    /// </para>
    /// </summary>
    private async Task<bool> AlreadyAppliedAsync(Movement movement, CancellationToken ct)
    {
        var existing = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == movement.Id)
            .Select(l => new { l.Kind, l.WorkspaceId, l.AmountMinor, l.Description })
            .FirstOrDefaultAsync(ct);

        if (existing is null) return false;

        if (existing.Kind == movement.Kind
            && existing.WorkspaceId == movement.WorkspaceId
            && existing.AmountMinor == movement.AmountMinor
            && existing.Description == movement.Note)
            return true;

        throw new InvalidOperationException(
            $"Ledger line {movement.Id} already exists and is not this movement: it is a " +
            $"{existing.Kind} of {existing.AmountMinor} on workspace {existing.WorkspaceId} noted " +
            $"\"{existing.Description}\", and this asks for a {movement.Kind} of {movement.AmountMinor} on " +
            $"workspace {movement.WorkspaceId} noted \"{movement.Note}\". Nothing was written — an id reused " +
            "for a different movement is a mistake, and reporting it as already applied would say " +
            "the money arrived somewhere it did not, or say what it was for when it was not that.");
    }

    private sealed record Movement(
        Guid Id,
        Guid WorkspaceId,
        long AmountMinor,
        string Note,
        Guid ByUserId,
        LedgerKind Kind);

    /// <summary>
    /// The balance as the store has it.
    ///
    /// <para>
    /// Projected to a scalar rather than read as an entity, which is what makes it fresh: a query
    /// returning an entity type is answered from the change tracker when this context is already
    /// holding that row, so it would hand back the balance as it was before somebody else's write.
    /// A workspace with no wallet has a balance of nothing rather than no answer, for the reason
    /// <see cref="BillingGate"/> gives: whether the hourly pass has happened to run yet is not
    /// something anybody should be able to get a different answer out of.
    /// </para>
    /// </summary>
    private async Task<long> BalanceAsync(Guid workspaceId, CancellationToken ct) =>
        await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.WorkspaceId == workspaceId)
            .Select(w => (long?)w.BalanceMinor)
            .FirstOrDefaultAsync(ct) ?? 0;

    /// <summary>
    /// Moves the balance and rotates the stamp.
    ///
    /// <para>
    /// The rotation is the load-bearing half, for the reason <see cref="BillingTick"/> spells out: EF
    /// checks a concurrency token by comparing what it read against the row it is updating, so a
    /// token nothing ever changes always matches and two writers both succeed. That is
    /// last-write-wins on a balance, and it looks exactly like a working lock from the outside.
    /// </para>
    /// </summary>
    private static void Apply(Wallet wallet, long moved)
    {
        wallet.BalanceMinor += moved;
        wallet.ConcurrencyStamp = Guid.CreateVersion7();
    }

    /// <summary>The top of the UTC hour the given instant falls in — the same normalisation the tick does.</summary>
    private static DateTimeOffset TopOfHour(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}
