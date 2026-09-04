using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Harbora.Infrastructure.Backups;
using Harbora.Infrastructure.Deployments;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Provisions backing services as containers on the shared Harbora network. Credentials live
/// encrypted in the DB; the container gets the plaintext only through its seed env on first boot.
///
/// <para>
/// A managed database is a workload the platform charges for — the hourly tick bills its disk by
/// the gibibyte — so the two methods that leave one running ask <see cref="IBillingGate"/> first.
/// This is the path the plan for pay-as-you-go did not name: five call sites queue a provision
/// (the database form, its rebuild button, a template stack, an environment clone) and one starts a
/// stopped database by hand, and none of them checked anything about money. A suspended workspace
/// could create a Postgres, rebuild it, and start it again after the suspension had stopped its
/// apps.
/// </para>
/// </summary>
public sealed class ManagedServiceEngine(
    HarboraDbContext db,
    IServerEngineFactory engineFactory,
    ISecretProtector protector,
    IJobQueue jobs,
    IBillingGate billing,
    IOptions<HarboraRuntimeOptions> options,
    ISystemClock clock,
    ILogger<ManagedServiceEngine> logger,
    ISecretRedactor? redactor = null,
    INotificationService? notifications = null,
    Monitoring.IncidentService? incidents = null,
    IEventPublisher? events = null) : IManagedServiceEngine
{
    private readonly HarboraRuntimeOptions _opt = options.Value;

    /// <summary>Small image used only to walk a volume and add up what is in it.</summary>
    private const string MeasuringImage = "alpine:3.20";

    public IReadOnlyList<ServiceCatalogEntry> Catalog =>
        ServiceCatalog.All.Values.Select(d => new ServiceCatalogEntry(
            d.Type, d.DisplayName, d.DisplayNameFa, $"{d.ImageRepo}:{d.Versions[0]}",
            d.Versions, d.Port, d.HasDatabaseName)).ToList();

    public async Task QueueProvisionAsync(Guid serviceId, CancellationToken ct)
    {
        // The four callers (the create form, the rebuild button, a template stack, an environment
        // clone) all already know the service's workspace, but three of them hand this only the id —
        // one extra read here is cheaper than widening the interface across every one of them.
        var workspaceId = await db.ManagedServices.IgnoreQueryFilters()
            .Where(s => s.Id == serviceId).Select(s => (Guid?)s.WorkspaceId).FirstOrDefaultAsync(ct);
        await jobs.EnqueueAsync(Harbora.Domain.Jobs.JobKind.ServiceProvision, serviceId, workspaceId, ct);
    }

    /// <summary>Runs on the background worker. Pulls the image and (re)creates the container.</summary>
    public async Task ProvisionAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (svc is null) return;

        // Asked here and not only where the provision was queued. The queue is durable, so a request
        // can be claimed long after it was made — by which time the balance that paid for it may be
        // gone. Reported the way every other provision failure is reported, so a database that will
        // not appear says Failed on the screen rather than Provisioning for ever.
        var mayStart = await billing.CanStartAsync(
            svc.WorkspaceId, Domain.Billing.BilledResourceType.Service, svc.Id, ct);
        if (!mayStart.Allowed)
        {
            // P4 (2026-08-17 app-environment-management design): this used to be the gap its own
            // comment named — Status flipped to Failed and mayStart.Reason went nowhere but the
            // operator log. ManagedService.ErrorMessage is that reason's home now, same as every
            // other failure this method can produce, so a refused database says why on its own page
            // instead of looking identical to one that tried and broke.
            await FailAsync(svc, mayStart.Reason ?? "The workspace may not start new work right now.", ct);
            logger.LogWarning("{Svc} was not provisioned: {Reason}", svc.Name, mayStart.Reason);
            return;
        }

        var def = ServiceCatalog.All[svc.Type];
        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);

        try
        {
            svc.Status = ServiceStatus.Provisioning;
            await db.SaveChangesAsync(ct);

            var creds = CredsFor(svc);

            // 1.7 (pgvector-as-option plan): the only place that decides which image a PostgreSQL
            // instance actually runs. A logical database's own CREATE EXTENSION never guesses at this
            // — it asks the engine and reports what it said — so this is the single seam where
            // "requested" (PgVectorEnabled) becomes "what RunningImage will read after this attempt".
            var image = svc.Type == ManagedServiceType.PostgreSql && svc.PgVectorEnabled
                ? PgVectorImage.For(svc.Version)
                : $"{def.ImageRepo}:{svc.Version}";

            // On its environment's own network, so a staging service cannot reach a production
            // database by name — the workspace network is only the fallback for a service placed
            // before projects and environments existed (see NetworkPlan).
            var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
            var workspaceNetwork = _opt.WorkspaceNetwork(wsSlug);
            var environmentNetwork = await ResolveEnvironmentNetworkAsync(svc, ct);
            var networks = Networking.NetworkPlan.For(environmentNetwork, workspaceNetwork);
            var network = Networking.NetworkPlan.Primary(environmentNetwork, workspaceNetwork);

            foreach (var name in networks) await docker.EnsureNetworkAsync(name, ct);
            await docker.EnsureVolumeAsync(svc.VolumeName, ct);
            await docker.PullImageAsync(image, new Progress<string>(l => logger.LogDebug("{Svc}: {Line}", svc.Name, l)), ct);
            await RemoveContainerByNameAsync(docker, svc.ContainerName, ct);

            var volumes = new List<(string, string, bool)> { (svc.VolumeName, def.DataMountPath, false) };
            var command = def.Command(creds);

            // Redis's eviction policy is a fact about this instance, not about the Redis service
            // type — the same reasoning DatabaseTls's server arguments follow below, assembled at
            // the provisioning site rather than inside the catalogue entry. Appended only when
            // something was actually chosen, so a database that predates this feature (or one nobody
            // has touched) gets exactly the command line it always has — RedisMemoryPolicy.CommandArguments
            // returns empty in that case, and this branch is a no-op.
            if (svc.Type == ManagedServiceType.Redis)
            {
                var memoryArgs = RedisMemoryPolicy.CommandArguments(svc.RedisEvictionPolicy, svc.RedisMaxMemoryBytes);
                if (memoryArgs.Count > 0) command = [.. command ?? [], .. memoryArgs];
            }

            // MariaDB and MySQL make their own certificate at first start; anything else starts
            // unencrypted unless the block below succeeds.
            svc.TlsEnabled = DatabaseTls.EncryptedByDefault(svc.Type);

            // PostgreSQL will not encrypt a connection without a certificate it can read, and the
            // moment external access publishes a port that stops being a private-network trade-off:
            // the password and every row after it would cross the internet in the clear. MariaDB and
            // MySQL make their own certificate at first start, so they are left alone.
            if (DatabaseTls.NeedsConfiguring(svc.Type))
            {
                var certVolume = DatabaseTls.VolumeName(svc.ContainerName);
                await docker.EnsureVolumeAsync(certVolume, ct);

                var (certificate, key) = DatabaseTls.Generate(svc.ContainerName, clock.UtcNow);

                var prepared = await docker.RunOneOffAsync(new DockerOneOffRequest(
                    // The service's own image, so `id -u postgres` inside it is the uid the
                    // server will actually run as.
                    Image: image,
                    Command: DatabaseTls.PrepareCommand(),
                    Binds: [(certVolume, DatabaseTls.MountPath, false)],
                    Env: DatabaseTls.PrepareEnvironment(certificate, key)),
                    new Progress<string>(l => logger.LogInformation("{Svc} tls: {Line}", svc.Name, l)), ct);

                if (prepared == 0)
                {
                    volumes.Add((certVolume, DatabaseTls.MountPath, true));
                    command = DatabaseTls.ServerCommand();
                    svc.TlsEnabled = true;
                }
                else
                {
                    // Started without encryption rather than not started at all: a database that
                    // refuses to boot because a certificate could not be written is a worse outcome
                    // than one that boots unencrypted and says so on the access page.
                    logger.LogError(
                        "Could not prepare a TLS certificate for {Svc}; it will start unencrypted.", svc.Name);
                }
            }

            // 3.1 (round-2 market-gaps plan): the only place PitrEnabled (requested) turns into an
            // actual container command line — the same seam PgVectorEnabled already owns for which
            // image runs, and HasUnpublishedChanges below is cleared the same way for both. Appended
            // after the TLS block, on top of whatever it decided (its own -c arguments, or nothing),
            // never in place of it — see PostgresWalArchivingCommand.Extend.
            if (svc.Type == ManagedServiceType.PostgreSql && svc.PitrEnabled)
            {
                command = PostgresWalArchivingCommand.Extend(command);
                var walVolume = PostgresWalArchivingCommand.VolumeNameFor(svc.VolumeName);
                await docker.EnsureVolumeAsync(walVolume, ct);
                volumes.Add((walVolume, PostgresWalArchivingCommand.ArchiveMountPath, false));
            }

            await docker.RunContainerAsync(new DockerRunRequest(
                image, svc.ContainerName, network,
                def.Env(creds),
                new Dictionary<string, string> { ["harbora.managed"] = "true", ["harbora.service"] = svc.Name },
                volumes,
                // Zero and zero until now: an app was sized and capped, and the database beside it
                // could take every core and every byte on the host. Zero still means unlimited, for
                // the services that predate this.
                def.Port, svc.MemoryLimitBytes, svc.CpuLimit, null, command), ct);

            foreach (var extra in networks.Skip(1))
                await docker.ConnectNetworkAsync(svc.ContainerName, extra, ct);

            svc.Status = ServiceStatus.Running;
            // What is actually running, as opposed to what was asked for. They diverge whenever a
            // moving tag is pulled again, and a database that changed major version this way will
            // not start on the data it already has.
            svc.RunningImage = image;
            // P4: whatever the last attempt said is history the moment this one lands — the same
            // reasoning `IncidentService.ResolveAsync`'s doc gives, and true here in a way it is not
            // for a deploy: a ManagedService row is mutated in place rather than minting a new one per
            // attempt, so THIS success is the earlier failure's own condition clearing, not a
            // different fact about a different attempt.
            svc.ErrorMessage = null;
            // The container was just (re)built from this row's own settings, so whatever was
            // queued — the Redis memory policy today — is no longer merely intended, it is what is
            // actually running. The same moment that makes InstanceSizeKey/TLS durable already.
            svc.HasUnpublishedChanges = false;
            await db.SaveChangesAsync(ct);
            if (incidents is not null)
            {
                await incidents.ResolveAsync(
                    svc.WorkspaceId, Domain.Common.AlertEvent.ServiceProvisionFailed, svc.Id.ToString(), clock.UtcNow, ct);
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation("Provisioned managed service {Name} ({Type}).", svc.Name, svc.Type);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision service {Name}.", svc.Name);

            // Redacted before it is stored, not only on its way to a log — DeploymentPipeline's own
            // failure path does the same for the same reason: the command that failed can be one that
            // carries this database's own password on its command line (see CredentialRotationPlan),
            // and this reason is shown on the service's own page.
            var creds = CredsFor(svc);
            var redacted = redactor is null ? ex.Message : redactor.Redact(ex.Message, [creds.Password]);
            var reason = Deployments.LogText.Clean(redacted);

            // Not `ct` from here on: a provision that hits the job's deadline arrives here with that
            // token already cancelled, and saving under it throws before the row is written — leaving
            // the service reading Provisioning with nothing provisioning it. The write that records
            // the failure is owed after the work stops, so it is made unconditionally, as
            // JobWorker.SettleAsync does for the job row itself.
            await FailAsync(svc, reason, CancellationToken.None);
        }
    }

    /// <summary>
    /// The one place a provisioning attempt is recorded as failed: the row, the incident, and whoever
    /// has a channel for it. Shared by the billing refusal above and the catch block below — before
    /// P4 only the row's <see cref="ManagedService.Status"/> changed and both of the other two never
    /// happened at all.
    /// </summary>
    private async Task FailAsync(ManagedService svc, string reason, CancellationToken ct)
    {
        svc.Status = ServiceStatus.Failed;
        svc.ErrorMessage = reason;
        await db.SaveChangesAsync(ct);

        if (incidents is not null)
        {
            await incidents.OpenAsync(
                svc.WorkspaceId, Domain.Common.AlertEvent.ServiceProvisionFailed, svc.Id.ToString(),
                Domain.Common.AlertSeverity.Critical, $"{svc.Name} failed to provision", reason, clock.UtcNow, ct);
            await db.SaveChangesAsync(ct);
        }

        if (notifications is not null)
        {
            try
            {
                await notifications.NotifyAsync(svc.WorkspaceId,
                    Domain.Notifications.NotificationEventData.Create(Domain.Common.AlertEvent.ServiceProvisionFailed,
                        ("ServiceName", svc.Name), ("Reason", reason)),
                    Domain.Common.AlertSeverity.Critical, ct);
            }
            catch (Exception notifyError)
            {
                // Best-effort, the same rule DeploymentPipeline's own TellSomebody applies to its own
                // alert dispatch: the row already carries the failure, so a channel refusing to accept
                // it must not cost the record of the failure itself.
                logger.LogWarning(notifyError, "Could not notify about the provisioning failure of {Svc}.", svc.Name);
            }
        }

        // P6 (2026-08-20 platform-options plan): the same seam, for a workspace's own event
        // subscriptions rather than its Alert rules. IEventPublisher.PublishAsync never throws on its
        // own, so this needs no guard of its own the way the notifications call above does.
        if (events is not null)
            await events.PublishAsync(svc.WorkspaceId, EventKind.ServiceFailed,
                new Dictionary<string, string> { ["service"] = svc.Name, ["reason"] = reason }, ct);
    }

    /// <summary>The private network for this resource's environment, or null while it has none.</summary>
    /// <summary>
    /// The network a service's container is actually on.
    ///
    /// Public so anything that has to sit beside a database — the external-access gateway — joins
    /// the same one by asking rather than by rebuilding the same name from the same parts. Two
    /// copies of that would agree until the day the naming changes, and then a gateway would come
    /// up on a network with no database on it and time out with nothing to explain why.
    /// </summary>
    public async Task<string> NetworkForAsync(Domain.Services.ManagedService svc, CancellationToken ct)
    {
        var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
        var environmentNetwork = await ResolveEnvironmentNetworkAsync(svc, ct);
        return Networking.NetworkPlan.Primary(environmentNetwork, _opt.WorkspaceNetwork(wsSlug));
    }

    private Task<string> ResolveEnvironmentNetworkAsync(Domain.Services.ManagedService svc, CancellationToken ct) =>
        Networking.EnvironmentNetworkResolver.ForAsync(db, svc.EnvironmentId, ct);

    /// <summary>
    /// Starts a stopped database, after asking whether the workspace may start anything.
    ///
    /// <para>
    /// <b>The service is read unfiltered, together with <see cref="StopAsync"/> and never one without
    /// the other.</b> This is the half of <c>BillingSuspension</c>'s fix that
    /// <c>AppOperationsService</c> got and this file did not. Both request-bound callers of this route
    /// are on the databases screen, in the customer's own session — but the resume after a top-up is
    /// not: it is driven from <c>WalletService</c> under whatever scope credited the account, and the
    /// only way an account is ever credited is an administrator pressing Credit on the provider
    /// console. That runs inside an HTTP request, so <c>HttpWorkspaceScope.IsUnscoped</c> is false and
    /// the ambient workspace is the <i>provider's</i>. Read through the tenant filter this matched
    /// nothing and threw "Sequence contains no elements" before a node was reached, so every managed
    /// database of a customer who had just paid stayed down while the failure they were shown blamed
    /// the node for not coming back — and retrying reproduced it exactly.
    /// </para>
    ///
    /// <para>
    /// The pair is fixed together because it is one route in both directions. Unfiltering only the
    /// start would leave a suspension that cannot stop a database it has already written
    /// <c>WasRunningAtSuspension</c> on — the markers say the workspace owes those databases a start
    /// while nothing ever stopped them, which is the same disagreement seen from the other side.
    /// </para>
    ///
    /// <para>
    /// <b>Ownership is the caller's to check</b>, exactly as it is for
    /// <c>AppOperationsService.ResolveAsync</c>. Both request-bound entry points —
    /// <c>DatabasesController.Start</c> and <c>.Stop</c> — call its <c>Guard</c> first, which asks
    /// <c>ProjectAccessService.CanTouchServiceAsync</c>; that predicate names
    /// <c>WorkspaceId == currentUser.WorkspaceId</c> explicitly and so does not depend on the ambient
    /// filter being narrow. <c>BillingSuspension</c>, the remaining caller, is bound to the one
    /// workspace it was asked about. Held by <c>ManagedServiceEngineTenancyTests</c>.
    /// </para>
    /// </summary>
    public async Task StartAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.IgnoreQueryFilters().FirstAsync(s => s.Id == serviceId, ct);

        // Throws rather than returning, for the reason AppOperationsService states at length: the
        // two lines below write Running, and a start that reports success without starting anything
        // hands the hourly tick an hour to bill for a container that is not there.
        var mayStart = await billing.CanStartAsync(
            svc.WorkspaceId, Domain.Billing.BilledResourceType.Service, svc.Id, ct);
        // QuotaRefusedException carries mayStart.ReasonFa along, so the controller that catches it —
        // reached from a request, and so with a culture to pick with — need not show English only.
        if (!mayStart.Allowed) throw new QuotaRefusedException(mayStart);

        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var id = await FindContainerIdAsync(docker, svc.ContainerName, ct);
        if (id is not null) await docker.RestartContainerAsync(id, ct); // restart starts a stopped container
        svc.Status = ServiceStatus.Running;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stops a running database. Never gated on the balance — a workspace with no money must still be
    /// able to put its own things down — and read unfiltered for the reason
    /// <see cref="StartAsync"/> gives, which is a statement about the pair and not about either half.
    /// </summary>
    public async Task StopAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.IgnoreQueryFilters().FirstAsync(s => s.Id == serviceId, ct);
        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var id = await FindContainerIdAsync(docker, svc.ContainerName, ct);
        if (id is not null) await docker.StopContainerAsync(id, ct);
        svc.Status = ServiceStatus.Stopped;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Guid serviceId, bool deleteData, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (svc is null) return;
        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var id = await FindContainerIdAsync(docker, svc.ContainerName, ct);
        if (id is not null) await docker.RemoveContainerAsync(id, force: true, ct);
        // With the backup engine in place, honouring deleteData is now safe: the UI warns and
        // users can back up first. Default keeps the volume.
        if (deleteData) await docker.RemoveVolumeAsync(svc.VolumeName, ct);

        // 5.1 (per-app grants, HARBORA-0035): same cleanup AppOperationsService.DeleteAsync does for
        // an AppId grant, for the ServiceId half — ProjectGrant has no FK to cascade this on its own.
        var serviceGrants = await db.ProjectGrants.IgnoreQueryFilters()
            .Where(g => g.ServiceId == serviceId && g.WorkspaceId == svc.WorkspaceId).ToListAsync(ct);
        db.ProjectGrants.RemoveRange(serviceGrants);

        db.ManagedServices.Remove(svc);
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> TestConnectionAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (svc is null) return "That database no longer exists.";

        if (Networking.ConnectionProbe.WhyUnsupported(svc.Type) is { } unsupported) return unsupported;

        var definition = ServiceCatalog.All[svc.Type];
        var plan = Networking.ConnectionProbe.For(svc.Type, CredsFor(svc))!;

        var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
        var environmentNetwork = await ResolveEnvironmentNetworkAsync(svc, ct);

        // On the network a service would use, not the panel's. Testing from anywhere else would
        // answer a question nobody asked — the failure being looked for here is usually the network.
        var network = Networking.NetworkPlan.Primary(environmentNetwork, _opt.WorkspaceNetwork(wsSlug));

        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var output = new System.Text.StringBuilder();
        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            $"{definition.ImageRepo}:{svc.Version}", plan.Command, [],
            Env: plan.Env, NetworkMode: network),
            new Deployments.InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);

        return exit == 0
            ? null
            : Networking.ConnectionProbe.Explain(svc.Type, Deployments.LogText.Clean(output.ToString()).Trim());
    }

    public async Task<long?> MeasureStorageAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (svc is null || string.IsNullOrWhiteSpace(svc.VolumeName)) return null;

        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var output = new System.Text.StringBuilder();

        // Read-only: measuring must not be able to change what it is measuring.
        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            MeasuringImage, StorageMeasurement.Command, [(svc.VolumeName, "/data", true)]),
            new Deployments.InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);

        var bytes = exit == 0 ? StorageMeasurement.Parse(output.ToString()) : null;

        // The timestamp is written even when the figure is not: "measured, and it did not work" and
        // "never measured" are different states, and the screen shows them differently.
        svc.StorageBytes = bytes;
        svc.StorageMeasuredAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        if (bytes is null)
            logger.LogWarning("Could not measure storage for {Name} (exit {Exit}).", svc.Name, exit);

        return bytes;
    }

    public async Task<IReadOnlyList<RotatedApp>> RotatePasswordAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (svc is null) return [];

        if (CredentialRotationPlan.WhyUnsupported(svc.Type) is { } reason)
            throw new InvalidOperationException(reason);

        var definition = ServiceCatalog.All[svc.Type];
        var current = CredsFor(svc);
        var newPassword = ServiceCredentials.Generate();
        if (!CredentialRotationPlan.IsSafeToApply(newPassword))
            throw new InvalidOperationException("The generated password could not be applied safely.");

        // What this database's variables looked like before the password changed, and the name they
        // may carry beside their bare one — the same two facts Detach already needs to tell "mine"
        // from "a database that happens to want the same key name", from the same place AttachKeys
        // keeps them. A second copy of either would drift from this one, and the drift is a
        // customer's app quietly holding another database's credentials.
        var oldAttachEnv = definition.AttachEnv(current);
        var prefix = AttachKeys.PrefixFor(svc.Name);

        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);

        // On the network the database actually answers on — its environment's, once it has one —
        // rather than the workspace network directly. TestConnectionAsync got this right already;
        // rotation used to be the one caller still reaching straight for the workspace network, which
        // is exactly the path P3 (2026-08-17 app-environment-management design) moves off it: the
        // workspace network is being retired underneath every one-off container that touches a
        // customer's database.
        var environmentNetwork = await ResolveEnvironmentNetworkAsync(svc, ct);
        var network = Networking.NetworkPlan.Primary(environmentNetwork, _opt.WorkspaceNetwork(wsSlug));

        if (CredentialRotationPlan.For(svc.Type, current, newPassword) is { } plan)
        {
            var output = new System.Text.StringBuilder();
            var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
                $"{definition.ImageRepo}:{svc.Version}", plan.Command, [],
                Env: plan.Env, NetworkMode: network),
                new Deployments.InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);

            // Nothing is stored unless the database accepted it. The other order locks every
            // attached app out of a database whose password never actually changed.
            if (exit != 0)
                throw new InvalidOperationException(
                    "The database refused the new password, so nothing was changed. " +
                    Deployments.LogText.Clean(output.ToString()).Trim());
        }

        svc.EncryptedPassword = protector.Protect(newPassword);

        // C1 (2026-08-22 config-delivery plan): every AppManagedService attachment of this service
        // now has a stale connection string in its running container — set true unconditionally,
        // never compare-and-skip. The trap the config-delivery plan names explicitly: a rotation's
        // whole point is that the wanted value is new, so any check that only flags a change when
        // the "wanted" value differs from something already stored would misfire on exactly the
        // service being rotated. Cleared only by DeploymentPipeline once a real deployment has
        // assembled that app's container from the service's current credentials (mirrors
        // AppStorageBucket's own HasUnpublishedChanges idiom exactly).
        var attachments = await db.AppManagedServices.Where(a => a.ManagedServiceId == svc.Id).ToListAsync(ct);
        foreach (var a in attachments) a.HasUnpublishedChanges = true;

        await db.SaveChangesAsync(ct);

        // Redis reads its password from its own command line, so it only takes effect on restart.
        if (CredentialRotationPlan.RequiresRecreate(svc.Type))
            await ProvisionAsync(svc.Id, ct);

        // Every app that was pointed at it is rewritten here, or the rotation simply breaks them.
        var newAttachEnv = definition.AttachEnv(CredsFor(svc));
        var apps = await db.Apps.Include(a => a.EnvironmentVariables)
            .Where(a => a.WorkspaceId == svc.WorkspaceId).ToListAsync(ct);

        var updated = new List<RotatedApp>();
        foreach (var app in apps)
        {
            var touched = false;
            foreach (var (key, oldValue) in oldAttachEnv)
            {
                var newValue = newAttachEnv[key];

                // Both names this database may have written under. Missing the prefixed one would
                // leave an app holding a dead password under the only name its code actually reads —
                // Defect 1. Checking only the bare key, and only its name, is Detach's own bug before
                // its guard existed: the same bare name can belong to a different database that
                // happens to want it too, and matching by key alone would rewrite that one — Defect 2.
                foreach (var candidate in new[] { key, prefix + key })
                {
                    var variable = app.EnvironmentVariables.FirstOrDefault(v => v.Key == candidate);
                    if (variable is null) continue;

                    // The value has to still be this database's old one, exactly as Detach checks
                    // before it removes anything.
                    string? decrypted;
                    try { decrypted = protector.Unprotect(variable.Value); } catch { decrypted = null; }
                    if (decrypted != oldValue) continue;

                    variable.Value = protector.Protect(newValue);
                    variable.IsSecret = true;
                    touched = true;
                }
            }
            // By id, not only by name (P4): the confirmation page this feeds queues a redeploy per
            // app, and App.Name is not guaranteed unique the way Id is.
            if (touched) updated.Add(new RotatedApp(app.Id, app.Name));
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Rotated the password for {Name}; {Count} service(s) updated.", svc.Name, updated.Count);
        return updated;
    }

    /// <summary>
    /// Sets a Redis instance's <c>maxmemory</c>/<c>maxmemory-policy</c> — see <see cref="RedisMemoryPolicy"/>
    /// for why a cache and a queue need opposite answers to the same setting.
    ///
    /// <para>
    /// Stored first, unconditionally: a rebuild after this point bakes the new value into the
    /// container's own command line (<see cref="ProvisionAsync"/> reads it back out), which is what
    /// makes this durable rather than a one-off <c>CONFIG SET</c> that a restart quietly undoes. Then,
    /// if the instance is running, applied live the same way <see cref="RotatePasswordAsync"/> and
    /// <see cref="TestConnectionAsync"/> already reach a running database — a one-off container of the
    /// service's own image, on its environment's network, running <c>redis-cli</c>. A stopped instance
    /// has nothing to reach; that is not a failure, only a fact the caller has to be told rather than
    /// asked to infer from a status field it may not have read.
    /// </para>
    /// </summary>
    public async Task<RedisMemoryPolicyOutcome> UpdateRedisMemoryPolicyAsync(
        Guid serviceId, string? policy, long maxMemoryBytes, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstAsync(s => s.Id == serviceId, ct);

        if (svc.Type != ManagedServiceType.Redis)
            throw new InvalidOperationException("Only a Redis instance has a memory eviction policy to set.");

        if (RedisMemoryPolicy.WhyRefused(policy, maxMemoryBytes, svc.MemoryLimitBytes, isFa: false) is { } reason)
            throw new RedisMemoryPolicyRefusedException(
                reason, RedisMemoryPolicy.WhyRefused(policy, maxMemoryBytes, svc.MemoryLimitBytes, isFa: true));

        svc.RedisEvictionPolicy = string.IsNullOrWhiteSpace(policy) ? null : policy.Trim();
        svc.RedisMaxMemoryBytes = maxMemoryBytes;
        // Stays true even once the live apply below succeeds — see the field's own doc for why a
        // live CONFIG SET alone does not survive a plain restart.
        svc.HasUnpublishedChanges = true;
        await db.SaveChangesAsync(ct);

        if (svc.Status != ServiceStatus.Running)
            return new RedisMemoryPolicyOutcome(WasRunning: false, AppliedLive: false, LiveApplyError: null);

        var plan = RedisMemoryPolicy.LiveApply(CredsFor(svc), svc.RedisEvictionPolicy, svc.RedisMaxMemoryBytes);
        if (plan is null)
            // Nothing chosen (both null/zero) — the state every instance is already in, so there is
            // nothing to send and nothing to fail.
            return new RedisMemoryPolicyOutcome(WasRunning: true, AppliedLive: true, LiveApplyError: null);

        var definition = ServiceCatalog.All[svc.Type];
        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
        var environmentNetwork = await ResolveEnvironmentNetworkAsync(svc, ct);
        var network = Networking.NetworkPlan.Primary(environmentNetwork, _opt.WorkspaceNetwork(wsSlug));

        var output = new System.Text.StringBuilder();
        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            $"{definition.ImageRepo}:{svc.Version}", plan.Command, [], Env: plan.Env, NetworkMode: network),
            new Deployments.InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);

        if (exit == 0)
            return new RedisMemoryPolicyOutcome(WasRunning: true, AppliedLive: true, LiveApplyError: null);

        var cleaned = Deployments.LogText.Clean(output.ToString()).Trim();
        logger.LogWarning("Could not apply the Redis memory policy live for {Svc} (exit {Exit}): {Output}",
            svc.Name, exit, cleaned);
        return new RedisMemoryPolicyOutcome(WasRunning: true, AppliedLive: false,
            LiveApplyError: string.IsNullOrWhiteSpace(cleaned) ? $"redis-cli exited {exit}." : cleaned);
    }

    public async Task<ServiceConnectionInfo> GetConnectionInfoAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.AsNoTracking().FirstAsync(s => s.Id == serviceId, ct);
        var def = ServiceCatalog.All[svc.Type];
        var creds = CredsFor(svc);
        var (full, masked) = def.Conn(creds);
        return new ServiceConnectionInfo(creds.Host, creds.Port, creds.User, creds.Password,
            def.HasDatabaseName ? creds.Database : null, full, masked);
    }

    public async Task<IReadOnlyDictionary<string, string>> BuildAttachEnvAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.AsNoTracking().FirstAsync(s => s.Id == serviceId, ct);
        return ServiceCatalog.All[svc.Type].AttachEnv(CredsFor(svc));
    }

    private ServiceCreds CredsFor(ManagedService svc) =>
        new(svc.ContainerName, ServiceCatalog.All[svc.Type].Port, svc.Username, SafeUnprotect(svc.EncryptedPassword), svc.DatabaseName);

    private static async Task<string?> FindContainerIdAsync(IDockerEngine docker, string name, CancellationToken ct)
    {
        var containers = await docker.ListContainersAsync("harbora.service", ct);
        return containers.FirstOrDefault(c => c.Name == name)?.Id;
    }

    private static async Task RemoveContainerByNameAsync(IDockerEngine docker, string name, CancellationToken ct)
    {
        var id = await FindContainerIdAsync(docker, name, ct);
        if (id is not null) await docker.RemoveContainerAsync(id, force: true, ct);
    }

    private string SafeUnprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch { return string.Empty; }
    }
}
