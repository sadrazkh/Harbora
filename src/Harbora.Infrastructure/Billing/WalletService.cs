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
