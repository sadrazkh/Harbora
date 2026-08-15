using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Billing;

/// <summary>
/// What one suspension or resumption did. Returned rather than logged, for the same reason
/// <see cref="BillingTickResult"/> is: the interesting outcomes here are the ones that raise no
/// exception.
///
/// <para>
/// <see cref="Failures"/> is not only exceptions. It carries every app the platform meant to stop and
/// did not, every app that did not come back, and every refusal to suspend at all — because each of
/// those is invisible otherwise. Nothing throws, the workspace flag flips, and the run reports
/// success having left a container up on a balance of nothing.
/// </para>
/// </summary>
/// <param name="WorkspaceSuspended">The workspace's state after the call, not before it.</param>
/// <param name="AppsStopped">Apps confirmed no longer running, by reading them again.</param>
/// <param name="AppsStarted">Apps confirmed running again, by reading them again.</param>
/// <param name="DatabasesStopped">Managed databases confirmed no longer running, likewise.</param>
/// <param name="DatabasesStarted">Managed databases confirmed running again, likewise.</param>
public sealed record BillingSuspensionResult(
    bool WorkspaceSuspended,
    int AppsStopped,
    int AppsStarted,
    int DatabasesStopped,
    int DatabasesStarted,
    IReadOnlyList<string> Failures);

