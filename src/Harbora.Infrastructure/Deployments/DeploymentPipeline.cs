using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Networking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// The end-to-end deployment pipeline: checkout → build → run → wire proxy → health check →
/// mark active. Each stage streams+persists logs (with secrets redacted). A failure leaves the
/// previously-running container untouched so the app keeps serving traffic.
/// </summary>
public sealed class DeploymentPipeline(
    HarboraDbContext db,
    IServerEngineFactory engineFactory,
    IGitService git,
    IProxyEngine proxy,
    IDeploymentLogStream stream,
    ISecretProtector protector,
    ISecretRedactor redactor,
    INotificationService notifications,
    IBillingGate billing,
    IHttpClientFactory httpFactory,
    ISystemClock clock,
    IOptions<HarboraRuntimeOptions> options,
    HostPortAllocator hostPorts,
    Nodes.NodeIngressRouter ingressRouter,
    IFunctionEventBus functionEvents,
    ILogger<DeploymentPipeline> logger)
{
    private readonly HarboraRuntimeOptions _opt = options.Value;

    public async Task ExecuteAsync(Guid deploymentId, CancellationToken ct)
    {
        var deployment = await db.Deployments.Include(d => d.App)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
        if (deployment?.App is null) return;

        // This deployment is already over — failed by a restart, or cancelled. Nothing here can add
        // to it, and the first thing below would move it back to Building, which the state machine
        // rightly refuses. That refusal used to be caught by this method's own failure path, which
        // overwrote the real reason with "Illegal deployment transition Failed → Building" and
        // alerted the operator a second time about a deployment that had already finished failing.
        // JobStartupGate is what should stop a settled deployment ever arriving here; this is the
        // second answer to the same question, for a duplicate dispatch from anywhere else.
        if (DeploymentStateMachine.IsTerminal(deployment.Status))
        {
            logger.LogInformation(
                "Deployment {Id} (#{Number}) is already {Status}; there is nothing left to run.",
                deploymentId, deployment.Number, deployment.Status);
            return;
        }

        var app = await db.Apps
            .Include(a => a.EnvironmentVariables)
            .Include(a => a.Volumes)
            .Include(a => a.Domains)
            .Include(a => a.GitRepository)!.ThenInclude(r => r!.Provider)
            .FirstAsync(a => a.Id == deployment.AppId, ct);

        // Resolve the container engine for this app's server (local or remote agent).
        var docker = await engineFactory.ResolveAsync(app.ServerId, ct);

        var secrets = app.EnvironmentVariables.Where(e => e.IsSecret)
            .Select(e => SafeUnprotect(e.Value)).Where(v => v.Length > 0).ToList();
        long seq = 0;

        // Build/pull progress arrives on the container engine's own threads (IProgress dispatches
        // via the thread pool — there is no SynchronizationContext here). A DbContext is NOT
        // thread-safe, so those lines are queued and persisted on this thread instead; writing them
        // directly would mutate the change tracker mid-SaveChangesAsync. Live streaming is
        // unaffected — it happens immediately, off-thread, and never touches the DbContext.
        var pendingEngineLogs = new System.Collections.Concurrent.ConcurrentQueue<(LogStream Stream, string Message)>();

        void DrainEngineLogs()
        {
            while (pendingEngineLogs.TryDequeue(out var pending))
                db.DeploymentLogs.Add(new DeploymentLog
                {
                    DeploymentId = deploymentId, Stream = pending.Stream, Sequence = seq++,
                    Message = pending.Message, Timestamp = clock.UtcNow
                });
        }

        // Safe to call from any thread.
        async Task LogFromEngine(LogStream s, string message)
        {
            var clean = LogText.Clean(redactor.Redact(message, secrets));
            pendingEngineLogs.Enqueue((s, clean));
            await stream.PublishLogAsync(deploymentId, s, clean, ct);
        }

        // Pipeline-thread logging only. Part of the work, so it takes the work's token: a line
        // written while the deployment is still running is one more thing a cancelled deployment
        // has no reason to be doing.
        Task Log(LogStream s, string message) => Append(s, message, ct);

        // The same line, written once there is nothing left to cancel.
        //
        // Two callers, both of them past the point where the work has stopped: the catch below, and
        // RepublishRoutingAsync, which runs while a proxy failure is already on its way up and says
        // in the log that the routing has been put back — a claim the failure message makes and
        // which used to go unsubstantiated exactly when it mattered most.
        // The distinction is the one JobWorker.SettleAsync and BackupSnapshotService already
        // draw: doing more WORK under a cancelled token is wrong, and recording what has ALREADY
        // happened under one is the whole reason the record exists. Deliberately not applied to
        // Log itself: on every other path the token is live, so the two are the same call, and a
        // pipeline whose logging silently outlived its own cancellation would be a different and
        // worse thing than the bug this fixes.
        Task Record(LogStream s, string message) => Append(s, message, CancellationToken.None);

        async Task Append(LogStream s, string message, CancellationToken publishOn)
        {
            var clean = Stage(s, message);
            await stream.PublishLogAsync(deploymentId, s, clean, publishOn);
        }

        // The durable half of a log line on its own: redact, clean, queue the row, hand the text
        // back. Split out so the failure path can put the row in front of the SaveChangesAsync it
        // already makes, instead of behind a hub call that may be the thing that is broken.
        string Stage(LogStream s, string message)
        {
            var clean = LogText.Clean(redactor.Redact(message, secrets));
            DrainEngineLogs();
            db.DeploymentLogs.Add(new DeploymentLog
            {
                DeploymentId = deploymentId, Stream = s, Sequence = seq++,
                Message = clean, Timestamp = clock.UtcNow
            });
            return clean;
        }

        // Everything logged after the LAST status change, made durable.
        //
        // Append only ADDS its row; the flush has always come from whatever saved next, and on the
        // success path nothing does. SetStatus(Succeeded) is the last save, and the "✅ Deployment
        // #N succeeded." line comes after it, as do image retention's two lines. JobWorker creates
        // this pipeline's scope (RunAndSettleAsync) and SettleAsync builds its own, so the change
        // tracker holding those rows was disposed unread: reload a successful deployment's page and
        // its log stopped mid-cutover, with no terminal line and no word of what retention did.
        // The status row kept the page header honest, which is what made it the quieter version of
        // the failure-path lie rather than a different one.
        //
        // On CancellationToken.None for the reason the catch below states at length: the deployment
        // is over by the time this runs, so there is nothing here left to cancel — only the account
        // of it, which is exactly what a cancelled job still owes the person who asked for it.
        async Task FlushLog()
        {
            DrainEngineLogs();
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // One surface of the report a finished deployment owes somebody, attempted on its own.
        //
        // The failure path's three share nothing but being downstream of one failure, and they used
        // to run as a single unguarded sequence — so a fault in the first took the other two with
        // it. The realistic fault is not cancellation (they already run on None): it is a host
        // shutting down and disposing the SignalR hub context out from under an in-flight deploy,
        // which throws ObjectDisposedException. That is the "one catch block failing five different
        // ways from a single cause" this method's own comment names, and a hub that is going away is
        // not a reason for nobody to be alerted. The success tail's one line uses it for the same
        // reason, which is why the warning below names neither an outcome nor a count.
        async Task TellSomebody(Func<Task> surface, string what)
        {
            try { await surface(); }
            catch (Exception surfaceError)
            {
                logger.LogWarning(surfaceError,
                    "Deployment {Id} has finished, and the {Surface} could not be told.", deploymentId, what);
            }
        }

        // All status changes go through the state machine (ADR-004): illegal transitions throw,
        // timestamps are stamped consistently, and the row is persisted + streamed on every change.
        async Task SetStatus(DeploymentStatus status)
        {
            DeploymentStateMachine.Transition(deployment, status, clock.UtcNow);
            await stream.PublishStatusAsync(deploymentId, status, ct);
            DrainEngineLogs();
            await db.SaveChangesAsync(ct);
        }

        // Taken before anything runs, so it records what this version was released with even if the
        // deploy fails: comparing a failed release against the last good one is the whole point.
        deployment.ConfigJson = DeploymentConfig
            .From(app, v => v.IsSecret ? SafeUnprotect(v.Value) : v.Value, protector.DeriveKey("config-fingerprint"))
            .ToJson();

        // Money before machinery, and asked HERE rather than where the deployment was queued.
        // Eleven call sites queue one — five buttons on the app page, two API routes, the redeploy
        // page, a webhook, a preview, a template stack — and the queue is durable, so any of them
        // can be claimed an hour after the balance that paid for it ran out. A check on the buttons
        // would be eleven checks, one of which somebody will forget; this is one.
        //
        // Refused outside the try on purpose. The catch below writes
        // `app.Status = ActiveDeploymentId is null ? Failed : Running`, which is right for a deploy
        // that broke halfway and wrong for one that never started: an app the suspension had
        // stopped would be recorded as Running, and the next hour would be billed for a container
        // that is not there.
        var mayStart = await billing.CanStartAsync(
            app.WorkspaceId, Domain.Billing.BilledResourceType.App, app.Id, ct);
        if (!mayStart.Allowed)
        {
            // English only, on purpose — this is a decision, not the gap QuotaCheck.ReasonFa was
            // added to close. deployment.ErrorMessage and the line below land in the deploy log
            // alongside git checkout output, Docker build errors and health-check diagnosis, none of
            // which is ever translated; the log is exactly the "deliberate LTR island" the panel's
            // RTL rules already carve out for code, IDs and terminals (see
            // docs/product-audit/19-do-not-change-list.md, item 21). Splicing Persian into one line
            // of that stream would not make the log readable to a Persian speaker — everything around
            // it would still be English — it would just make this one line look like a rendering bug.
            // And unlike Start/Restart, nobody reading it is necessarily the person the refusal
            // happened to: the queue is durable, so this may be read an hour later by whoever is
            // debugging the deploy, in whatever language that happens to be.
            deployment.ErrorMessage = mayStart.Reason;
            await SetStatus(DeploymentStatus.Failed);
            await Record(LogStream.System, $"❌ Deployment refused: {mayStart.Reason}");
            await FlushLog();
            return;
        }

        try
        {
            await SetStatus(DeploymentStatus.Building);
            await Log(LogStream.System, $"Deployment #{deployment.Number} started ({app.SourceType}).");

            // A Compose app is resolved up front: the file is parsed and validated BEFORE anything is
            // built or started, so an unsupported directive is a plain rejection rather than a
            // half-deployed stack.
            var stackBuildLog = new Progress<string>(l => _ = LogFromEngine(LogStream.Build, l));
            ComposeParseResult? composeStack = null;
            if (app.SourceType == AppSourceType.DockerCompose)
                composeStack = await LoadComposeAsync(app, deployment, stackBuildLog, Log, ct);

            IReadOnlyCollection<string> keepContainers = [];
            string imageTag;
            // An image chosen at deploy time (`harbora deploy --image`) is released as-is: there is
            // no source to fetch and nothing to build, so pull it and go straight to the cutover.
            if (deployment.RolledBackFromId is null && !string.IsNullOrWhiteSpace(deployment.ImageTag))
            {
                imageTag = deployment.ImageTag!;
                await Log(LogStream.System, $"Releasing image {imageTag} (nothing to build).");
                await docker.PullImageAsync(imageTag, new Progress<string>(l => _ = LogFromEngine(LogStream.Build, l)), ct);
            }
            else if (deployment.RolledBackFromId is { } rollbackTargetId)
            {
                // Rollback = re-release a prior artifact. Never rebuild (instant + exact; ADR-006).
                var target = await db.Deployments.FirstOrDefaultAsync(d => d.Id == rollbackTargetId, ct);
                imageTag = DeploymentPlanning.ResolveRollbackImage(target);

                // The artifact must still be on the node. Checking here — before anything is started
                // or changed — turns a confusing mid-deploy failure into a clear "can't do that yet".
                if (!await docker.ImageExistsAsync(imageTag, ct))
                    throw new InvalidOperationException(
                        $"The image for deployment #{target!.Number} ({imageTag}) is no longer on this server, " +
                        "so it cannot be re-released. It was most likely pruned by image retention — " +
                        "deploy that commit from source instead.");

                await Log(LogStream.System,
                    $"Rolling back to deployment #{target!.Number}; re-releasing image {imageTag} (no rebuild).");
            }
            else
            {
                imageTag = $"{_opt.ImagePrefix}/{app.Slug}:build-{deployment.Number}";
                var buildLog = new Progress<string>(l => _ = LogFromEngine(LogStream.Build, l));
                // A Compose stack has no single image; its services are built or pulled by the
                // deployer below. The recorded tag names the stack so history still reads sensibly.
                imageTag = composeStack is null
                    ? await AcquireImageAsync(docker, app, deployment, imageTag, buildLog, Log, ct)
                    : $"{_opt.ImagePrefix}/{app.Slug}:compose-{deployment.Number}";
            }
            deployment.ImageTag = imageTag;

            await SetStatus(DeploymentStatus.Deploying);

            // Per-tenant isolation: each workspace gets its own network. Apps + their attached
            // services share it; other tenants can't reach them.
            var wsSlug = await db.Workspaces.Where(w => w.Id == app.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
            var workspaceNetwork = _opt.WorkspaceNetwork(wsSlug);

            // Each environment gets its own private network, so staging cannot reach production's
            // database by name. During the move both are attached — see NetworkPlan: a service that
            // has redeployed must stay reachable by the ones that have not.
            var environmentNetwork = await ResolveEnvironmentNetworkAsync(app, ct);
            var networks = Networking.NetworkPlan.For(environmentNetwork, workspaceNetwork, keepWorkspaceNetwork: true);
            var network = Networking.NetworkPlan.Primary(environmentNetwork, workspaceNetwork);

            foreach (var name in networks) await docker.EnsureNetworkAsync(name, ct);
            foreach (var v in app.Volumes)
                await docker.EnsureVolumeAsync(v.Name, ct);

            // A scheduled job has no long-running container: releasing it means having the image its
            // runs will use, and nothing more. Starting one and health-gating it fails every deploy
            // of a service that is behaving exactly as designed — which is what happened the first
            // time a cron service was created here. It reported "Failed", and went on running its
            // schedule successfully every minute underneath that.
            if (!ServicePlan.IsLongRunning(app.Kind))
            {
                await RetireOldContainersAsync(docker, app.WorkspaceId, app.Slug, [], Log, ct);

                if (deployment.RolledBackFromId is not null)
                    await MarkSupersededAsRolledBackAsync(app.ActiveDeploymentId, deployment.Id, Log, ct);

                app.ActiveDeploymentId = deployment.Id;
                // Here "Running" means enabled rather than "a process is up": Stop disables the
                // schedule and the runner skips it. There is no container to describe.
                app.Status = AppStatus.Running;
                // Cleared so the next tick recomputes from the schedule as it stands now — a deploy
                // that changed the expression must not keep firing at the old time.
                app.NextRunAt = null;
                // Neither kind in this branch ever starts a long-lived container, so nothing can
                // answer to a private name — recorded explicitly rather than left null, which the
                // page would otherwise read as "not deployed since this shipped" for ever.
                app.PrivateAddressState = PrivateAddressOutcome.KindDoesNotJoin;
                await SetStatus(DeploymentStatus.Succeeded);
                await Log(LogStream.System,
                    $"✅ Deployment #{deployment.Number} succeeded. " +
                    (app.Kind == ServiceKind.Cron
                        ? $"Nothing is started now — this job runs on its schedule ({app.CronExpression})."
                        : "Nothing is started now — this service runs on demand."));
                await PruneOldImagesAsync(docker, app, Log, ct);
                await FlushLog();
                return;
            }

            // Zero-downtime cutover (ADR-007): the new container gets a versioned name and starts
            // ALONGSIDE the currently-serving one. We only retire the old container(s) AFTER the new
            // one is healthy and traffic has been switched — so a failed deploy never drops traffic.
            var containerName = DeploymentPlanning.ContainerName(app.WorkspaceId, app.Slug, deployment.Number);

            // Decide how the proxy reaches this app. On the local node Traefik joins the tenant
            // network and routes by container name. On a remote node there is no shared overlay, so
            // we publish the container port to a per-deployment host port (lets old + new coexist).
            var server = await db.Servers.FirstAsync(s => s.Id == app.ServerId, ct);

            // What the image says about itself beats what the app was configured with. A stock
            // ASP.NET Core project listens on 8080 and declares it, so an app created with port 80
            // built, started, logged "Application started" — and then failed a health check aimed at
            // a port nothing was listening on.
            var containerPort = await ResolveContainerPortAsync(docker, app, imageTag, Log, ct);

            int? publishPort = null;
            string upstreamHost = containerName;
            var upstreamPort = containerPort;
            if (!server.IsLocal)
            {
                // Reserved, not derived: a hashed port consulted nothing about what was already
                // in use, and a port reused across apps aims one app's route at another's container.
                publishPort = await hostPorts.AllocateAsync(server.Id, app.Id, deployment.Number, ct);

                // A node behind NAT publishes on a port only its own machine can reach, so the
                // proxy is sent to a port here instead and the bytes go back down the node's tunnel.
                var upstream = await ingressRouter.ResolveAsync(server, app.Id, deployment.Number, publishPort.Value, ct);
                upstreamHost = upstream.Host;
                upstreamPort = upstream.Port;

                if (upstream.Tunnelled)
                    await Log(LogStream.System,
                        $"This node is reached through its ingress tunnel; the proxy will target {upstream.Host}:{upstream.Port}.");
            }
            else
            {
                // Give the local Traefik ingress into this tenant's network, and the panel reach
                // for HTTP health checks by container name (both idempotent, best-effort).
                foreach (var name in networks)
                {
                    await docker.ConnectNetworkAsync(_opt.ProxyContainerName, name, ct);
                    await docker.ConnectNetworkAsync(_opt.PanelContainerName, name, ct);
                }
            }

            var env = BuildEnv(app);
            var labels = new Dictionary<string, string>
            {
                ["harbora.managed"] = "true",
                ["harbora.app"] = app.Slug,
                // Slug alone is unique platform-wide now (HarboraDbContext: HasIndex(x =>
                // x.Slug).IsUnique()), but containers are listed host-wide and an install where that
                // migration could not apply keeps the old per-workspace uniqueness — so this is still
                // the label RetireOldContainersAsync and CurrentContainerId actually match ownership
                // on (DeploymentPlanning.WorkspaceLabel), not the slug above.
                [DeploymentPlanning.WorkspaceLabel] = app.WorkspaceId.ToString(),
                // The id is what TakenAliasesAsync matches siblings by, so a same-slugged app in a
                // different workspace cannot be mistaken for a sibling.
                ["harbora.app.id"] = app.Id.ToString(),
                ["harbora.deployment"] = deployment.Number.ToString()
            };

            // A Compose stack starts several containers instead of one, so it takes over from here
            // and returns the name + port the proxy should target. The guarantees are the same:
            // everything new starts alongside the old stack, health is checked before any cutover.
            if (composeStack is not null)
            {
                var web = await StartComposeStackAsync(
                    docker, app, deployment, composeStack, network, labels, server, stackBuildLog, Log, ct);
                containerName = web.ContainerName;
                // A remote server's upstream was already decided above, and on a tunnelled node it
                // is the panel's own port — naming server.Hostname here would quietly route around
                // the tunnel and leave a compose stack unreachable where a single container works.
                upstreamHost = server.IsLocal ? web.ContainerName : upstreamHost;
                upstreamPort = server.IsLocal ? web.Port : publishPort is not null ? upstreamPort : web.Port;
                keepContainers = web.AllContainerNames;

                await SetStatus(DeploymentStatus.HealthChecking);
                var stackHealth = await WaitForHealthyAsync(docker, upstreamHost, upstreamPort,
                    web.ContainerName, app.HealthCheckPath, msg => Log(LogStream.System, msg), ct);
                if (!stackHealth.IsHealthy)
                    throw new InvalidOperationException(
                        $"The '{web.ServiceName}' service did not become healthy. " +
                        HealthDiagnosis.Explain(stackHealth, web.ContainerName));

                await WireProxyAsync(app, upstreamHost, upstreamPort, Log, Record, ct);
                await RetireOldContainersAsync(docker, app.WorkspaceId, app.Slug, keepContainers, Log, ct);

                if (deployment.RolledBackFromId is not null)
                    await MarkSupersededAsRolledBackAsync(app.ActiveDeploymentId, deployment.Id, Log, ct);

                app.ActiveDeploymentId = deployment.Id;
                app.Status = AppStatus.Running;
                // Not KindDoesNotJoin: a stack's services DO join the network and answer to names —
                // just not to the app's own slug. Each service already carries its own alias
                // (StartComposeStackAsync, above), so there is no single app-level name to report,
                // and the app's slug may not even match any of them.
                app.PrivateAddressState = PrivateAddressOutcome.ComposeManaged;
                await SetStatus(DeploymentStatus.Succeeded);
                await Log(LogStream.System,
                    $"✅ Deployment #{deployment.Number} succeeded ({composeStack.Services.Count} services).");
                await PruneOldImagesAsync(docker, app, Log, ct);
                await FlushLog();
                return;
            }

            // The release task runs from the NEW image, with this app's environment and network, but
            // before anything is started or switched. A failure here fails the deployment while the
            // current version is still serving — which is the whole reason it does not live inside the
            // container's own start-up, where a failed migration takes the site down with it.
            await RunReleaseTaskAsync(docker, app, imageTag, network, env, Log, LogFromEngine, ct);

            // The compose path twenty lines below has always done this; the ordinary path never did,
            // so an app was reachable only as harbora-{slug}-{number} — a name that changes every
            // time it ships. An ambiguous alias is withheld rather than registered: docker balances
            // between every container answering to a name, so a duplicate silently sends a share of
            // the calls to a stranger.
            var privateAddress = PrivateAddress.Decide(app.Kind, app.Slug, await TakenAliasesAsync(docker, app, ct));

            await Log(LogStream.System, $"Starting container {containerName} …");
            var containerId = await docker.RunContainerAsync(new DockerRunRequest(
                imageTag, containerName, network, env, labels,
                app.Volumes.Select(v => (v.Name, v.MountPath, v.ReadOnly)).ToList(),
                containerPort, app.MemoryLimitBytes, app.CpuLimit, app.HealthCheckPath,
                Command: null, PublishToHostPort: publishPort,
                NetworkAliases: privateAddress.HasAlias ? [privateAddress.Alias!] : null), ct);
            // Recorded only once the container that would answer to it actually exists — assigning it
            // alongside the decision above would let a failure between here and there (the run call
            // itself throwing, e.g.) flush a state describing a container that was never started.
            app.PrivateAddressState = privateAddress.Outcome;

            // A container is created on one network; the rest are attached now. Both are needed
            // only while the platform moves to per-environment networks (see NetworkPlan).
            foreach (var extra in networks.Skip(1))
                await docker.ConnectNetworkAsync(containerName, extra, ct);

            await Log(LogStream.System, $"Container {containerId[..12]} is up. Verifying health …");
            await SetStatus(DeploymentStatus.HealthChecking);

            // A worker answers no HTTP, forever, and that is correct behaviour — so it is checked for
            // being alive, not for replying. Only services that serve HTTP get the probe.
            var probePath = ServicePlan.HasHttpHealthCheck(app.Kind) ? app.HealthCheckPath : null;
            var health = await WaitForHealthyAsync(docker, upstreamHost, upstreamPort, containerName, probePath,
                msg => Log(LogStream.System, msg), ct);
            if (!health.IsHealthy)
                throw new InvalidOperationException(HealthDiagnosis.Explain(health, containerName));

            // New container is healthy → switch traffic to it, THEN retire the old container(s).
            if (ServicePlan.HasPublicTraffic(app.Kind))
                await WireProxyAsync(app, upstreamHost, upstreamPort, Log, Record, ct);
            else
                await Log(LogStream.System,
                    $"{app.Kind} service — no public route. " +
                    (ServicePlan.JoinsInternalNetwork(app.Kind)
                        ? $"Reachable inside this project at {containerName}."
                        : "Not reachable from other services."));
            await RetireOldContainersAsync(docker, app.WorkspaceId, app.Slug, keepContainerName: containerName, Log, ct);
            if (!server.IsLocal)
            {
                app.PublishedHostPort = publishPort;
                // Only now: releasing before the cutover would offer another app a port that is
                // still carrying this one's live traffic.
                await hostPorts.ReleaseAllButAsync(server.Id, app.Id, deployment.Number, ct);
            }

            // A rollback supersedes whatever was live: mark that deployment RolledBack so the
            // history shows which version was abandoned, not just which one replaced it.
            if (deployment.RolledBackFromId is not null)
                await MarkSupersededAsRolledBackAsync(app.ActiveDeploymentId, deployment.Id, Log, ct);

            app.ActiveDeploymentId = deployment.Id;
            app.Status = AppStatus.Running;
            await SetStatus(DeploymentStatus.Succeeded);
            await Log(LogStream.System, $"✅ Deployment #{deployment.Number} succeeded.");

            // The code in the image is now the code in the database, so the editor stops saying
            // "edited, not published" — the one sentence on that page a person acts on.
            await MarkFunctionsPublishedAsync(app, ct);
            await functionEvents.PublishAsync(
                Domain.Functions.FunctionEvent.Create(
                    Domain.Functions.FunctionEvents.DeploymentSucceeded, app.WorkspaceId, app.Slug,
                    ("app", app.Slug), ("deployment", deployment.Number.ToString()),
                    ("commit", deployment.CommitSha)),
                ct);

            // Only after the deployment is recorded as succeeded — pruning is housekeeping and must
            // never be able to turn a live, working deployment into a failure. Ordering is half of
            // that; the other half is the success-tail catch below, because this still logs on `ct`
            // and a deadline landing here escapes retention's own best-effort catch.
            await PruneOldImagesAsync(docker, app, Log, ct);
            await FlushLog();
        }
        // A deployment that reached Succeeded is never afterwards reported as failed. Stated once,
        // here, at the pipeline's own exit — not left for each thing that runs after the success
        // transition to remember, because the list grows and the cost of forgetting is the whole
        // point of this phase.
        //
        // The try block has one exit that is not a failure, and everything in it runs on a row the
        // database already records as Succeeded: the ✅ line, image retention, the flush. The first
        // two log through Log, which publishes on the work's own token, so a deadline firing in
        // that window throws out of a deployment that worked. The catch below would then stamp
        // ErrorMessage over the successful row, publish Failed to the page it had just told
        // Succeeded, and raise DeployFailed — a live status contradicting the stored one, which is
        // the sentence this phase exists to make false.
        //
        // And it would not stop at saying so: the container it removes is named for THIS deployment
        // number, which before the cutover is the container nothing owns yet and after it is the
        // release serving traffic. Reporting the deployment failed would also make it fail, minutes
        // after telling the user it was live.
        //
        // Retention's own failure is not swallowed, it is filed against retention: PruneOldImagesAsync
        // logs its warning to the host log, and the line below puts it on the deployment's page with
        // what it leaves behind. Silence would be a different lie.
        catch (Exception tailError) when (DeploymentStateMachine.IsSuccessful(deployment.Status))
        {
            logger.LogWarning(tailError,
                "Deployment {Id} succeeded; the housekeeping after it did not finish.", deploymentId);

            var whatStopped = LogText.Clean(redactor.Redact(tailError.Message, secrets));
            var tailLine = Stage(LogStream.System,
                $"⚠ Deployment #{deployment.Number} succeeded, but the housekeeping after it did " +
                $"not finish: {whatStopped} Superseded images may still be on the node.");
            // Durable first, and on None — the commonest way to arrive here is the token that would
            // refuse the save. This is also the flush FlushLog never reached, so it is what carries
            // the ✅ line and anything retention staged.
            await db.SaveChangesAsync(CancellationToken.None);
            // Then the surface that is not the row, guarded like the failure path's three: a hub
            // disposed under a finishing deploy must not cost a line that is already stored. No
            // status is published — the page was told Succeeded, and that is still true.
            await TellSomebody(
                () => stream.PublishLogAsync(deploymentId, LogStream.System, tailLine, CancellationToken.None),
                "deploy log");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deployment {Id} failed.", deploymentId);

            // NOTHING BELOW THIS LINE TAKES `ct`, and the reason is the same one for all of it.
            //
            // The commonest way to arrive here is that `ct` was cancelled: the job's own per-kind
            // deadline, which fires while the pipeline is inside a build, a pull or a health check.
            // Everything from here on is cleanup and record-keeping for work that has already
            // stopped — not more of the work — and passing it the token that stopped the work makes
            // each step throw on its first await, which also skips every step after it. That is one
            // `catch` block failing five different ways from a single cause.
            //
            // The line between the two is the one JobWorker.SettleAsync (:281) and
            // BackupSnapshotService (:279) already draw, and it is drawn at the `catch` rather than
            // inside any of the helpers: above it, a cancelled token must stop the deployment
            // dead — and still does, at every await in the `try`. Below it there is no deployment
            // left to stop, only what it left behind and what it owes the person who asked for it.

            // Zero-downtime guarantee: remove only the just-started (failed) container; the previous
            // version — if any — keeps serving untouched. TryRemoveContainerByNameAsync swallows
            // everything ("never mask the original failure"), so on the cancelled token this looked
            // exactly like a successful cleanup and left the container running.
            await TryRemoveContainerByNameAsync(
                docker, DeploymentPlanning.ContainerName(app.WorkspaceId, app.Slug, deployment.Number), CancellationToken.None);
            // The container is gone, so the port it reserved must go too — otherwise a node loses a
            // port to every failed deploy until the range runs out. The range is per-node and shared
            // by every app on it, so one app's repeatedly-timing-out builds drain it for everybody
            // else: one tenant's work freezing another's, which is the thing this phase exists to
            // stop rather than to introduce.
            try { await hostPorts.ReleaseAsync(app.ServerId, app.Id, deployment.Number, CancellationToken.None); }
            catch (Exception releaseError) { logger.LogWarning(releaseError, "Could not release the host port."); }
            // Failed is reachable from any in-flight state; guard against a double-terminal write.
            if (DeploymentStateMachine.IsInFlight(deployment.Status))
                DeploymentStateMachine.Transition(deployment, DeploymentStatus.Failed, clock.UtcNow);
            // Cleaned as well as redacted, and before it is stored rather than only on its way to the
            // log. A failure message quotes the failing command's own output, so it carries the same
            // unstorable bytes — and a write that throws here leaves the deployment unable to record
            // that it failed at all. Redacting the stored copy too: it is shown on the deployment
            // page, so a build error that echoes a secret would otherwise keep it in the database.
            var reason = LogText.Clean(redactor.Redact(ex.Message, secrets));
            deployment.ErrorMessage = reason;
            app.Status = app.ActiveDeploymentId is null ? AppStatus.Failed : AppStatus.Running;
            // The ❌ line is queued HERE, ahead of the save and ahead of every publish. It only ever
            // reached the database as a side effect of NotificationService writing a delivery
            // attempt back on this same scoped context — so a workspace with no rule matching
            // DeployFailed lost the one line that says why the deployment stopped, on every failure.
            // Putting it in front of the save that was already being made costs nothing and means
            // the row no longer depends on anything after this point working at all.
            var failureLine = Stage(LogStream.System, $"❌ Deployment failed: {reason}");
            // The durable half. Saving under the cancelled token threw before the row was written,
            // the transition above was dropped with the scope, and the deployment stayed in flight
            // for ever — and QueueDeploymentAsync coalesces onto an in-flight deployment, so every
            // later deploy of this app returned the abandoned id and ran nothing.
            await db.SaveChangesAsync(CancellationToken.None);

            // And the three surfaces a person actually watches, none of which that row reaches on
            // its own. The status the deployment page is bound to; the last line of the deploy log;
            // the alert that finds somebody who is not looking at the panel at all. They ran as one
            // unguarded sequence, so the first to fault skipped the rest — and a fault here is not
            // hypothetical: the host going down disposes the hub context under an in-flight deploy.
            // Each is now attempted on its own, so what can still be said, is.
            await TellSomebody(
                () => stream.PublishStatusAsync(deploymentId, DeploymentStatus.Failed, CancellationToken.None),
                "deployment page");
            await TellSomebody(
                () => stream.PublishLogAsync(deploymentId, LogStream.System, failureLine, CancellationToken.None),
                "deploy log");
            await TellSomebody(
                () => notifications.NotifyAsync(app.WorkspaceId, AlertEvent.DeployFailed, AlertSeverity.Critical,
                    $"Deploy failed: {app.Name} #{deployment.Number}", reason, CancellationToken.None),
                "alert rules");
            // A fourth surface, and the same rule as the three above it: on its own token-free call,
            // guarded, so a customer's event handler cannot cost this deployment its failure record.
            await TellSomebody(
                () => functionEvents.PublishAsync(
                    Domain.Functions.FunctionEvent.Create(
                        Domain.Functions.FunctionEvents.DeploymentFailed, app.WorkspaceId, app.Slug,
                        ("app", app.Slug), ("deployment", deployment.Number.ToString()), ("reason", reason)),
                    CancellationToken.None),
                "function subscribers");
        }
    }

    /// <summary>
    /// The private network for this service's environment, or null while it has none — an app
    /// created before projects existed and never reassigned. Null keeps it on the workspace network
    /// rather than inventing a boundary it was never placed inside.
    /// </summary>
    private async Task<string?> ResolveEnvironmentNetworkAsync(App app, CancellationToken ct)
    {
        if (app.EnvironmentId is not { } environmentId) return null;

        var placement = await db.Environments
            .Where(e => e.Id == environmentId)
            .Select(e => new { e.Slug, ProjectSlug = e.Project!.Slug })
            .FirstOrDefaultAsync(ct);

        return placement is null
            ? null
            : Networking.EnvironmentNetwork.For(placement.ProjectSlug, placement.Slug, environmentId);
    }

    /// <summary>
    /// Every short name already answered to on this app's network by somebody else.
    ///
    /// Two authorities, because neither holds both halves. Which apps share an environment is the
    /// database's fact. What their compose services are called exists only on the containers —
    /// ComposeFile is parsed from the repository at deploy time and never stored — so the
    /// harbora.compose.service label is the only place that name survives.
    ///
    /// Matched by <c>harbora.app.id</c>, not the <c>harbora.app</c> slug label: a slug is unique only
    /// per workspace (<c>HasIndex(WorkspaceId, Slug).IsUnique()</c>), but containers are listed
    /// host-wide, so a same-slugged app in an unrelated workspace would otherwise be mistaken for a
    /// sibling and cost this app its address over a collision that could never actually happen.
    ///
    /// Only a running container counts. <c>ListContainersAsync</c> lists every container regardless
    /// of state, and a stopped one holds no DNS record — nothing answers to its alias — so letting it
    /// block would deny a live app its name for ever over a corpse (Stop leaves containers behind
    /// rather than removing them).
    ///
    /// The database read sits inside the same guard as the Docker call: a failure to answer yields an
    /// empty set, which withholds nothing — the alias is registered and the deployment proceeds.
    /// Refusing a shortcut because either was briefly unreachable would trade a rare wrong-target risk
    /// for a common lost-feature one. A cancelled token is not "briefly unreachable" and passes
    /// through rather than being read as such.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> TakenAliasesAsync(IDockerEngine docker, App app, CancellationToken ct)
    {
        if (app.EnvironmentId is not { } environmentId) return [];

        try
        {
            var siblingIds = await db.Apps
                .Where(a => a.EnvironmentId == environmentId && a.Id != app.Id)
                .Select(a => a.Id.ToString())
                .ToListAsync(ct);

            if (siblingIds.Count == 0) return [];

            var containers = await docker.ListContainersAsync("harbora.compose.service", ct);
            return containers
                .Where(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase)
                    && c.Labels.TryGetValue("harbora.app.id", out var ownerId)
                    && siblingIds.Contains(ownerId))
                .Select(c => c.Labels["harbora.compose.service"])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read compose service names for {Slug}; registering its alias unchecked.", app.Slug);
            return [];
        }
    }

    private async Task<string> AcquireImageAsync(
        IDockerEngine docker, App app, Deployment deployment, string imageTag,
        IProgress<string> buildLog, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        // A pushed archive always wins, whatever the app's configured source is: the user just sent
        // this exact code and expects that deployed, not whatever a Git remote happens to hold.
        if (!string.IsNullOrWhiteSpace(deployment.SourceArchivePath))
            return await BuildFromUploadAsync(docker, app, deployment, imageTag, buildLog, log,
                                              forceStatic: app.SourceType == AppSourceType.StaticSite, ct);

        switch (app.SourceType)
        {
            case AppSourceType.PrebuiltImage:
                if (string.IsNullOrWhiteSpace(app.PrebuiltImage))
                    throw new InvalidOperationException("No image configured.");
                await log(LogStream.System, $"Pulling image {app.PrebuiltImage} …");
                await docker.PullImageAsync(app.PrebuiltImage, buildLog, ct);
                return app.PrebuiltImage;

            case AppSourceType.GitRepository:
            case AppSourceType.Dockerfile:
                return await BuildFromGitAsync(docker, app, deployment, imageTag, buildLog, log, forceStatic: false, ct);

            case AppSourceType.StaticSite:
                // A repo of static files served by Nginx — always use the generated static build,
                // ignoring any Dockerfile / stack detection.
                return await BuildFromGitAsync(docker, app, deployment, imageTag, buildLog, log, forceStatic: true, ct);

            case AppSourceType.Template:
            {
                var template = app.TemplateId is { } tid
                    ? await db.AppTemplates.FirstOrDefaultAsync(t => t.Id == tid, ct)
                    : null;
                if (template is null)
                    throw new InvalidOperationException("This app references a template that no longer exists.");

                var spec = TemplateResolver.Resolve(template.ManifestJson);
                switch (spec.Kind)
                {
                    case TemplateResolver.TemplateKind.Image:
                        await log(LogStream.System, $"Template '{template.Name}': pulling image {spec.Image} …");
                        await docker.PullImageAsync(spec.Image!, buildLog, ct);
                        return spec.Image!;

                    case TemplateResolver.TemplateKind.Git:
                        if (app.GitRepository is null)
                            throw new InvalidOperationException(
                                $"Template '{template.Name}' deploys from a Git repository — add a repository URL to the app, then redeploy.");
                        return await BuildFromGitAsync(docker, app, deployment, imageTag, buildLog, log, forceStatic: false, ct);

                    case TemplateResolver.TemplateKind.ManagedService:
                        throw new InvalidOperationException(
                            $"'{template.Name}' provisions a managed database/cache — create it from the Databases page, not as an app.");

                    default:
                        throw new InvalidOperationException(spec.Reason ?? "This template isn't one-click deployable yet.");
                }
            }

            case AppSourceType.Upload:
                // The app is upload-only and nothing was pushed — BuildFromUploadAsync explains how.
                return await BuildFromUploadAsync(docker, app, deployment, imageTag, buildLog, log, forceStatic: false, ct);

            case AppSourceType.InlineCode:
                return await BuildFromInlineCodeAsync(docker, app, deployment, imageTag, buildLog, log, ct);

            case AppSourceType.DockerCompose:
                // Multi-service Compose orchestration is planned (see docs/overhaul/12 P7+) but not
                // yet implemented; fail with a clear message rather than a raw NotSupported.
                throw new InvalidOperationException(
                    "Docker Compose deployments aren't supported yet. Deploy from a Git repo, Dockerfile, " +
                    "prebuilt image, static site or template for now.");

            default:
                throw new NotSupportedException($"Source type {app.SourceType} is not supported.");
        }
    }

    /// <summary>
    /// Checkout + build for Git/Dockerfile/StaticSite/Git-templates. When <paramref name="forceStatic"/>
    /// is set, always generate an Nginx static build regardless of any Dockerfile or detected stack.
    /// </summary>
    private async Task<string> BuildFromGitAsync(
        IDockerEngine docker, App app, Deployment deployment, string imageTag,
        IProgress<string> buildLog, Func<LogStream, string, Task> log, bool forceStatic, CancellationToken ct)
    {
        if (app.GitRepository is null)
            throw new InvalidOperationException("No Git repository linked.");

        var token = app.GitRepository.Provider?.EncryptedCredential is { } enc && enc.Length > 0
            ? SafeUnprotect(enc) : null;
        var workDir = Path.Combine(_opt.WorkDir, app.Slug, deployment.Number.ToString());
        var gitRef = deployment.GitRef ?? app.GitRepository.DefaultBranch;

        await log(LogStream.System, $"Checking out {app.GitRepository.FullName}@{gitRef} …");
        var checkout = await git.CheckoutAsync(app.GitRepository.CloneUrl, gitRef, token, workDir, buildLog, ct);

        deployment.CommitSha = checkout.CommitSha;
        deployment.CommitMessage = checkout.CommitMessage;
        deployment.CommitAuthor = checkout.CommitAuthor;

        return await BuildFromSourceAsync(docker, app, deployment, imageTag, checkout.LocalPath,
                                          buildLog, log, forceStatic, ct);
    }

    /// <summary>
    /// Materialises the source, reads its compose file and validates it. Runs before anything is
    /// built or started so an unsupported directive rejects the deployment cleanly instead of
    /// leaving half a stack running.
    /// </summary>
    private async Task<ComposeParseResult> LoadComposeAsync(
        App app, Deployment deployment, IProgress<string> buildLog,
        Func<LogStream, string, Task> log, CancellationToken ct)
    {
        var sourceRoot = await MaterialiseSourceAsync(app, deployment, buildLog, log, ct);
        var candidates = string.IsNullOrWhiteSpace(app.ComposeFilePath)
            ? new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" }
            : [app.ComposeFilePath!];

        var path = candidates
            .Select(c => Path.Combine(sourceRoot, c))
            .FirstOrDefault(File.Exists);

        if (path is null)
            throw new InvalidOperationException(
                $"No compose file found in the source (looked for {string.Join(", ", candidates)}).");

        await log(LogStream.System, $"Reading {Path.GetFileName(path)} …");
        var stack = ComposeFile.Parse(await File.ReadAllTextAsync(path, ct));

        // Surface warnings before deciding: the operator should see what was ignored even on success.
        foreach (var warning in stack.Warnings)
            await log(LogStream.System, $"⚠ {warning}");

        if (!stack.IsValid)
        {
            foreach (var error in stack.Errors)
                await log(LogStream.System, $"✗ {error}");
            throw new InvalidOperationException(
                "The compose file uses directives Harbora can't run safely: " + string.Join(" ", stack.Errors));
        }

        // Where each service's build context lives, resolved once.
        foreach (var service in stack.Services)
            if (service.Build is not null)
                service.Build = Path.Combine(sourceRoot, service.Build.TrimStart('.', '/', '\\'));

        await log(LogStream.System,
            $"Stack: {string.Join(", ", stack.Services.Select(s => s.Name))} " +
            $"(web = {stack.Web!.Name}:{stack.Web.Port}).");
        return stack;
    }

    /// <summary>
    /// Builds/pulls every service and starts the whole stack under versioned names, alongside
    /// whatever is currently running. Returns the service the proxy should target.
    /// </summary>
    private async Task<(string ServiceName, string ContainerName, int Port, IReadOnlyCollection<string> AllContainerNames)>
        StartComposeStackAsync(
            IDockerEngine docker, App app, Deployment deployment, ComposeParseResult stack,
            string network, Dictionary<string, string> labels, Domain.Servers.Server server,
            IProgress<string> buildLog, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        var started = new List<string>();

        // Dependencies first, so a service that needs its database finds it already running. Not a
        // full topological sort: compose's depends_on doesn't wait for readiness either, and the
        // health gate below is what actually decides whether the stack works.
        foreach (var service in stack.Services.OrderByDescending(s => s.DependsOn.Count == 0))
        {
            var containerName = DeploymentPlanning.ComposeContainerName(app.WorkspaceId, app.Slug, service.Name, deployment.Number);

            string image;
            if (service.Build is not null)
            {
                image = $"{_opt.ImagePrefix}/{app.Slug}-{service.Name}:build-{deployment.Number}";
                var dockerfile = service.Dockerfile ?? "Dockerfile";
                if (!File.Exists(Path.Combine(service.Build, dockerfile)))
                {
                    var pack = Buildpacks.Detect(service.Build, service.Port ?? app.ContainerPort);
                    if (pack is null)
                        throw new InvalidOperationException(
                            $"Service '{service.Name}' builds from '{service.Build}' but has no Dockerfile " +
                            "and no recognisable stack.");
                    dockerfile = "Dockerfile.harbora";
                    await File.WriteAllTextAsync(Path.Combine(service.Build, dockerfile), pack.Value.Dockerfile, ct);
                    await log(LogStream.System, $"Service '{service.Name}': detected {pack.Value.Stack}.");
                }

                await log(LogStream.System, $"Building {service.Name} → {image} …");
                image = await docker.BuildImageAsync(
                    new DockerBuildRequest(service.Build, dockerfile, image, new Dictionary<string, string>()),
                    buildLog, ct);
            }
            else
            {
                image = service.Image!;
                await log(LogStream.System, $"Pulling {image} for '{service.Name}' …");
                await docker.PullImageAsync(image, buildLog, ct);
            }

            foreach (var (volume, _) in service.Volumes)
                await docker.EnsureVolumeAsync(VolumeNameFor(app, volume), ct);

            // The app's own env vars apply to every service, with the compose file's values winning
            // for that service — the file is the more specific statement.
            var env = BuildEnv(app);
            foreach (var (key, value) in service.Environment) env[key] = value;

            env.TryAdd("HARBORA_SERVICE", service.Name);

            // Under compose, services reach each other by service name. Two deployments of the same
            // app coexist during a cutover, so the alias is scoped per deployment as well: the bare
            // name resolves within a stack, and the versioned one is unambiguous across stacks.
            var aliases = new List<string> { service.Name, $"{service.Name}-{deployment.Number}" };

            // Also export SERVICE_HOST for each sibling, for stacks that prefer configuration over
            // DNS conventions.
            foreach (var sibling in stack.Services)
                env.TryAdd($"{Sanitize(sibling.Name).ToUpperInvariant()}_HOST", sibling.Name);

            var serviceLabels = new Dictionary<string, string>(labels) { ["harbora.compose.service"] = service.Name };

            // Only the web service needs a published host port, and only on a remote node.
            // Same reservation as the single-container path: one port per deployment, held for the
            // web service. AllocateAsync returns the existing one if this deployment already has it.
            int? publish = service.IsWeb && !server.IsLocal
                ? await hostPorts.AllocateAsync(server.Id, app.Id, deployment.Number, ct)
                : null;

            await log(LogStream.System, $"Starting {containerName} …");
            await docker.RunContainerAsync(new DockerRunRequest(
                image, containerName, network, env, serviceLabels,
                service.Volumes.Select(v => (VolumeNameFor(app, v.Volume), v.MountPath, false)).ToList(),
                service.Port, app.MemoryLimitBytes, app.CpuLimit,
                service.IsWeb ? app.HealthCheckPath : null,
                Command: service.Command.Count > 0 ? service.Command : null,
                PublishToHostPort: publish,
                NetworkAliases: aliases), ct);

            started.Add(containerName);

            // Aliases would be the proper way for services to resolve each other by bare name; until
            // the engine exposes them, the versioned name is what works and is what we route to.
            if (service.IsWeb)
                await log(LogStream.System, $"'{service.Name}' will receive inbound traffic.");
        }

        var web = stack.Web!;
        return (web.Name,
                DeploymentPlanning.ComposeContainerName(app.WorkspaceId, app.Slug, web.Name, deployment.Number),
                web.Port ?? app.ContainerPort,
                started);
    }

    /// <summary>
    /// Compose volume names are namespaced per app, so two tenants both using "pgdata" don't share
    /// one volume.
    /// </summary>
    private static string VolumeNameFor(App app, string composeVolume) => $"harbora-{app.Slug}-{composeVolume}";

    /// <summary>Service names become env var names, so anything not alphanumeric becomes '_'.</summary>
    private static string Sanitize(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>
    /// Gets the source on disk for whichever way this app supplies it, without building anything.
    /// Shared by the compose path, which has to read a file from the tree before it can plan.
    /// </summary>
    private async Task<string> MaterialiseSourceAsync(
        App app, Deployment deployment, IProgress<string> buildLog,
        Func<LogStream, string, Task> log, CancellationToken ct)
    {
        var workDir = Path.Combine(_opt.WorkDir, app.Slug, deployment.Number.ToString());

        if (!string.IsNullOrWhiteSpace(deployment.SourceArchivePath))
        {
            if (!File.Exists(deployment.SourceArchivePath))
                throw new InvalidOperationException("The uploaded source archive is missing.");
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);

            await log(LogStream.System, "Unpacking uploaded source …");
            await using var stream = File.OpenRead(deployment.SourceArchivePath);
            await SourceArchive.ExtractAsync(stream, workDir, ct);
            try { File.Delete(deployment.SourceArchivePath); } catch { /* best effort */ }
            return workDir;
        }

        if (app.GitRepository is null)
            throw new InvalidOperationException(
                "This app has no source: link a Git repository, or push one with `harbora deploy`.");

        var token = app.GitRepository.Provider?.EncryptedCredential is { } enc && enc.Length > 0
            ? SafeUnprotect(enc) : null;
        var gitRef = deployment.GitRef ?? app.GitRepository.DefaultBranch;

        await log(LogStream.System, $"Checking out {app.GitRepository.FullName}@{gitRef} …");
        var checkout = await git.CheckoutAsync(app.GitRepository.CloneUrl, gitRef, token, workDir, buildLog, ct);

        deployment.CommitSha = checkout.CommitSha;
        deployment.CommitMessage = checkout.CommitMessage;
        deployment.CommitAuthor = checkout.CommitAuthor;
        return checkout.LocalPath;
    }

    /// <summary>
    /// Unpacks the archive pushed by <c>harbora deploy</c> and builds from it. Same build rules as a
    /// Git checkout — the only difference is how the source got here.
    /// </summary>
    private async Task<string> BuildFromUploadAsync(
        IDockerEngine docker, App app, Deployment deployment, string imageTag,
        IProgress<string> buildLog, Func<LogStream, string, Task> log, bool forceStatic, CancellationToken ct)
    {
        var archive = deployment.SourceArchivePath;
        if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
            throw new InvalidOperationException(
                "This app deploys from code pushed with `harbora deploy`, but no source archive was " +
                "uploaded for this deployment. Run `harbora deploy` from your project folder.");

        var workDir = Path.Combine(_opt.WorkDir, app.Slug, deployment.Number.ToString());
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);

        await log(LogStream.System, "Unpacking uploaded source …");
        await using (var stream = File.OpenRead(archive))
        {
            var result = await SourceArchive.ExtractAsync(stream, workDir, ct);
            await log(LogStream.System,
                $"Unpacked {result.Files} entries ({result.Bytes / 1024 / 1024.0:0.#} MB).");
        }

        // The upload is consumed; keeping it would double the disk cost of every deployment.
        try { File.Delete(archive); } catch { /* best effort */ }

        return await BuildFromSourceAsync(docker, app, deployment, imageTag, workDir,
                                          buildLog, log, forceStatic, ct);
    }

    /// <summary>
    /// Clears the "edited since it was published" flag on a function app's functions.
    ///
    /// <para>
    /// Only on success, and only for the rows as they were when the build started — a function edited
    /// while this deployment was building has genuinely not been published, and marking it clean here
    /// would tell the person their unsaved-to-production change is live.
    /// </para>
    /// </summary>
    private async Task MarkFunctionsPublishedAsync(App app, CancellationToken ct)
    {
        if (app.SourceType != AppSourceType.InlineCode || _codeReadAt is not { } readAt) return;

        var functions = await db.FunctionDefinitions.IgnoreQueryFilters()
            .Where(f => f.AppId == app.Id && f.HasUnpublishedChanges)
            .ToListAsync(ct);

        foreach (var fn in functions.Where(f => f.UpdatedAt <= readAt))
            fn.HasUnpublishedChanges = false;
    }

    /// <summary>
    /// The instant this deployment read the code it built, so an edit that arrived while the image
    /// was building can be told from one that is in it. Null until a function app is built.
    /// </summary>
    private DateTimeOffset? _codeReadAt;

    /// <summary>
    /// Writes a function app's rows out as a source tree and builds it.
    ///
    /// <para>
    /// The whole of "code typed into the panel, running in a container" is this method plus
    /// <see cref="Functions.FunctionProject"/>: once the files exist, this takes the identical path a
    /// Git checkout does, so a function app health-checks, cuts over, rolls back and streams logs
    /// with no code that knows what a function is.
    /// </para>
    /// </summary>
    private async Task<string> BuildFromInlineCodeAsync(
        IDockerEngine docker, App app, Deployment deployment, string imageTag,
        IProgress<string> buildLog, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        if (app.FunctionRuntime is not { } runtime)
            throw new InvalidOperationException(
                "This function app has no runtime. Delete it and create it again choosing C#, JavaScript or Python.");

        // Unfiltered: the pipeline runs on the job worker with no session, and a filtered read here
        // would find no functions and publish an empty host that answers 404 to everything.
        _codeReadAt = clock.UtcNow;
        var functions = await db.FunctionDefinitions.IgnoreQueryFilters()
            .Where(f => f.AppId == app.Id)
            .ToListAsync(ct);

        if (functions.Count == 0)
            throw new InvalidOperationException(
                "This function app has no functions yet. Add one on the app's Code tab, then publish.");

        var workDir = Path.Combine(_opt.WorkDir, app.Slug, deployment.Number.ToString());
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        Directory.CreateDirectory(workDir);

        var port = app.ContainerPort <= 0 ? Functions.FunctionProject.DefaultPort : app.ContainerPort;
        var generated = Functions.FunctionProject.Generate(runtime, functions, port);
        foreach (var file in generated)
        {
            var target = Path.Combine(workDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, file.Content, ct);
        }

        await log(LogStream.System,
            $"Generated a {runtime} host for {functions.Count} function(s).");

        // forceStatic is false and the generated context always contains Dockerfile.harbora, which
        // the app's DockerfilePath names — so stack detection is never consulted for a function app.
        return await BuildFromSourceAsync(docker, app, deployment, imageTag, workDir,
                                          buildLog, log, forceStatic: false, ct);
    }

    /// <summary>
    /// Everything after the source exists on disk: pick the Dockerfile (or generate one from a
    /// detected stack) and build the image. Shared by the Git and upload paths so both get identical
    /// build behaviour.
    /// </summary>
    private async Task<string> BuildFromSourceAsync(
        IDockerEngine docker, App app, Deployment deployment, string imageTag, string sourceRoot,
        IProgress<string> buildLog, Func<LogStream, string, Task> log, bool forceStatic, CancellationToken ct)
    {
        var contextPath = Path.Combine(sourceRoot, app.BuildContextPath?.TrimStart('.', '/', '\\') ?? "");
        if (!Directory.Exists(contextPath)) contextPath = sourceRoot;

        string dockerfile;
        if (forceStatic)
        {
            dockerfile = "Dockerfile.harbora";
            await File.WriteAllTextAsync(Path.Combine(contextPath, dockerfile), Buildpacks.ForStaticSite().Dockerfile, ct);
            await log(LogStream.System, "Static site — using a generated Nginx build.");
        }
        else
        {
            // Use the repo's Dockerfile if present; otherwise auto-detect the stack (buildpack).
            dockerfile = app.DockerfilePath ?? "Dockerfile";
            if (!File.Exists(Path.Combine(contextPath, dockerfile)) &&
                File.Exists(Path.Combine(contextPath, "Dockerfile.harbora")))
            {
                // An inline Dockerfile the client supplied (harbora.yml `dockerfileLines`). It is an
                // explicit instruction, so it outranks stack detection.
                dockerfile = "Dockerfile.harbora";
                await log(LogStream.System, "Using the Dockerfile supplied in harbora.yml.");
            }
            else if (!File.Exists(Path.Combine(contextPath, dockerfile)))
            {
                var pack = Buildpacks.Detect(contextPath, app.ContainerPort);
                if (pack is null)
                    throw new InvalidOperationException(
                        "No Dockerfile found and the stack couldn't be auto-detected. Add a Dockerfile, or deploy a prebuilt image / template.");

                dockerfile = "Dockerfile.harbora";
                await File.WriteAllTextAsync(Path.Combine(contextPath, dockerfile), pack.Value.Dockerfile, ct);
                await log(LogStream.System, $"No Dockerfile — auto-detected {pack.Value.Stack}; using a generated build.");
            }
        }

        await log(LogStream.System, $"Building image {imageTag} …");
        var buildArgs = app.EnvironmentVariables
            .Where(e => e.AvailableAtBuild)
            .ToDictionary(e => e.Key, e => e.IsSecret ? SafeUnprotect(e.Value) : e.Value);

        return await docker.BuildImageAsync(
            new DockerBuildRequest(contextPath, dockerfile, imageTag, buildArgs), buildLog, ct);
    }

    private Dictionary<string, string> BuildEnv(App app)
    {
        var env = app.EnvironmentVariables.ToDictionary(
            e => e.Key,
            e => e.IsSecret ? SafeUnprotect(e.Value) : e.Value);

        // A function host refuses an unsigned invocation, so the secret has to reach the container —
        // and it is injected here rather than stored as an ordinary variable so nobody can rename,
        // reveal or delete it from the environment page and lock the scheduler out of its own app.
        if (app.SourceType == AppSourceType.InlineCode && app.FunctionInvokeSecret is { Length: > 0 } secret)
            env[Functions.FunctionProject.SecretEnvVar] = SafeUnprotect(secret);

        return env;
    }

    /// <summary>
    /// After a rollback cuts over, flag the deployment it displaced as <see cref="DeploymentStatus.RolledBack"/>.
    /// Only a Succeeded deployment can be superseded this way, and never the rollback itself.
    /// </summary>
    private async Task MarkSupersededAsRolledBackAsync(
        Guid? supersededId, Guid currentDeploymentId, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        if (supersededId is not { } id) return;

        var superseded = await db.Deployments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (superseded is null) return;
        if (!DeploymentPlanning.ShouldMarkRolledBack(superseded, currentDeploymentId)) return;

        DeploymentStateMachine.Transition(superseded, DeploymentStatus.RolledBack, clock.UtcNow);
        await log(LogStream.System, $"Deployment #{superseded.Number} marked rolled back.");
    }

    /// <summary>
    /// Delete this app's superseded build images, keeping the active one and the newest
    /// <see cref="HarboraRuntimeOptions.ImageRetentionCount"/> rollback targets.
    ///
    /// Without this, every deploy leaves an image on disk forever. With it, retention becomes an
    /// explicit promise: rollback reaches exactly as far back as the retained images, and the depth
    /// is configurable rather than "however long until someone runs docker image prune".
    /// Entirely best-effort — a failure here is logged and never touches the deployment's outcome.
    /// The catch below cannot make that true on its own: it logs through the pipeline's own logger,
    /// which publishes on the work's token, so a cancellation raised by the log call is one this
    /// catch cannot swallow. What keeps the promise is the pipeline's success-tail catch, which
    /// refuses to report a failure for a deployment already recorded as succeeded.
    /// </summary>
    private async Task PruneOldImagesAsync(
        IDockerEngine docker, App app, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        if (_opt.ImageRetentionCount <= 0) return;

        try
        {
            var prefix = DeploymentPlanning.BuildImagePrefix(_opt.ImagePrefix, app.Slug);
            var onNode = await docker.ListImagesAsync(prefix, ct);
            var history = await db.Deployments.Where(d => d.AppId == app.Id).ToListAsync(ct);

            var prunable = DeploymentPlanning.ImagesToPrune(
                onNode, history, app.ActiveDeploymentId, _opt.ImagePrefix, app.Slug, _opt.ImageRetentionCount);
            if (prunable.Count == 0) return;

            foreach (var tag in prunable)
                await docker.RemoveImageAsync(tag, ct);

            await log(LogStream.System,
                $"Retention: removed {prunable.Count} superseded image(s); keeping the newest " +
                $"{_opt.ImageRetentionCount} for rollback.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Image retention failed for app {Slug}.", app.Slug);
            await log(LogStream.System, $"(image cleanup skipped: {ex.Message})");
        }
    }

    /// <summary>
    /// Retire this app's previous container(s) after a successful cutover — everything labelled for
    /// the app except the just-deployed container (incl. any legacy unversioned container).
    /// </summary>
    private Task RetireOldContainersAsync(
        IDockerEngine docker, Guid workspaceId, string slug, string keepContainerName,
        Func<LogStream, string, Task> log, CancellationToken ct) =>
        RetireOldContainersAsync(docker, workspaceId, slug, new[] { keepContainerName }, log, ct);

    /// <summary>Stack form: keeps every container of the new set (see <c>ContainersToRetire</c>).</summary>
    private async Task RetireOldContainersAsync(
        IDockerEngine docker, Guid workspaceId, string slug, IReadOnlyCollection<string> keepContainerNames,
        Func<LogStream, string, Task> log, CancellationToken ct)
    {
        // The legacy bridge's other half (DeploymentPlanning.OwnedByThisWorkspace): a real query, not
        // a hardcoded true, so an install where the platform-wide slug index could not apply (a
        // pre-existing duplicate — see the migration) still gets a real answer instead of a
        // rubber-stamped retirement of a container that might not be this workspace's.
        var slugExclusive = !await db.Apps.IgnoreQueryFilters()
            .AnyAsync(a => a.Slug == slug && a.WorkspaceId != workspaceId, ct);

        var existing = await docker.ListContainersAsync(DeploymentPlanning.AppLabel, ct);
        var toRetire = DeploymentPlanning.ContainersToRetire(existing, workspaceId, slug, keepContainerNames, slugExclusive);
        foreach (var id in toRetire)
        {
            await log(LogStream.System, $"Retiring previous container {id[..12]} …");
            try { await docker.RemoveContainerAsync(id, force: true, ct); }
            catch (Exception ex) { await log(LogStream.System, $"(could not remove {id[..12]}: {ex.Message})"); }
        }
    }

    /// <summary>Best-effort removal of a single container by exact name (used to clean up a failed deploy).</summary>
    private static async Task TryRemoveContainerByNameAsync(IDockerEngine docker, string containerName, CancellationToken ct)
    {
        try
        {
            var existing = await docker.ListContainersAsync(DeploymentPlanning.AppLabel, ct);
            var match = existing.FirstOrDefault(c => c.Name == containerName);
            if (match is not null) await docker.RemoveContainerAsync(match.Id, force: true, ct);
        }
        catch { /* best effort — never mask the original failure */ }
    }

    /// <summary>
    /// Health gate: first wait for the container to reach "running" (fail fast if it exits). Then,
    /// for a local-server app with a health path, HTTP-probe it over the shared harbora network
    /// until it returns a success status. Remote nodes fall back to liveness (the panel can't reach
    /// their containers by name without an overlay network).
    /// </summary>
    private async Task<HealthReport> WaitForHealthyAsync(
        IDockerEngine docker, string upstreamHost, int upstreamPort, string containerName, string? healthPath,
        Func<string, Task> log, CancellationToken ct)
    {
        ContainerInfo? last = null;
        var running = false;
        for (var i = 0; i < _opt.HealthRunningAttempts && !running; i++)
        {
            await Task.Delay(_opt.HealthPollInterval, ct);
            var c = (await docker.ListContainersAsync("harbora.app", ct)).FirstOrDefault(x => x.Name == containerName);
            if (c is null) return new HealthReport(HealthFailure.Vanished);
            last = c;
            if (CrashFailure(c) is { } crash) return await FailedAsync(docker, crash, c, ct);
            running = c.State.Equals("running", StringComparison.OrdinalIgnoreCase);
        }
        if (!running) return await FailedAsync(docker, HealthFailure.NeverStarted, last, ct);

        if (string.IsNullOrWhiteSpace(healthPath))
            return HealthReport.Healthy;

        // Probe the same address the proxy will use: container name on the local network, or the
        // node's host:publishedPort for a remote node.
        var url = $"http://{upstreamHost}:{upstreamPort}/{healthPath.TrimStart('/')}";
        await log($"HTTP health check → {url}");
        var client = httpFactory.CreateClient();
        client.Timeout = _opt.HealthHttpTimeout;

        for (var attempt = 0; attempt < _opt.HealthHttpAttempts; attempt++)
        {
            try
            {
                using var res = await client.GetAsync(url, ct);
                var status = (int)res.StatusCode;
                if (HealthProbeRule.Accepts(healthPath, status))
                {
                    if (HealthProbeRule.ExplainAcceptance(healthPath, status) is { } note)
                        await log($"Health check → {url} {note}");
                    return HealthReport.Healthy;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // App still booting / not accepting connections yet — keep trying.
            }
            await Task.Delay(_opt.HealthPollInterval, ct);
        }
        await log("Health check did not pass within the timeout.");

        // Re-read the container: an app that fails its health path has often died in the meantime,
        // and the crash is a far more useful thing to report than the unanswered probe.
        var current = (await docker.ListContainersAsync("harbora.app", ct)).FirstOrDefault(x => x.Name == containerName);
        if (current is not null && CrashFailure(current) is { } lateCrash)
            return await FailedAsync(docker, lateCrash, current, ct);

        return await FailedAsync(docker, HealthFailure.NoHealthyResponse, current, ct, url);
    }

    /// <summary>
    /// Runs the app's release command, if it has one. Throws on a non-zero exit so the caller's
    /// failure path — which leaves the previous container untouched — handles it like any other.
    /// </summary>
    private async Task RunReleaseTaskAsync(
        IDockerEngine docker, App app, string imageTag, string network,
        IReadOnlyDictionary<string, string> env, Func<LogStream, string, Task> log,
        Func<LogStream, string, Task> logFromEngine, CancellationToken ct)
    {
        var command = app.ReleaseCommand?.Trim();
        if (string.IsNullOrEmpty(command)) return;

        await log(LogStream.System, $"Release task: {command}");

        var output = new System.Text.StringBuilder();

        // Bounded, so a command that waits for input cannot leave the deployment in progress for
        // ever. The linked source keeps a real cancellation (the user pressing stop) distinguishable
        // from the timeout below.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_opt.ReleaseTaskTimeout);

        int exit;
        try
        {
            exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
                imageTag,
                // Through a shell so an ordinary command line works, rather than only an exec-form array.
                ["sh", "-c", command],
                [],
                Env: env,
                NetworkMode: network),
                // Reported inline, and through the engine-safe logger. Progress<T> hands the
                // callback to the thread pool, so the tail could still be empty when the failure
                // message below is built — a command that printed plenty would be reported as
                // having produced no output. Running inline also means this callback arrives on
                // the engine's own thread, where only the queueing logger may be used: the
                // pipeline's logger writes to the DbContext, which is not thread-safe.
                new InlineProgress<string>(line =>
                {
                    lock (output) output.AppendLine(line);
                    _ = logFromEngine(LogStream.Build, line);
                }), limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"The release task was still running after {_opt.ReleaseTaskTimeout.TotalMinutes:0} minutes " +
                $"and was given up on, so this version was not released and the current one is still serving.");
        }
        catch (Exception ex) when (ex.Message.Contains("executable file not found", StringComparison.OrdinalIgnoreCase))
        {
            // Scratch and distroless images have no shell, so there is nothing to run the command
            // line with. Worth saying plainly: the raw Docker error blames "sh", which reads like a
            // fault in the command rather than in the choice of base image.
            throw new InvalidOperationException(
                "The release task could not start because this image has no shell, so there is nothing " +
                "to run the command with. Use a base image that includes /bin/sh, or clear the release " +
                "command. This version was not released and the current one is still serving.");
        }

        if (exit != 0)
        {
            var tail = output.ToString().Trim();
            if (tail.Length > 600) tail = "…" + tail[^600..];

            throw new InvalidOperationException(
                $"The release task failed (exit {exit}), so this version was not released and the " +
                $"current one is still serving." +
                (tail.Length > 0 ? $" Its last output was: {tail}" : " It produced no output."));
        }

        await log(LogStream.System, "Release task finished.");
    }

    /// <summary>
    /// Reconciles the app's configured port with the one the image declares.
    ///
    /// The app is updated when they disagree, so the panel stops showing a number that cannot work —
    /// a Details page promising port 80 for a container listening on 8080 is its own small lie.
    /// </summary>
    private async Task<int> ResolveContainerPortAsync(
        IDockerEngine docker, App app, string imageTag, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        IReadOnlyList<int> exposed;
        try { exposed = await docker.GetImagePortsAsync(imageTag, ct); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the ports for {Image}.", imageTag);
            return app.ContainerPort;
        }

        var choice = PortSelection.Choose(app.ContainerPort, exposed);
        if (!choice.Changed) return choice.Port;

        await log(LogStream.System, $"Port: {choice.Reason}.");
        app.ContainerPort = choice.Port;
        return choice.Port;
    }

    /// <summary>
    /// Whether the container is dead or dying, and in which way.
    ///
    /// App containers run under <c>unless-stopped</c>, so a container that crashes on startup is
    /// revived by Docker within moments: in practice it reports "restarting", and almost never
    /// "exited". Watching only for "exited" is why a crash-looping app was reported as a container
    /// that was "running but never answered" — the opposite of what was happening.
    /// </summary>
    private static HealthFailure? CrashFailure(ContainerInfo c) =>
        c.State.Equals("exited", StringComparison.OrdinalIgnoreCase) ? HealthFailure.Exited
        : c.State.Equals("restarting", StringComparison.OrdinalIgnoreCase) ? HealthFailure.CrashLooping
        : null;

    /// <summary>
    /// Collects the container's own account of what happened, while it still exists — the failure
    /// path removes it moments later.
    /// </summary>
    private static async Task<HealthReport> FailedAsync(
        IDockerEngine docker, HealthFailure failure, ContainerInfo? container, CancellationToken ct,
        string? probeUrl = null)
    {
        string? tail = null;
        if (container is not null)
        {
            try { tail = await docker.GetLogsAsync(container.Id, 30, ct); }
            catch { /* diagnostics must never replace the failure they describe */ }
        }
        return new HealthReport(failure, container?.Status, tail, probeUrl);
    }

    /// <summary>
    /// Materialise a Route per domain then re-apply the whole platform's proxy config.
    ///
    /// <para>
    /// Takes two loggers. <paramref name="log"/> is the work's, on the deployment's own token, and
    /// describes wiring that is still being attempted. <paramref name="record"/> is the same line
    /// written once the work has stopped, and only the revert path below uses it — see
    /// <see cref="RepublishRoutingAsync"/>.
    /// </para>
    /// </summary>
    private async Task WireProxyAsync(
        App app, string upstreamHost, int upstreamPort,
        Func<LogStream, string, Task> log, Func<LogStream, string, Task> record, CancellationToken ct)
    {
        if (app.Domains.Count == 0)
        {
            await log(LogStream.System, "No domains attached; skipping proxy wiring.");
            return;
        }

        // Saved BEFORE the apply, and it has to be: the config below is rendered from the stored
        // routes of the whole platform, so a route added for a domain's first deployment would not
        // be in it otherwise, and the deployment would publish a config missing the very route it
        // exists for. The cost is that from here until this method returns, the rows describe a
        // container that may not survive — and every other caller (RoutesController, AppsController,
        // AdminerService, AppOperationsService) renders from those same rows, so anyone else's apply
        // in that window publishes this deployment's upstream on its behalf. So: keep what each row
        // said, put it back if anything below refuses, and re-publish from them once it has been put
        // back, rather than leaving a dead upstream live until somebody happens to apply again.
        var undo = new List<RouteRevert>();
        foreach (var domain in app.Domains)
        {
            var route = await db.Routes.FirstOrDefaultAsync(r => r.AppId == app.Id && r.Host == domain.Host, ct);
            var isNew = route is null;
            if (route is null)
            {
                route = new Route { WorkspaceId = app.WorkspaceId, AppId = app.Id, Host = domain.Host };
                db.Routes.Add(route);
            }
            // Only the first sight of a row is what that row said. Two domains resolving to one
            // route send the second pass through here back the instance the first has already
            // rewritten, and capturing it again would record this deployment's own new upstream as
            // the thing to go back to — the revert would then "restore" the container it is about
            // to remove, which is the failure it exists to prevent, arriving through its own
            // bookkeeping. Domains.Host is unique in the schema, so that pair cannot be made through
            // the panel today; what the revert holds is now re-published to the live config the
            // moment a cutover fails, and that is not a thing to leave resting on an index in
            // another table.
            if (undo.All(u => !ReferenceEquals(u.Route, route)))
                undo.Add(new RouteRevert(route, isNew, route.TargetService, route.TargetPort,
                    route.SslEnabled, route.RedirectHttpToHttps, route.IsEnabled));
            route.TargetService = upstreamHost;
            route.TargetPort = upstreamPort;
            route.SslEnabled = domain.SslEnabled;
            route.RedirectHttpToHttps = domain.ForceHttps;
            route.IsEnabled = true;
        }
        await db.SaveChangesAsync(ct);

        try
        {
            var result = await proxy.ApplyAllAsync(app.WorkspaceId, ct);

            // A deployment whose routing did not apply has not deployed. This used to log a warning
            // and carry on to "Succeeded", which is the one thing the platform promises never to do:
            // the container was up, so nothing looked wrong, and traffic was still on the old
            // upstream or on nothing at all. Throwing hands it to the pipeline's own failure path,
            // which raises DeployFailed, records the reason, and removes only the container just
            // started — the previous release is still running, because nothing is retired until
            // after this step.
            if (!result.Success)
                throw new InvalidOperationException(ProxyDiagnosis.ExplainApplyFailure(result));

            await log(LogStream.System, "Proxy configuration applied.");

            if (_opt.VerifyThroughProxy)
                await VerifyThroughProxyAsync(app, log, ct);
        }
        catch
        {
            // Put the rows back, then put the file back from them. Both steps, on every failure path
            // here, because on none of them can the live config be assumed to still describe what is
            // running:
            //
            //  - A refused apply may well have changed it. The engine leaves an invalid route out of
            //    the render rather than refusing the whole platform's file, so a refusal means our
            //    own row was dropped from a config that was published — with this app's other
            //    domains in it, naming the container the failure path is about to remove.
            //  - An apply that reported RolledBack: false did not put it back either, which
            //    ProxyDiagnosis.ExplainApplyFailure says correctly in the message thrown just above
            //    ("it may no longer match what is running") and this comment used to deny outright.
            //  - A refused verification applied cleanly first, so the live config names the new
            //    container outright.
            //  - And between the save above and the apply, any other caller's apply — a route edit,
            //    an Adminer session, another tenant's deployment — publishes these rows as it finds
            //    them, which is this deployment's new upstream. Reverting the rows alone leaves that
            //    published and waits for somebody else to happen to re-apply.
            //
            // So the revert is followed by an apply of its own. That second apply is now worth
            // making, which it was not before: an apply can no longer be refused by an unrelated
            // tenant's bad row, so re-publishing the routing this deployment found is a thing that
            // reliably happens rather than a thing that might be turned away.
            await RevertRoutesAsync(undo);
            await RepublishRoutingAsync(app, record);
            throw;
        }
    }

    /// <summary>
    /// Publish the platform's routing again from the rows as this deployment left them — which,
    /// having just reverted, is the routing it found. Best effort by design: this runs while a
    /// failure is on its way up, and a failure here must never replace the one that caused it. It
    /// says what it did in the deploy log, because "the previous release is still serving" is a
    /// claim the failure message makes and this is the step that makes it true.
    ///
    /// <para>
    /// Which is why <paramref name="record"/> is the pipeline's cancellation-free logger and not the
    /// work's. This is the one <c>Log</c> call site reachable after the work has stopped: the apply
    /// above already runs on <c>None</c>, so on a fired deadline the routing genuinely was put back
    /// and the line saying so was the only thing lost — leaving the failure message asserting that
    /// the previous release still serves with nothing in the log to substantiate it.
    /// </para>
    /// </summary>
    private async Task RepublishRoutingAsync(App app, Func<LogStream, string, Task> record)
    {
        try
        {
            // CancellationToken.None for the same reason as the revert: a cancelled deployment is
            // precisely when the routing has to be put back.
            var result = await proxy.ApplyAllAsync(app.WorkspaceId, CancellationToken.None);
            await record(LogStream.System, result.Success
                ? "Proxy configuration put back to the routing this deployment found."
                : $"Could not put the proxy configuration back: {result.Error}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not re-apply the proxy config after a failed proxy step.");
            try { await record(LogStream.System, "Could not put the proxy configuration back; see the server log."); }
            catch { /* the deployment already has a reason to report; this is not it */ }
        }
    }

    /// <summary>What one Route row said before this deployment rewrote it.</summary>
    private sealed record RouteRevert(
        Route Route, bool WasAdded, string TargetService, int TargetPort,
        bool SslEnabled, bool RedirectHttpToHttps, bool IsEnabled);

    /// <summary>
    /// Put the routes back as this deployment found them: rows it created are removed, rows it
    /// rewrote get their previous upstream back. A failure here must never replace the failure that
    /// caused it — the deployment is already going to be reported as failed, and losing that reason
    /// to a bookkeeping error would be the same lie in a new place.
    /// </summary>
    private async Task RevertRoutesAsync(IReadOnlyList<RouteRevert> undo)
    {
        try
        {
            foreach (var previous in undo)
            {
                if (previous.WasAdded)
                {
                    // Nothing was routing this host before, so no row is the honest state — a
                    // disabled or dangling one is still a row someone has to explain.
                    db.Routes.Remove(previous.Route);
                    continue;
                }
                previous.Route.TargetService = previous.TargetService;
                previous.Route.TargetPort = previous.TargetPort;
                previous.Route.SslEnabled = previous.SslEnabled;
                previous.Route.RedirectHttpToHttps = previous.RedirectHttpToHttps;
                previous.Route.IsEnabled = previous.IsEnabled;
            }
            // CancellationToken.None deliberately: a cancelled deployment is exactly when this has to
            // run, and passing the cancelled token would skip the cleanup its cancellation created.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not restore the routes after a failed proxy step.");
        }
    }

    /// <summary>
    /// One request to the proxy, to prove the proxy this deployment just reconfigured is up and
    /// answering. Only a failure to connect fails the deployment.
    ///
    /// Be precise about what this establishes, because the name invites more. The request goes to
    /// <c>http://{proxy}:{port}/</c> — both halves from
    /// <see cref="HarboraRuntimeOptions.ProxyContainerName"/> and
    /// <see cref="HarboraRuntimeOptions.ProxyHttpPort"/> — with the domain in a Host header, and
    /// <c>deploy/docker-compose.yml</c>
    /// puts an ENTRYPOINT-level redirect on <c>web</c>
    /// (<c>--entrypoints.web.http.redirections.entrypoint.to=websecure</c>). Traefik applies that to
    /// every request arriving on :80 before any router is consulted, so the 308 comes back whatever
    /// the Host header says and whether or not a route for it exists. What this proves is therefore
    /// that the proxy container is reachable from the panel and serving :80 — a narrow fact, but a
    /// real one, and the failure it catches (a proxy that took the config and then died, or was
    /// never reachable) is a failure no other step here would notice.
    ///
    /// It does NOT prove the route matched, that the upstream is reachable, or that the domain
    /// serves. Proving that means reaching the routers on <c>websecure</c>: a named
    /// <see cref="HttpClient"/> whose handler carries a <c>ConnectCallback</c> dialling the proxy
    /// while the request URI stays <c>https://{domain}/</c>, so SNI and certificate validation
    /// remain on the domain — the true equivalent of install.sh's <c>curl --resolve</c>. That is a
    /// later-phase decision, recorded on <see cref="HarboraRuntimeOptions.VerifyThroughProxy"/>, and
    /// deliberately not built here; the flag is off by default and nothing ships behind it.
    /// </summary>
    private async Task VerifyThroughProxyAsync(App app, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        var domain = app.Domains.FirstOrDefault(d => d.IsPrimary) ?? app.Domains.First();
        // Both halves configured, or neither is: the container name was a setting and the port it
        // answers on was a literal, so an install that moved its proxy's plain-HTTP entry point off
        // 80 would have had this probe dial a closed port and call every deployment failed.
        var url = $"http://{_opt.ProxyContainerName}:{_opt.ProxyHttpPort}/";

        await log(LogStream.System, $"Checking the proxy is answering for {domain.Host} …");

        var client = httpFactory.CreateClient();
        client.Timeout = _opt.HealthHttpTimeout;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // A host the panel cannot even put in a header is one Traefik would never match either,
            // and the FormatException this throws says only "not a valid 'Host' header string" —
            // no domain, no app, nothing to act on. Not a message this method gets to hand out.
            try { request.Headers.Host = domain.Host; }
            catch (FormatException bad)
            {
                throw new InvalidOperationException(
                    ProxyDiagnosis.ExplainUnusableHost(domain.Host, bad.Message), bad);
            }
            using var res = await client.SendAsync(request, ct);
            await log(LogStream.System,
                $"The proxy answered with HTTP {(int)res.StatusCode}; it is up and serving.");
        }
        // A real cancellation is the user stopping the deployment, not the proxy refusing it, and
        // must not be reported as an unreachable domain.
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) &&
                                   !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(ProxyDiagnosis.ExplainUnreachable(domain.Host, url, ex.Message));
        }
    }

    private string SafeUnprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch { return string.Empty; }
    }
}
