using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Harbora.Infrastructure.Billing;

/// <summary>
/// What one pass did. Returned so a caller can assert on it rather than read a log.
///
/// <para>
/// <see cref="Failures"/> is not only exceptions. It carries everything that made the pass
/// incomplete — a size nobody priced, a volume nobody measured, hours dropped at the backfill bound
/// — because those are invisible otherwise: nothing throws, the ledger still adds up, and the run
/// reports success having quietly hosted somebody for nothing. Each distinct cause appears once per
/// pass, so a forgotten price on a popular tier is one legible line rather than twenty thousand.
/// </para>
/// </summary>
public sealed record BillingTickResult(
    int WorkspacesCharged,
    int LinesWritten,
    int HoursBackfilled,
    IReadOnlyList<string> Failures);

/// <summary>
/// Charges every workspace for one hour that has already ended.
///
/// <para>
/// <b>Runs unscoped, deliberately.</b> The EF tenancy filters are driven by the request's workspace,
/// so work with no session behind it sees an empty database — it would charge nobody and report
/// success. Every read here is <c>IgnoreQueryFilters</c>, which is the same call the retention
/// sweeper makes for the same reason: relying on the ambient scope happening to be unscoped is a bet,
/// and this codebase has already lost it four times. A test hands this class a context scoped to
/// <see cref="Guid.Empty"/> — the deny-by-default scope an unauthenticated request resolves to — and
/// counts the workspaces charged, because that failure is invisible in a log.
/// </para>
///
/// <para>
/// <b>Two things are never turned into money.</b> A rate nobody has set is null, not zero, and a
/// volume nobody has measured has no size, not a size of nothing. Neither gets a ledger line: a line
/// of zero reads on the bill exactly like a deliberately free resource, and — worse — it takes that
/// resource's slot in the unique index for the hour, so the corrected pass after somebody sets the
/// price collides with it and is discarded as "already charged". Writing nothing leaves the slot
/// open, and the hour becomes payable the moment the gap is filled.
/// </para>
///
/// <para>
/// <b>An hour that could not be priced in full pays no plan minimum.</b> The floor is the difference
/// between the hour's cost and the plan's minimum, so it cannot be worked out while part of the hour
/// is unknown. Charging it anyway would let the corrected pass add the missing lines on top of a
/// top-up that already covered them — two passes, each looking right, adding up to an overcharge.
/// Under-charging visibly beats over-charging invisibly.
/// </para>
///
/// <para>
/// <b>The warning before the lights go out is sent once, not once an hour.</b> This pass is the only
/// thing that knows what an hour actually costs a workspace, so it is where the balance is measured
/// against the customer's chosen number of hours — and the de-duplication has to outlive the pass,
/// because a day of backfill opens a fresh scope per hour and would otherwise send twenty-four copies
/// of one piece of news. What outlives it is <see cref="Wallet.LowBalanceWarnedAtBalanceMinor"/>.
/// </para>
/// </summary>
public sealed class BillingTick(
    IServiceScopeFactory scopeFactory,
    IOptions<BillingOptions> options,
    ISystemClock clock,
    ILogger<BillingTick> logger)
{
    /// <summary>
    /// How many times the wallet write is re-read and re-applied before the workspace's hour is
    /// recorded as failed. Small on purpose: a conflict means somebody else moved the balance, which
    /// is a person crediting an account, not a hot loop.
    /// </summary>
    private const int WalletWriteAttempts = 3;

    /// <summary>
    /// One hour, every workspace. Idempotent twice over: the lines already written for the hour are
    /// read first and skipped, and the unique index on
    /// (WorkspaceId, ResourceType, ResourceId, BillingHour) settles the race the read cannot win. A
    /// retry from the durable queue is therefore harmless.
    /// </summary>
    public async Task<BillingTickResult> ChargeHourAsync(DateTimeOffset hour, CancellationToken ct)
    {
        var pass = new Pass();
        if (Off(hour)) return pass.Result();

        await ChargeHourAsync(hour, pass, ct);
        return pass.Result();
    }

    /// <summary>
    /// Pay for every hour between <paramref name="lastChargedHour"/> and now, oldest first, up to
    /// <c>Billing:MaxBackfillHours</c>. Reaching the bound is a warning naming the hours dropped.
    ///
    /// <para>
    /// Oldest first, and the bound drops the newest: an hour dropped from the far end has not been
    /// billed yet and the next catch-up reaches it, whereas dropping the oldest loses it for good
    /// once it falls out of the window.
    /// </para>
    /// </summary>
    public async Task<BillingTickResult> CatchUpAsync(DateTimeOffset lastChargedHour, CancellationToken ct)
    {
        var pass = new Pass();
        if (Off(lastChargedHour)) return pass.Result();

        var bound = Math.Max(0, options.Value.MaxBackfillHours);
        var next = TopOfHour(lastChargedHour).AddHours(1);

        // How many ended hours are owed, worked out rather than counted. `lastChargedHour` is a
        // stored value, and one corrupted to a date in 1970 would otherwise spin a loop once for
        // every hour since — before the pass had charged anybody.
        var owed = (int)Math.Clamp(Math.Floor((clock.UtcNow - next).TotalHours), 0, int.MaxValue);

        for (var h = next; HasEnded(h) && pass.HoursBackfilled < bound; h = h.AddHours(1))
        {
            await ChargeHourAsync(h, pass, ct);
            pass.HoursBackfilled++;
        }

        var dropped = owed - pass.HoursBackfilled;
        if (dropped <= 0) return pass.Result();

        // Named rather than skipped. The whole point of the bound is that the platform does not get
        // to decide on its own how much free hosting is acceptable, so the hours it declined to pay
        // for are stated, with the setting that decided it.
        var from = next.AddHours(pass.HoursBackfilled);
        var message =
            $"The backfill stopped at the {bound}-hour bound; {dropped} hour(s) from " +
            $"{from:yyyy-MM-dd HH:mm}Z onwards were not charged. Raise " +
            $"{BillingOptions.SectionName}:{nameof(BillingOptions.MaxBackfillHours)}, or run the " +
            "catch-up again — the oldest hours were paid first, so the rest are still reachable.";
        pass.Report("backfill-bound", message);
        logger.LogWarning(
            "Billing backfill stopped at the {Bound}-hour bound; {Dropped} hour(s) from {From} were not charged.",
            bound, dropped, from);

        return pass.Result();
    }

    /// <summary>
    /// True when billing is switched off. Logged at debug rather than information: on an install
    /// that never turns billing on this is every hour for ever, and a line nobody needs every hour
    /// is how the log stops being read.
    /// </summary>
    private bool Off(DateTimeOffset hour)
    {
        if (options.Value.Enabled) return false;

        logger.LogDebug(
            "Billing is off ({Setting} is false); nothing was charged for {Hour}.",
            $"{BillingOptions.SectionName}:{nameof(BillingOptions.Enabled)}", hour);
        return true;
    }

    private async Task ChargeHourAsync(DateTimeOffset hour, Pass pass, CancellationToken ct)
    {
        var billingHour = TopOfHour(hour);

        // An hour is charged after it is over. Billing forward makes a customer pay for an hour they
        // might spend stopped, and there is no honest way to give it back except an adjustment line.
        if (!HasEnded(billingHour)) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        // Required, never optional. A null-tolerant resolution here would turn "nobody wired the
        // notifications up" into a pass that charges everybody, warns nobody, and reports success —
        // the exact failure this warning exists to prevent, arriving through the container.
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var workspaces = await db.Workspaces.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        if (workspaces.Count == 0) return;

        var plans = await db.Plans.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var defaultPlan = plans.FirstOrDefault(p => p.IsDefault);

        // Every size, once, rather than a join per workload. Keyed by Key because that is what an
        // app and a managed service store — the key is not editable for exactly this reason.
        var sizes = await db.InstanceSizes.IgnoreQueryFilters().AsNoTracking()
            .ToDictionaryAsync(s => s.Key, ct);

        foreach (var workspace in workspaces)
        {
            var plan = workspace.PlanId is { } id ? plans.FirstOrDefault(p => p.Id == id) : defaultPlan;

            try
            {
                var hourCostMinor = await ChargeWorkspaceAsync(db, workspace, plan, sizes, billingHour, pass, ct);

                // Deliberately outside the charge and after it. The review has to run for a
                // workspace that was charged NOTHING this hour as well as for one that was charged,
                // because "nothing is running it down any more" is what re-arms a warning already
                // sent. Folded into ChargeWorkspaceAsync it would sit behind that method's several
                // early returns and never see the case it exists for.
                await ReviewLowBalanceAsync(db, notifications, workspace, hourCostMinor, pass, ct);
            }
            // Shutdown is not a billing failure. Without the guard, stopping the panel mid-pass
            // records one failure per remaining tenant for a run that was simply asked to stop.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Named, counted and stepped over. One broken tenant must not stop the platform
                // billing the others — and the hour it lost is recoverable, because nothing was
                // written for it and the index slot is still free.
                pass.Report($"workspace:{workspace.Id}",
                    $"Workspace \"{workspace.Name}\" was not charged for {billingHour:yyyy-MM-dd HH:mm}Z: {ex.Message}");
                logger.LogError(ex,
                    "Billing workspace {Workspace} for {Hour} failed; the remaining workspaces were still charged.",
                    workspace.Id, billingHour);

                // The tracker holds this workspace's rejected lines and a wallet decrement that
                // never happened. Left there, the next workspace's save would try to write them
                // again and fail with somebody else's name on it.
                try
                {
                    db.ChangeTracker.Clear();
                }
                catch (ObjectDisposedException)
                {
                    // The context died with the write — a dropped connection, a failover, an
                    // exhausted pool. Nothing more can be read or written through it, so the pass
                    // ends here rather than reporting the same dead connection once per remaining
                    // tenant and burying the one that named the cause. The hour is untouched and
                    // the next pass will charge it.
                    logger.LogError(
                        "The billing pass for {Hour} stopped after the database connection was lost; " +
                        "{Charged} workspace(s) had been charged.", billingHour, pass.Charged.Count);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Charges one workspace for one hour, and answers with what that hour cost it in minor units.
    ///
    /// <para>
    /// The figure is what the <i>hour</i> came to, not what this pass happened to write: an hour
    /// another pass had already half-written cost the customer the same, and the low-balance review
    /// downstream is asking how fast the money is going, not who wrote the rows. Zero means the
    /// workspace held nothing chargeable — a real answer, not a missing one.
    /// </para>
    /// </summary>
    private async Task<long> ChargeWorkspaceAsync(
        HarboraDbContext db,
        Workspace workspace,
        Plan? plan,
        IReadOnlyDictionary<string, InstanceSize> sizes,
        DateTimeOffset hour,
        Pass pass,
        CancellationToken ct)
    {
        var billable = new List<BillableResource>();

        // Everything the hour holds that could not be turned into a number. One of these is enough
        // to withhold the plan minimum, because the hour's total is then not known.
        var unknowns = 0;

        void Workload(BilledResourceType type, Guid id, string name, string? sizeKey, BilledRunState state)
        {
            if (sizeKey is null || !sizes.TryGetValue(sizeKey, out var size))
            {
                unknowns++;
                // Per resource, not per size: each of these is a row somebody has to go and look at.
                pass.Report($"sizeless:{id}",
                    $"{type} \"{name}\" is on no instance size, so there is no rate to charge it at. " +
                    "Give it a size, or it holds capacity nobody is billed for.");
                return;
            }

            if (BillingRates.ForWorkload(size, state) is not { } rate)
            {
                unknowns++;
                // Per size and state, not per resource. An operator who forgot to price a popular
                // tier needs one line naming the tier, not one per workload sitting on it.
                pass.Report($"unpriced-size:{size.Key}:{state}",
                    $"Instance size \"{size.Key}\" has no price for a {state} workload, so nothing on " +
                    "it was charged for this hour. Set the rate and the hour can still be backfilled; " +
                    "set it to 0 if the tier really is free.");
                return;
            }

            billable.Add(new BillableResource(type, id, name, state, rate));
        }

        // What a gibibyte-hour costs on this workspace's plan, or null having said why. Shared by
        // an app's volume and a database's data disk on purpose: both are priced by the same column,
        // so an operator who has not set it needs one line naming the plan rather than one per thing
        // sitting on it — and two copies of that message would be free to drift apart.
        long? DiskRate(long bytes)
        {
            if (BillingRates.ForVolume(bytes, plan?.DiskGbHourMinor) is { } rate) return rate;

            unknowns++;
            pass.Report($"unpriced-disk:{plan?.Id}",
                $"Plan \"{plan?.Name ?? "(none)"}\" has no price for a gibibyte-hour, so no disk " +
                "was charged on it. Set the rate, or set it to 0 if disk really is included.");
            return null;
        }

        var apps = await db.Apps.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.WorkspaceId == workspace.Id).ToListAsync(ct);

        foreach (var app in apps)
        {
            if (!TryRunState(app.Status, out var state))
            {
                unknowns++;
                pass.Report($"app-status:{(int)app.Status}",
                    $"App \"{app.Name}\" is in status {(int)app.Status}, which this billing code has " +
                    "never heard of, so it was not charged. A status appended without a rule here " +
                    "would otherwise be hosted for free.");
                continue;
            }

            // Created and Deploying reserve nothing yet: no container, no image on disk, no port.
            if (state is not { } billedState) continue;

            Workload(BilledResourceType.App, app.Id, app.Name, app.InstanceSizeKey, billedState);
        }

        var services = await db.ManagedServices.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.WorkspaceId == workspace.Id).ToListAsync(ct);

        foreach (var service in services)
        {
            if (!TryRunState(service.Status, out var state))
            {
                unknowns++;
                pass.Report($"service-status:{(int)service.Status}",
                    $"Database \"{service.Name}\" is in status {(int)service.Status}, which this " +
                    "billing code has never heard of, so it was not charged.");
                continue;
            }

            // Provisioning reserves nothing yet, and the disk below is governed by the same answer
            // rather than by a second rule of its own. A service in this state has a data volume
            // being created as this pass runs and no measurement of it, so reporting one would be a
            // warning about a database somebody created ten minutes ago — and, because a report
            // counts as an unknown, it would cost the whole workspace its plan minimum for the hour.
            if (state is not { } billedState) continue;

            Workload(BilledResourceType.Service, service.Id, service.Name, service.InstanceSizeKey, billedState);

            // The disk the database is sitting on, which nothing else in this pass can reach: a
            // ManagedService carries its own VolumeName and StorageBytes and has no relation to the
            // Volume table, which is read below by AppId. Without this a workspace paid for its
            // database's size and then held as much data as it liked for nothing.
            //
            // Charged whatever the container is doing — a stopped database is the clearest case of
            // the rate model this branch settled on, because the data has not gone anywhere.
            if (service.StorageBytes is not { } storedBytes)
            {
                unknowns++;

                // Two states, two different things for an operator to do, and ManagedService writes
                // the timestamp even when the figure is null exactly so they can be told apart.
                // Telling somebody to go and measure a database whose measurement is broken wastes
                // the one warning they were going to read.
                var why = service.StorageMeasuredAt is { } measuredAt
                    ? $"was last measured at {measuredAt:yyyy-MM-dd HH:mm}Z and that measurement did " +
                      "not come back with a figure"
                    : "has never been measured";

                pass.Report($"unmeasured-database:{service.Id}",
                    $"Database \"{service.Name}\"'s disk {why}, so its storage was not charged for " +
                    "this hour. It is not empty; it is unread.");
                continue;
            }

            if (DiskRate(storedBytes) is not { } diskRate) continue;

            billable.Add(new BillableResource(
                BilledResourceType.ServiceVolume, service.Id, service.Name,
                BilledRunState.NotApplicable, diskRate));
        }

        // Volumes carry no WorkspaceId of their own — they are reached through their app, which is
        // why they are read by app id rather than through a navigation. A navigation predicate would
        // put the app's own query filter back into the join, which is the thing this pass is unscoped
        // to avoid.
        var appIds = apps.Select(a => a.Id).ToList();
        var volumes = await db.Volumes.IgnoreQueryFilters().AsNoTracking()
            .Where(v => appIds.Contains(v.AppId)).ToListAsync(ct);

        foreach (var volume in volumes)
        {
            if (volume.StorageBytes is not { } bytes)
            {
                unknowns++;
                // "Unmeasured is not zero" is a rule this platform already prints on every metric it
                // shows. A volume with no reading is not a volume holding nothing, and billing it for
                // nothing hosts whatever is really on it for free until somebody happens to measure.
                pass.Report($"unmeasured-volume:{volume.Id}",
                    $"Volume \"{volume.Name}\" has never been measured, so its disk was not charged " +
                    "for this hour. It is not empty; it is unread.");
                continue;
            }

            if (DiskRate(bytes) is not { } rate) continue;

            billable.Add(new BillableResource(
                BilledResourceType.Volume, volume.Id, volume.Name, BilledRunState.NotApplicable, rate));
        }

        if (plan is null)
        {
            unknowns++;
            pass.Report($"planless:{workspace.Id}",
                $"Workspace \"{workspace.Name}\" is on no plan and there is no default plan, so " +
                "nothing decides its hourly minimum or its disk price.");
        }
        else if (plan.BaseRatePerHourMinor is null)
        {
            // Reported but NOT counted as an unknown: an unpriced floor withholds only the floor
            // line, and the resources still have prices of their own. Treating it as an unknown
            // would stop a workspace being charged for its apps because of a blank on its plan.
            pass.Report($"unpriced-plan:{plan.Id}",
                $"Plan \"{plan.Name}\" has no hourly minimum, so no plan-minimum line was written " +
                "for it. Null is not zero here: set it to 0 if the plan really has no floor.");
        }

        // What the hour is known to have cost, counted exactly the way BillingHourPlan counts it: a
        // rate of zero or less writes no line and adds nothing. Counting it differently here would
        // make the message below disagree with the ledger it is describing.
        var known = billable.Sum(r => Math.Max(0L, r.RatePerHourMinor));

        // The floor is withheld above whenever anything is unknown. Saying so is only honest when a
        // top-up could actually have been due — every rate is non-negative, so an hour whose known
        // charges already reach the floor would have reached it with the missing ones added too, and
        // the shortfall was always going to be nothing. Reported there, this announces the loss of a
        // charge that never existed, in the same channel that carries the real ones. That is the
        // failure the per-entity de-duplication in `Pass` exists to prevent, arriving by another
        // route: an operator who is warned about nothing stops reading the warnings.
        //
        // Strictly greater, because a shortfall of zero writes no line either. The unpriced resource
        // itself is still reported by whichever branch above found it — this narrows one warning, it
        // does not silence the fault.
        if (unknowns > 0 && plan?.BaseRatePerHourMinor is { } floor && floor > known)
            pass.Report($"withheld-floor:{workspace.Id}",
                $"Workspace \"{workspace.Name}\" did not pay its plan minimum this pass, because " +
                $"{unknowns} of the things it held could not be priced and the shortfall is " +
                "therefore unknown. Fix those and the hours can be backfilled in full.");

        var lines = BillingHourPlan.For(
            // Built without reference to `unknowns`, and that is load-bearing rather than
            // incidental: an unknown withholds the FLOOR, never a charge that was priced. Coupling
            // the two would make one unpriced resource cost a workspace the whole hour's real
            // charges, silently and in the platform's own favour.
            // `An_hour_that_withholds_its_floor_still_charges_everything_it_could_price` is the
            // test that goes red if this list ever learns about `unknowns`.
            billable,
            // Null when anything in the hour is unknown, so no floor line is written. Passing the
            // property straight through is the whole point of the nullable parameter: there is no
            // `?? 0` here to turn "nobody priced it" back into "it is free".
            unknowns == 0 ? plan?.BaseRatePerHourMinor : null);

        // Every planned line is money out, so the hour's cost is the negation of their sum. Read off
        // the plan rather than off the wallet movement below, which is only the part this pass wrote.
        var hourCostMinor = -lines.Sum(l => l.AmountMinor);

        if (lines.Count == 0) return hourCostMinor;

        // What this hour already holds. The unique index is the authority, but reaching it means an
        // exception and a rolled-back transaction, and the ordinary retry is not a race — it is the
        // same queue message delivered twice.
        var written = await AlreadyWrittenAsync(db, workspace.Id, hour, ct);

        // `written.Add` returning false means the line is already on the bill. Adding as we go also
        // drops a duplicate inside this hour's own plan, which the index would refuse anyway.
        var fresh = lines.Where(l => written.Add((l.Type, l.Id))).ToList();
        if (fresh.Count == 0) return hourCostMinor;

        var wallet = await db.Wallets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspace.Id, ct);

        if (wallet is null)
        {
            // Created here rather than at sign-up, and only when there is something to charge: a
            // wallet row for a workspace that has never been billed says a balance of zero, which is
            // a claim about money nobody has made.
            wallet = new Wallet { WorkspaceId = workspace.Id };
            db.Wallets.Add(wallet);
        }

        foreach (var line in fresh)
        {
            db.BillingLedger.Add(new BillingLedgerEntry
            {
                WorkspaceId = workspace.Id,
                BillingHour = hour,
                Kind = line.Kind,
                AmountMinor = line.AmountMinor,
                ResourceType = line.Type,
                ResourceId = line.Id,
                // Copied, never joined: an app deleted next month must still read on this month's
                // bill.
                ResourceName = line.Name,
                RunState = line.State,
                RatePerHourMinor = line.RatePerHourMinor,
                Hours = 1,
                // Left empty on purpose. Description is where a person says why they moved money;
                // every fact about a tick's line is already in the columns beside it, and an English
                // sentence stored here could not be rendered on the Persian half of a bilingual bill.
                Description = string.Empty,
            });
        }

        var moved = fresh.Sum(l => l.AmountMinor);
        Apply(wallet, moved);

        // The lines and the balance go in one SaveChanges, which is one transaction. They are two
        // halves of one fact — the wallet is a cached total whose truth is SUM(AmountMinor) — and
        // committing them separately leaves a window where the cache is a lie, plus a crash window
        // where it stays one until somebody reconciles.
        if (!await SaveAsync(db, wallet, moved, workspace.Id, hour, fresh, ct)) return hourCostMinor;

        pass.LinesWritten += fresh.Count;
        pass.Charged.Add(workspace.Id);

        return hourCostMinor;
    }

    /// <summary>
    /// Tells a workspace its balance is running out, once — and works out whether "once" has already
    /// happened.
    ///
    /// <para>
    /// <b>Read after the money is committed, never before.</b> The verdict is a question about the
    /// balance the customer actually has, and the charge above can retry: a concurrency conflict
    /// reloads the wallet and re-applies the movement on top of somebody else's credit, which is
    /// precisely the case where a decision taken beforehand would warn a customer who had just paid.
    /// Reading here means the number this judges is the number the ledger now sums to.
    /// </para>
    ///
    /// <para>
    /// <b>The record is written before the notification goes out.</b> If the write lands and the
    /// delivery does not, the customer misses one warning and the failed attempt is on the alert rule
    /// where a broken channel is meant to be read. The other order loses the record on a crash and
    /// sends the same warning again next hour, and the hour after that, for as long as the balance
    /// stays low — and this whole mechanism exists because that flood is what makes a customer stop
    /// reading.
    /// </para>
    /// </summary>
    private async Task ReviewLowBalanceAsync(
        HarboraDbContext db,
        INotificationService notifications,
        Workspace workspace,
        long hourCostMinor,
        Pass pass,
        CancellationToken ct)
    {
        // Unfiltered for the reason the whole class is: this runs with no session behind it, and a
        // filtered read finds no wallet and quietly warns nobody. Tracked, because the verdict below
        // is written back onto this row.
        var wallet = await db.Wallets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspace.Id, ct);

        // No wallet is no charge that has ever landed, so there is nothing to be running out of and
        // nothing to re-arm. Not the same as a balance of zero, which has a row.
        if (wallet is null) return;

        var verdict = Review(
            wallet.BalanceMinor, hourCostMinor, wallet.LowBalanceHours, wallet.LowBalanceWarnedAtBalanceMinor);

        if (verdict == LowBalanceVerdict.Silent) return;

        try
        {
            wallet.LowBalanceWarnedAtBalanceMinor =
                verdict == LowBalanceVerdict.Warn ? wallet.BalanceMinor : null;

            await db.SaveChangesAsync(ct);

            if (verdict != LowBalanceVerdict.Warn) return;

            var (title, body) = LowBalanceMessage(workspace.Name, wallet.BalanceMinor, hourCostMinor);
            await notifications.NotifyAsync(
                workspace.Id, AlertEvent.LowBalance, AlertSeverity.Warning, title, body, ct);
        }
        // Shutdown is not a warning failure, same guard as the charge above.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Named rather than swallowed. Nothing here throws outward — the money is committed and
            // an unreachable Telegram bot must not undo an hour's billing — so this report is the
            // only place a warning that never reached anybody is visible at all.
            pass.Report($"low-balance-warning:{workspace.Id}",
                $"Workspace \"{workspace.Name}\" is inside its low-balance warning window and could " +
                $"not be told: {ex.Message}. Its apps will be stopped when the balance reaches zero " +
                "whether or not the warning arrived.");
            logger.LogError(ex,
                "Warning workspace {Workspace} that its balance is running low failed; the hour it was charged for stands.",
                workspace.Id);
        }
    }

    /// <summary>What the balance says should happen to the low-balance warning this hour.</summary>
    private enum LowBalanceVerdict
    {
        /// <summary>Nothing to do, and nothing to write.</summary>
        Silent,
        /// <summary>Tell them, and write down the balance they were told at.</summary>
        Warn,
        /// <summary>They are out of the window; forget the warning so the next one is news again.</summary>
        Rearm
    }

    /// <summary>
    /// The whole rule, in one place and with no database in it.
    ///
    /// <para>
    /// Two things make a warning news again, and both are here because either alone is a hole. A
    /// balance that climbed clear of the window and fell back into it is a second episode months
    /// later, which a "warn at most once ever" rule would say nothing about. And money arriving
    /// while still inside the window is a customer who read the first warning and topped up too
    /// little — the one moment when telling them again is the most useful thing this feature does,
    /// and which a plain hysteresis on the window would miss entirely.
    /// </para>
    ///
    /// <para>
    /// The counterpart is what does <i>not</i> re-arm it: time. The hourly pass only ever takes money
    /// out, so a balance above the one that was warned at can only mean somebody put money in. That
    /// is why this is a balance and not a timestamp — a timestamp needs every future writer of the
    /// wallet to remember to clear it, and a flag that stays honest only while everybody remembers
    /// is a flag that eventually lies.
    /// </para>
    /// </summary>
    private static LowBalanceVerdict Review(
        long balanceMinor, long hourCostMinor, int lowBalanceHours, long? warnedAtBalanceMinor)
    {
        // Zero is off, as a zero is on every other limit in this platform. Off leaves the record
        // alone rather than clearing it: switching a warning off is not a customer recovering, and
        // erasing an outstanding warning here would send a second copy of it the moment somebody
        // switched the warning back on.
        if (lowBalanceHours <= 0) return LowBalanceVerdict.Silent;

        if (!RunningLow(balanceMinor, hourCostMinor, lowBalanceHours))
            return warnedAtBalanceMinor is null ? LowBalanceVerdict.Silent : LowBalanceVerdict.Rearm;

        // Already told, and no money has arrived since. Twenty of these is how a customer learns to
        // skip the twenty-first, which is the one that says their site stopped.
        return warnedAtBalanceMinor is { } warned && balanceMinor <= warned
            ? LowBalanceVerdict.Silent
            : LowBalanceVerdict.Warn;
    }

    /// <summary>
    /// Whether the balance is worth fewer than <paramref name="lowBalanceHours"/> hours at what the
    /// hour just charged actually cost.
    ///
    /// <para>
    /// An hour that cost nothing is not "worth zero hours" — it is worth an unbounded number of them,
    /// because nothing is running the balance down. A workspace holding nothing chargeable is
    /// therefore never low, which is also what re-arms a warning when a customer stops everything:
    /// the apps they start again next month are a new risk, and the warning they read before they
    /// stopped was about a different set of them.
    /// </para>
    /// </summary>
    private static bool RunningLow(long balanceMinor, long hourCostMinor, int lowBalanceHours)
    {
        if (hourCostMinor <= 0) return false;

        // The threshold is hours × cost, and both are a customer's and an operator's numbers rather
        // than the platform's. Multiplying them unchecked wraps to a negative on a large enough pair
        // and turns "warn nearly everybody" into "warn nobody" — silently, and only on the installs
        // with the biggest bills. A threshold too large to hold is one no balance can reach.
        if (lowBalanceHours > long.MaxValue / hourCostMinor) return true;

        return balanceMinor < lowBalanceHours * hourCostMinor;
    }

    /// <summary>
    /// The warning, in both languages.
    ///
    /// <para>
    /// Both, because nothing here can know which one to pick. This runs on a timer with no request
    /// and therefore no culture; the destination is a channel — a Telegram group, a shared mailbox, a
    /// webhook — rather than a person with a <c>PreferredCulture</c>; and an install serving Persian
    /// customers is the one this platform is built for first. That is item 21 of the do-not-change
    /// list reaching a surface that has no request to read a language off, and the honest answer is
    /// to say it twice rather than to guess.
    /// </para>
    ///
    /// <para>
    /// It counts in hours rather than money on purpose. Hours is the unit the customer set the
    /// window in, it needs no currency and no decimal places to render, and "about nineteen hours" is
    /// the sentence somebody can act on — a balance in minor units is not.
    /// </para>
    /// </summary>
    private static (string Title, string Body) LowBalanceMessage(
        string workspaceName, long balanceMinor, long hourCostMinor)
    {
        // Floored, never rounded: a warning that promises an hour the customer does not have is
        // worse than one that understates.
        //
        // Invariant, so a pool thread that happens to be under fa-IR cannot send a figure in digits
        // the reader has never seen. Said plainly rather than left implicit: no test fails if that
        // argument is deleted, because .NET does not substitute native digits on this path today —
        // it is a guard against a format that grows one later, and against the habit of leaving the
        // thread's culture to decide what a machine-read message looks like.
        var hours = balanceMinor <= 0 ? 0 : balanceMinor / hourCostMinor;
        var left = hours.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var title = $"Balance running low: {workspaceName} — اعتبار رو به پایان است";

        var body =
            $"Workspace \"{workspaceName}\" has about {left} more hour(s) of balance at what the last " +
            "hour cost it. When the balance reaches zero its apps and databases are stopped until it " +
            "is topped up.\n\n" +
            $"اعتبار فضای کاری «{workspaceName}» با نرخ ساعت گذشته تقریباً برای {left} ساعت دیگر کافی " +
            "است. با رسیدن اعتبار به صفر، برنامه‌ها و پایگاه‌های داده‌ی آن تا زمان شارژ حساب متوقف " +
            "می‌شوند.";

        return (title, body);
    }

    /// <summary>
    /// The (type, id) of every line already on this workspace's bill for this hour, over the two
    /// kinds the tick writes. Credits and adjustments are a person's doing and may legitimately
    /// repeat within an hour, so they are not part of the key.
    /// </summary>
    private static async Task<HashSet<(BilledResourceType Type, Guid? Id)>> AlreadyWrittenAsync(
        HarboraDbContext db, Guid workspaceId, DateTimeOffset hour, CancellationToken ct) =>
        (await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId
                        && l.BillingHour == hour
                        && (l.Kind == LedgerKind.Charge || l.Kind == LedgerKind.PlanMinimumTopUp))
            .Select(l => new { l.ResourceType, l.ResourceId })
            .ToListAsync(ct))
        .Select(l => (l.ResourceType, l.ResourceId))
        .ToHashSet();

    /// <summary>
    /// Moves the balance and rotates the stamp.
    ///
    /// <para>
    /// The rotation is the load-bearing half. EF checks a concurrency token by comparing the value
    /// it read against the row it is updating, so a token nothing ever changes always matches: two
    /// writers both succeed and the second silently overwrites the first. That is last-write-wins on
    /// a balance, which is the one thing the token exists to prevent, and it looks exactly like a
    /// working lock from the outside.
    /// </para>
    /// </summary>
    private static void Apply(Wallet wallet, long moved)
    {
        wallet.BalanceMinor += moved;
        wallet.ConcurrencyStamp = Guid.CreateVersion7();
    }

    /// <summary>
    /// Writes the hour. False means the hour was already on the bill; an exception means something
    /// else went wrong and the workspace's hour is recorded as failed.
    /// </summary>
    private async Task<bool> SaveAsync(
        HarboraDbContext db,
        Wallet wallet,
        long moved,
        Guid workspaceId,
        DateTimeOffset hour,
        IReadOnlyList<PlannedLine> fresh,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            // Somebody moved the balance between the read and the write — an administrator's credit,
            // most likely. The lines are still worked out and still correct, so the balance is read
            // again and the same movement re-applied on top of theirs. DbUpdateConcurrencyException
            // derives from DbUpdateException, so this clause must come first or the unique-violation
            // catch below would swallow it.
            catch (DbUpdateConcurrencyException)
                when (attempt < WalletWriteAttempts && db.Entry(wallet).State != EntityState.Added)
            {
                var entry = db.Entry(wallet);
                await entry.ReloadAsync(ct);

                // Reload detaches an entity whose row has gone. Re-applying to a detached wallet
                // would change a value nothing is tracking and then report a clean save that wrote
                // no money at all.
                if (entry.State == EntityState.Detached) throw;

                Apply(wallet, moved);
            }
            catch (DbUpdateException e)
                when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Qualified on 23505 and nothing else, and the qualification still earns its place
                // even though the verification below would rethrow anything else anyway. A unique
                // violation leaves the connection healthy, so the question below can be asked; a
                // dropped connection or a failover does not, and asking it there raises a SECOND,
                // unrelated exception that replaces the first — an operator sent hunting a disposed
                // object while the real fault goes unrecorded.
                //
                // Everything in hand is discarded first, the wallet decrement included: the write
                // was refused as a whole, so nothing here happened.
                db.ChangeTracker.Clear();

                // 23505 says A unique index refused this, not WHICH one, and this write touches two.
                // The other is Wallets.WorkspaceId, which two passes reaching a workspace's first
                // charge at the same moment will both try to insert — and reading that as "already
                // charged" would drop an hour nobody has billed while reporting success. So the
                // question the code actually needs answering is asked directly: is this hour on the
                // bill now? The connection survives a constraint violation, so it can be asked.
                var now = await AlreadyWrittenAsync(db, workspaceId, hour, ct);
                if (fresh.All(l => now.Contains((l.Type, l.Id)))) return false;

                // Some other index refused it, or another pass wrote only part of this hour. Either
                // way it is not paid for, and saying so is the only honest answer.
                throw;
            }
        }
    }

    /// <summary>
    /// The state to bill a workload in, or null when it holds nothing yet. False means the status is
    /// one this code has never seen.
    ///
    /// <para>
    /// The unknown case is kept apart from "holds nothing yet" on purpose. <see cref="AppStatus"/> is
    /// append-only, so it will grow an arm one day, and a default case that quietly answered "not
    /// chargeable" would host every workload in the new state for free for ever while every tick
    /// reported success. Throwing instead would be worse: one appended value would stop the platform
    /// billing anybody. It is reported, and everything else in the hour is still charged.
    /// </para>
    /// </summary>
    private static bool TryRunState(AppStatus status, out BilledRunState? state)
    {
        switch (status)
        {
            case AppStatus.Running:
                state = BilledRunState.Running;
                return true;
            // Stopped but not deleted: the slot, the image and the disk are still the customer's.
            // Failed and Crashed are the same reservation, held by a workload that is not working.
            case AppStatus.Stopped or AppStatus.Failed or AppStatus.Crashed:
                state = BilledRunState.Stopped;
                return true;
            case AppStatus.Created or AppStatus.Deploying:
                state = null;
                return true;
            default:
                state = null;
                return false;
        }
    }

    /// <inheritdoc cref="TryRunState(AppStatus, out BilledRunState?)"/>
    private static bool TryRunState(ServiceStatus status, out BilledRunState? state)
    {
        switch (status)
        {
            case ServiceStatus.Running:
                state = BilledRunState.Running;
                return true;
            case ServiceStatus.Stopped or ServiceStatus.Failed:
                state = BilledRunState.Stopped;
                return true;
            case ServiceStatus.Provisioning:
                state = null;
                return true;
            default:
                state = null;
                return false;
        }
    }

    /// <summary>
    /// The top of the UTC hour the given instant falls in.
    ///
    /// <para>
    /// Every caller is normalised, not trusted. A timer naming an hour as 14:00 and a catch-up naming
    /// the same hour as 14:37 must land on one row, or the unique index has nothing to collide on and
    /// the retry it makes harmless becomes a second bill.
    /// </para>
    /// </summary>
    private static DateTimeOffset TopOfHour(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>True once the hour beginning at <paramref name="hour"/> is over.</summary>
    private bool HasEnded(DateTimeOffset hour) => hour.AddHours(1) <= clock.UtcNow;

    /// <summary>
    /// What one pass has done so far, carried across the hours of a catch-up.
    ///
    /// <para>
    /// The reported set is why it is carried: a day of backfill on a size nobody priced is one
    /// mistake, not twenty-four, and repeating it per hour is how the channel that also carries real
    /// faults becomes the one nobody reads.
    /// </para>
    /// </summary>
    private sealed class Pass
    {
        /// <summary>Distinct workspaces, so one workspace over three hours is still one workspace.</summary>
        public HashSet<Guid> Charged { get; } = [];

        public int LinesWritten { get; set; }
        public int HoursBackfilled { get; set; }

        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
        private readonly List<string> _failures = [];

        public void Report(string key, string message)
        {
            if (_reported.Add(key)) _failures.Add(message);
        }

        public BillingTickResult Result() => new(Charged.Count, LinesWritten, HoursBackfilled, _failures);
    }
}