/// <summary>
/// Stops a workspace's workloads when its balance runs out, and brings back exactly what that stop
/// took away.
///
/// <para>
/// <b>Runs unscoped, deliberately.</b> Nothing schedules this from a request: it is reached from the
/// hourly pass and from whatever settles a payment. The EF tenancy filters are driven by the
/// request's workspace, so work with no session behind it reads an empty <c>Apps</c> table — it would
/// stop nothing, write down nothing about what had been running, and report a clean pass, and the
/// customer would find out months later that their top-up brought back none of their services. Every
/// read here is <c>IgnoreQueryFilters</c>, the same call the retention sweeper and the hourly tick
/// both make, because relying on the ambient scope happening to be the system one is a bet this
/// codebase has already lost.
/// </para>
///
/// <para>
/// <b>Nothing is called done because a flag says so.</b> Neither method decides "already suspended"
/// or "already stopped" from state it wrote itself. A stop route that returns without an exception
/// and without stopping anything is a real shape — a filtered <c>ExecuteUpdate</c> matching no rows
/// is one line of code away — and here it costs a customer money they do not have. So both methods
/// read the apps again afterwards and report every one that is not where it was asked to be, and a
/// second suspension over an already-suspended workspace does the work again rather than returning
/// early.
/// </para>
///
/// <para>
/// <b>The two directions are not symmetrical, on purpose.</b> Suspension is refused outright when
/// billing is switched off and when the workspace is the provider's own; resumption is refused in
/// neither case. The switch guards the act that costs somebody their uptime, and turning billing off
/// after a suspension must not strand every workspace the hour before had already stopped.
/// </para>
///
/// <para>
/// <b>Billing stops only what billing can start again.</b> <see cref="ResumeAsync"/> acts on
/// <see cref="SuspensionReason.NoBalance"/> and on nothing else, so the one fact that decides
/// whether a pass may claim the reason also decides whether it may stop anything — one condition,
/// not two that can drift apart. A workspace somebody else already suspended is left entirely alone.
/// </para>
///
/// <para>
/// <b>Databases are stopped as well as apps, and for a harder reason.</b> The hourly pass charges a
/// managed database for its instance size and for the gibibytes on its disk. A suspension that
/// stopped only apps therefore produced the worst state on this feature: the customer's site down,
/// their database still running, the running database still billed against a balance of nothing —
/// and no way for them to stop it themselves, because being suspended is exactly what blocks them.
/// A database is also the one workload whose loss is not recoverable by redeploying, which is why
/// <c>ManagedService.WasRunningAtSuspension</c> is written before any container is touched and is
/// kept when a start fails.
/// </para>
///
/// <para>
/// <b>What this class assumes about the two stop/start routes, and when that stops being true.</b>
/// It calls <c>IAppOperationsService</c>, whose <c>ResolveAsync</c> reads <c>db.Apps</c>
/// <i>through</i> the tenant filter, and <c>IManagedServiceEngine</c>, whose <c>StartAsync</c> and
/// <c>StopAsync</c> read <c>db.ManagedServices</c> through it in exactly the same way. That is safe
/// here only because this class is reached from sessionless background work, where
/// <c>HttpWorkspaceScope.IsUnscoped</c> is true and the filters are inert. Give
/// <see cref="SuspendAsync"/> a caller running under any other scope — a provider-console button, a
/// webhook handler that resolves a workspace claim, a test with an <c>HttpContext</c> — and every
/// stop throws <c>Sequence contains no elements</c> before it reaches a node, while this class still
/// flags the workspace suspended and writes a marker on each workload. The failures are reported
/// rather than swallowed, so it is loud; it is still wrong. The fix is <c>IgnoreQueryFilters()</c> on
/// <c>AppOperationsService.ResolveAsync</c> <b>and</b> on its <c>SetStatusAsync</c> — together, never
/// one alone, because unfiltering only the read turns a throw into a filtered <c>ExecuteUpdate</c>
/// that matches no rows and reports success, which is the shape nobody sees — and the same call on
/// the two reads in <c>ManagedServiceEngine</c>.
/// </para>
///
/// <para>
/// <b>Resuming a database goes back through the gate, and that is deliberate.</b>
/// <c>ManagedServiceEngine.StartAsync</c> asks <c>IBillingGate</c> and throws if refused, so a
/// resumption only works inside the window the gate opens: suspended for <c>NoBalance</c> with money
/// on the account. That is the same window <c>WalletService</c> credits into before calling here,
/// and it is a feature rather than an obstacle — nothing can bring a database back onto a balance
/// that is still empty.
/// </para>
/// </summary>
public sealed class BillingSuspension(
    HarboraDbContext db,
    IAppOperationsService apps,
    IManagedServiceEngine databases,
    IOptions<BillingOptions> options,
    ILogger<BillingSuspension> logger,
    Harbora.Application.Abstractions.ISystemClock? clock = null,
    Harbora.Application.Abstractions.IFunctionEventBus? functionEvents = null)
{
    /// <summary>
    /// Tells subscribing functions that a workspace stopped or started again — the two moments a
    /// provider most often wants to automate around. Optional so the tests that drive this class
    /// directly need no bus.
    /// </summary>
    private Task PublishAsync(string key, Domain.Identity.Workspace workspace, CancellationToken ct) =>
        functionEvents?.PublishAsync(
            Domain.Functions.FunctionEvent.Create(key, workspace.Id, workspace.Name,
                ("workspace", workspace.Slug), ("reason", workspace.SuspendedReason.ToString())), ct)
        ?? Task.CompletedTask;

    /// <summary>
    /// Suspends a workspace for an empty balance: blocks its deploys, writes down what was running,
    /// and stops it.
    ///
    /// <para>
    /// Safe to call again. A workspace already suspended <i>for the balance</i> is not a finished
    /// one — the previous pass may have lost a node halfway through — so the apps still running are
    /// stopped and the record of what was running is added to.
    /// </para>
    ///
    /// <para>
    /// Does nothing to a workspace suspended for any other reason, including none. That is not
    /// politeness about the reason field: <see cref="ResumeAsync"/> is the only thing that reads
    /// what this method writes down, and it acts on <see cref="SuspensionReason.NoBalance"/> alone,
    /// so stopping apps this method may not label is stopping them with nobody left to start them.
    /// </para>
    /// </summary>
    public Task<BillingSuspensionResult> SuspendAsync(Guid workspaceId, CancellationToken ct) =>
        SuspendAsync(workspaceId, SuspensionReason.NoBalance, ct);

    public Task<BillingSuspensionResult> SuspendForSpendLimitAsync(Guid workspaceId, CancellationToken ct) =>
        SuspendAsync(workspaceId, SuspensionReason.SpendLimit, ct);

    public Task<BillingSuspensionResult> SuspendForArchiveAsync(Guid workspaceId, CancellationToken ct) =>
        SuspendAsync(workspaceId, SuspensionReason.Archived, ct);

    private async Task<BillingSuspensionResult> SuspendAsync(
        Guid workspaceId, SuspensionReason reason, CancellationToken ct)
    {
        if (reason is not (SuspensionReason.NoBalance or SuspensionReason.SpendLimit or SuspensionReason.Archived))
            throw new ArgumentOutOfRangeException(nameof(reason));
        var report = new Report();

        var workspace = await WorkspaceAsync(workspaceId, ct);
        if (workspace is null)
        {
            report.Add($"No workspace with id {workspaceId} exists, so nothing was suspended.");
            return report.Result(suspended: false);
        }

        // The switch guards the money everywhere else in this feature; here it guards something
        // dearer than money. An install that upgraded into billing unasked must not stop a tenant's
        // services over a balance nobody ever told them existed.
        if (reason != SuspensionReason.Archived && !options.Value.Enabled)
        {
            report.Add(
                $"Workspace \"{workspace.Name}\" was not suspended because " +
                $"{BillingOptions.SectionName}:{nameof(BillingOptions.Enabled)} is false. Billing " +
                "that is switched off must not stop anybody's workloads.");
            return report.Result(workspace.IsSuspended);
        }

        // The provider console already refuses this by hand. A background job has to refuse it too:
        // the default workspace is where the platform's own workloads live, so suspending it takes
        // the panel down to collect a debt the platform owes itself — and takes down the only screen
        // anybody could have used to put it right.
        if (workspace.IsDefault)
        {
            report.Add(
                $"Workspace \"{workspace.Name}\" is the provider's own workspace and is never " +
                "suspended for an empty balance; the platform's own workloads live in it.");
            return report.Result(workspace.IsSuspended);
        }

        // Somebody else already holds this workspace's suspension, so billing does not touch it.
        //
        // The reason is one field and the two facts can be true at once — an operator suspended
        // them AND the money ran out — but stopping the apps under somebody else's reason is not a
        // half-measure, it is a hole. Each stopped app would carry WasRunningAtSuspension, and the
        // only path that reads that marker is ResumeAsync, which acts on NoBalance alone. When the
        // operator lifts their suspension the console sets the reason back to None, and with it the
        // last route by which those apps could ever be started again. The customer is left with
        // containers that are down, markers that say somebody owes them a start, and nobody who
        // does.
        //
        // Deferring strands nothing, which is why it is the answer rather than merely the cautious
        // one: the apps are still running, still recorded as running, and the pass after the
        // operator lifts their suspension finds them exactly so and does the whole job under a
        // reason a top-up can lift. The cost is stated plainly in the refusal — a workspace an
        // operator has suspended keeps spending a balance it does not have until they act.
        //
        // The condition is a whitelist, not a list of reasons to skip. Billing may claim the
        // suspension when nobody holds it (this pass is starting one) or when billing already holds
        // it (this pass is retrying one). A reason added to the enum later therefore defers by
        // default instead of being quietly overwritten with NoBalance — which is the same trap
        // SuspendedReason's own doc-comment describes, arriving through the other door: relabelling
        // a suspension nobody can explain would make a payment lift it.
        if (workspace.IsSuspended && workspace.SuspendedReason != reason)
        {
            var who = workspace.SuspendedReason == SuspensionReason.Manual
                ? "by an operator"
                : "by something that did not say why";

            report.Add(
                $"Workspace \"{workspace.Name}\" is already suspended {who}, so the balance running " +
                "out did not stop its apps. Billing stops only what lifting a billing suspension " +
                "would start again, and lifting this one is not billing's to do.");
            return report.Result(suspended: true);
        }

        // Read before the flag below is set, because it decides whether this pass is starting a
        // suspension or finishing one. Past the guard above it is exactly "the reason is already
        // NoBalance"; it is kept as its own name because that is what the marker rule below is
        // asking about.
        var alreadySuspended = workspace.IsSuspended;

        var all = await AppsOfAsync(workspaceId, ct);

        // Running, and only running. Created and Deploying hold a deployment that owns its own state
        // machine and throws on an illegal transition; reaching into one from here would make this a
        // second writer on that path. Failed, Crashed and Stopped are already not running.
        var running = all.Where(a => a.Status == AppStatus.Running).ToList();

        foreach (var app in all)
        {
            if (app.Status == AppStatus.Running)
            {
                app.WasRunningAtSuspension = true;
            }
            else if (!alreadySuspended)
            {
                // A suspension that is STARTING rebuilds the set from what is actually running, so a
                // marker stranded by some earlier episode — a resumption that never finished, an
                // operator who cleared the flag by hand — cannot make a later top-up start an app
                // the customer stopped themselves months ago.
                //
                // A suspension that is CONTINUING only ever adds. The apps it is retrying are
                // stopped precisely because the last pass stopped them, and clearing their markers
                // here would erase the only record that they were ever running — the pass that
                // exists to make the outage recoverable would be the thing that made it permanent.
                app.WasRunningAtSuspension = false;
            }
        }

        var allDatabases = await DatabasesOfAsync(workspaceId, ct);

        // Provisioning is the database's Deploying: a queued job owns that row's status and writes
        // it itself, so stopping the container underneath it would make this a second writer on that
        // path. Failed and Stopped are already not running.
        var runningDatabases = allDatabases.Where(s => s.Status == ServiceStatus.Running).ToList();

        foreach (var service in allDatabases)
        {
            // The identical rule, and it has to be the identical rule. By the second pass a database
            // is Stopped precisely because the first pass stopped it, so a set rebuilt from what is
            // running now would clear the only record that it was ever up — and a top-up would then
            // bring back every app and not the data layer they all need, while every pass reported
            // success.
            if (service.Status == ServiceStatus.Running) service.WasRunningAtSuspension = true;
            else if (!alreadySuspended) service.WasRunningAtSuspension = false;
        }

        workspace.IsSuspended = true;

        // Unconditional, and only because the guard above has already established that this reason
        // is billing's to write. Testing it again here would be a second copy of the rule, free to
        // disagree with the one that decided whether the apps get stopped.
        workspace.SuspendedReason = reason;
        if (reason == SuspensionReason.SpendLimit && !alreadySuspended)
        {
            var now = (clock?.UtcNow ?? DateTimeOffset.UtcNow).ToUniversalTime();
            workspace.SpendLimitResetsAt = new DateTimeOffset(
                now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
            workspace.SpendLimitAtSuspensionMinor = workspace.MonthlySpendLimitMinor;
        }

        // Written before a single container is touched, which is the node agent's drain rule applied
        // to a workspace. If the panel dies partway through the stops below, this is the only record
        // that the apps it had not reached yet were ever running.
        await db.SaveChangesAsync(ct);

        var refused = new Dictionary<Guid, string>();

        foreach (var app in running)
        {
            try
            {
                // The platform's own route, not the daemon. It resolves the app's server engine, so
                // this works for a remote node as well as for the panel's own machine, and it is the
                // single place an app's status is written.
                await apps.StopAsync(app.Id, ct);
            }
            // Shutdown is not a suspension failure. Without the guard, stopping the panel mid-pass
            // records one failure per remaining app for a run that was simply asked to stop.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                refused[app.Id] = ex.Message;
                logger.LogError(ex,
                    "Stopping app {App} while suspending workspace {Workspace} failed; the remaining apps were still stopped.",
                    app.Id, workspaceId);
            }
        }

        var after = await StatusesAsync(workspaceId, ct);
        var stopped = 0;

        foreach (var app in running)
        {
            // Gone from the table between the two reads — somebody deleted it. Nothing of theirs is
            // running, which is what this pass was for, and it is not a failure of this pass.
            if (!after.TryGetValue(app.Id, out var status)) continue;

            if (status != AppStatus.Running)
            {
                stopped++;
                continue;
            }

            // The two ways to arrive here are worth telling apart in the message. One is a node that
            // said no, which an operator can go and look at. The other is a route that reported
            // success and changed nothing, which is the failure nobody would otherwise ever see.
            var because = refused.TryGetValue(app.Id, out var message)
                ? $": {message}"
                : ", although the stop route reported no error";

            report.Add(
                $"App \"{app.Name}\" in workspace \"{workspace.Name}\" is still running after the " +
                $"workspace was suspended{because}. It is still spending a balance the workspace " +
                "does not have; suspending again will try it once more.");
        }

        var refusedDatabases = new Dictionary<Guid, string>();

        foreach (var service in runningDatabases)
        {
            try
            {
                // The platform's own route, not the daemon — it resolves the service's server engine,
                // so this reaches a remote node, and it is the single writer of ServiceStatus.
                await databases.StopAsync(service.Id, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                refusedDatabases[service.Id] = ex.Message;
                logger.LogError(ex,
                    "Stopping database {Database} while suspending workspace {Workspace} failed; the remaining databases were still stopped.",
                    service.Id, workspaceId);
            }
        }

        var databasesAfter = await DatabaseStatusesAsync(workspaceId, ct);
        var databasesStopped = 0;

        foreach (var service in runningDatabases)
        {
            // Deleted between the two reads. Nothing of theirs is running, which is what this pass
            // was for.
            if (!databasesAfter.TryGetValue(service.Id, out var status)) continue;

            if (status != ServiceStatus.Running)
            {
                databasesStopped++;
                continue;
            }

            var because = refusedDatabases.TryGetValue(service.Id, out var message)
                ? $": {message}"
                : ", although the stop route reported no error";

            report.Add(
                $"Database \"{service.Name}\" in workspace \"{workspace.Name}\" is still running " +
                $"after the workspace was suspended{because}. It is still spending a balance the " +
                "workspace does not have — on its size and on every gibibyte of its disk — and the " +
                "customer cannot stop it themselves while they are suspended. Suspending again will " +
                "try it once more.");
        }

        // Told after the workloads are down, not before: a function that reacts to a suspension is
        // reacting to one that has actually happened.
        await PublishAsync(Domain.Functions.FunctionEvents.WorkspaceSuspended, workspace, ct);

        return report.Result(suspended: true, appsStopped: stopped, databasesStopped: databasesStopped);
    }

    /// <summary>
    /// Lifts a suspension the balance caused, and starts back only the apps that suspension stopped.
    ///
    /// <para>
    /// Does nothing at all unless the reason is <see cref="SuspensionReason.NoBalance"/>. Paying a
    /// bill is not a request to undo an operator's decision — and the test of that is the reason
    /// being NoBalance, not merely being other than Manual, because every workspace suspended before
    /// the reason existed carries None.
    /// </para>
    ///
    /// <para>
    /// The workspace's flags are cleared only once every app it remembers is running again. An app
    /// that did not come back keeps its marker and keeps the suspension alive, so the resume can be
    /// run again and finish. Clearing the flags anyway would be worse than it looks: the marker is
    /// the only record that the app was ever running, and the next suspension replaces the set.
    /// </para>
    /// </summary>
    public Task<BillingSuspensionResult> ResumeAsync(Guid workspaceId, CancellationToken ct) =>
        ResumeAsync(workspaceId, allowArchive: false, ct);

    public Task<BillingSuspensionResult> ResumeArchivedAsync(Guid workspaceId, CancellationToken ct) =>
        ResumeAsync(workspaceId, allowArchive: true, ct);

    private async Task<BillingSuspensionResult> ResumeAsync(
        Guid workspaceId, bool allowArchive, CancellationToken ct)
    {
        var report = new Report();

        var workspace = await WorkspaceAsync(workspaceId, ct);
        if (workspace is null)
        {
            report.Add($"No workspace with id {workspaceId} exists, so nothing was resumed.");
            return report.Result(suspended: false);
        }

        // Not reported as a failure. A payment landing on a workspace an operator suspended, or on
        // one that was never suspended at all, is an ordinary thing that happens; the correct
        // response is to leave it exactly as it is.
        if (allowArchive
            ? workspace.SuspendedReason != SuspensionReason.Archived
            : workspace.SuspendedReason is not (SuspensionReason.NoBalance or SuspensionReason.SpendLimit))
            return report.Result(workspace.IsSuspended);

        if (workspace.SuspendedReason == SuspensionReason.SpendLimit)
        {
            if (!WorkspaceBudgetService.CanResetSpendLimit(
                    workspace, clock?.UtcNow ?? DateTimeOffset.UtcNow))
                return report.Result(suspended: true);
        }

        var marked = (await AppsOfAsync(workspaceId, ct)).Where(a => a.WasRunningAtSuspension).ToList();
        var refused = new Dictionary<Guid, string>();
        var asked = new HashSet<Guid>();

        foreach (var app in marked)
        {
            // Already back — the node returned, or somebody started it by hand. Start on a running
            // container is a restart, and a restart is a visible outage handed to a customer who has
            // just paid.
            if (app.Status == AppStatus.Running) continue;

            asked.Add(app.Id);

            try
            {
                await apps.StartAsync(app.Id, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                refused[app.Id] = ex.Message;
                logger.LogError(ex,
                    "Starting app {App} while resuming workspace {Workspace} failed; the remaining apps were still started.",
                    app.Id, workspaceId);
            }
        }

        var after = await StatusesAsync(workspaceId, ct);
        var started = 0;
        var stranded = 0;

        foreach (var app in marked)
        {
            // Deleted while the workspace was suspended. There is nothing to bring back and nothing
            // to keep remembering.
            if (!after.TryGetValue(app.Id, out var status))
            {
                app.WasRunningAtSuspension = false;
                continue;
            }

            if (status == AppStatus.Running)
            {
                app.WasRunningAtSuspension = false;
                if (asked.Contains(app.Id)) started++;
                continue;
            }

            stranded++;

            var because = refused.TryGetValue(app.Id, out var message)
                ? $": {message}"
                : ", although the start route reported no error";

            report.Add(
                $"App \"{app.Name}\" in workspace \"{workspace.Name}\" did not come back when the " +
                $"suspension was lifted{because}. It is still marked as one this workspace owes a " +
                "start, so resuming again will try it once more.");
        }

        var markedDatabases = (await DatabasesOfAsync(workspaceId, ct))
            .Where(s => s.WasRunningAtSuspension).ToList();
        var refusedDatabases = new Dictionary<Guid, string>();
        var askedDatabases = new HashSet<Guid>();

        foreach (var service in markedDatabases)
        {
            // Already back. Starting a running database is a restart, which is a visible outage —
            // and on a database, an outage handed to every app attached to it — given to a customer
            // who has just paid.
            if (service.Status == ServiceStatus.Running) continue;

            askedDatabases.Add(service.Id);

            try
            {
                await databases.StartAsync(service.Id, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                refusedDatabases[service.Id] = ex.Message;
                logger.LogError(ex,
                    "Starting database {Database} while resuming workspace {Workspace} failed; the remaining databases were still started.",
                    service.Id, workspaceId);
            }
        }

        var databasesAfter = await DatabaseStatusesAsync(workspaceId, ct);
        var databasesStarted = 0;

        foreach (var service in markedDatabases)
        {
            // Deleted while the workspace was suspended: nothing to bring back, nothing to keep
            // remembering.
            if (!databasesAfter.TryGetValue(service.Id, out var status))
            {
                service.WasRunningAtSuspension = false;
                continue;
            }

            if (status == ServiceStatus.Running)
            {
                service.WasRunningAtSuspension = false;
                if (askedDatabases.Contains(service.Id)) databasesStarted++;
                continue;
            }

            stranded++;

            var because = refusedDatabases.TryGetValue(service.Id, out var message)
                ? $": {message}"
                : ", although the start route reported no error";

            report.Add(
                $"Database \"{service.Name}\" in workspace \"{workspace.Name}\" did not come back " +
                $"when the suspension was lifted{because}. It is still marked as one this workspace " +
                "owes a start, so resuming again will try it once more — and every app attached to " +
                "it will fail to reach it until then.");
        }

        if (stranded == 0)
        {
            workspace.IsSuspended = false;
            workspace.SuspendedReason = SuspensionReason.None;
            workspace.SpendLimitResetsAt = null;
            workspace.SpendLimitAtSuspensionMinor = null;
        }
        else
        {
            report.Add(
                $"Workspace \"{workspace.Name}\" is still suspended because {stranded} of the " +
                "workloads the suspension stopped are not running again. Lifting it here would " +
                "throw away the only record that they were ever running.");
        }

        await db.SaveChangesAsync(ct);

        // Only when the suspension actually lifted. A resume that left workloads stranded has not
        // resumed anything, and saying otherwise to a function is how an automation acts on a
        // workspace that is still down.
        if (!workspace.IsSuspended)
            await PublishAsync(Domain.Functions.FunctionEvents.WorkspaceResumed, workspace, ct);

        return report.Result(
            workspace.IsSuspended, appsStarted: started, databasesStarted: databasesStarted);
    }

    /// <summary>
    /// The workspace, tracked, whatever tenant the ambient scope believes it is.
    ///
    /// <para>
    /// <c>Workspace</c> carries no query filter of its own today — it is the table the filters are
    /// written in terms of — so this <c>IgnoreQueryFilters</c> currently changes nothing, and no test
    /// can be made to fail by deleting it. It is here anyway, and said out loud rather than left
    /// implicit: this class must not depend on which tables happen to be filtered this month.
    /// </para>
    /// </summary>
    private Task<Workspace?> WorkspaceAsync(Guid workspaceId, CancellationToken ct) =>
        db.Workspaces.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == workspaceId, ct);

    /// <summary>Every app in the workspace, tracked, unfiltered.</summary>
    private Task<List<App>> AppsOfAsync(Guid workspaceId, CancellationToken ct) =>
        db.Apps.IgnoreQueryFilters().Where(a => a.WorkspaceId == workspaceId).ToListAsync(ct);

    /// <summary>
    /// What the table says each of this workspace's apps is doing, read fresh.
    ///
    /// <para>
    /// Fresh is the whole requirement. The stop route writes the status with <c>ExecuteUpdate</c>,
    /// which never reaches this context's change tracker, so a read answered from the tracked copies
    /// loaded a moment earlier would hand back the status as it was BEFORE the stop and confirm work
    /// that never happened. The projection to scalars is what guarantees it: a query that returns no
    /// entity type has no identity resolution to fall back on, so the values come from the store.
    /// <c>AsNoTracking</c> says the same thing out loud and would matter if this ever selected the
    /// entity again — a mutation removing it kills no test today, which is stated here rather than
    /// left for somebody to discover.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, AppStatus>> StatusesAsync(Guid workspaceId, CancellationToken ct) =>
        await db.Apps.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId)
            .Select(a => new { a.Id, a.Status })
            .ToDictionaryAsync(a => a.Id, a => a.Status, ct);

    /// <summary>Every managed database in the workspace, tracked, unfiltered.</summary>
    private Task<List<ManagedService>> DatabasesOfAsync(Guid workspaceId, CancellationToken ct) =>
        db.ManagedServices.IgnoreQueryFilters().Where(s => s.WorkspaceId == workspaceId).ToListAsync(ct);

    /// <inheritdoc cref="StatusesAsync"/>
    private async Task<Dictionary<Guid, ServiceStatus>> DatabaseStatusesAsync(
        Guid workspaceId, CancellationToken ct) =>
        await db.ManagedServices.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .Select(s => new { s.Id, s.Status })
            .ToDictionaryAsync(s => s.Id, s => s.Status, ct);

    /// <summary>What one call has to say for itself.</summary>
    private sealed class Report
    {
        private readonly List<string> _failures = [];

        public void Add(string message) => _failures.Add(message);

        public BillingSuspensionResult Result(
            bool suspended,
            int appsStopped = 0,
            int appsStarted = 0,
            int databasesStopped = 0,
            int databasesStarted = 0) =>
            new(suspended, appsStopped, appsStarted, databasesStopped, databasesStarted, _failures);
    }
}
