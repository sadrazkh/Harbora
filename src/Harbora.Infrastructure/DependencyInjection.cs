using Docker.DotNet;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Common;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Docker;
using Harbora.Infrastructure.Git;
using Harbora.Infrastructure.Jobs;
using Harbora.Infrastructure.Proxy;
using Harbora.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harbora.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers every infrastructure adapter. The host (Web) must additionally register an
    /// <see cref="IDeploymentLogStream"/> (the SignalR-backed one) after calling this.
    /// </summary>
    public static IServiceCollection AddHarboraInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<TraefikOptions>(config.GetSection("Traefik"));
        services.Configure<HarboraRuntimeOptions>(config.GetSection("Runtime"));
        services.Configure<Nodes.NodeAgentControlPlaneOptions>(
            config.GetSection(Nodes.NodeAgentControlPlaneOptions.SectionName));

        // Node agent v1. The registry is a singleton because it holds live sockets; everything that
        // touches the database around them is scoped, as usual.
        services.AddSingleton<Nodes.NodeChannelRegistry>();
        // Also a singleton, and for the same reason: it holds the listeners that carry traffic back
        // down a node's ingress tunnel.
        services.AddSingleton<Nodes.NodeIngressRegistry>();
        services.AddHostedService<Nodes.NodeIngressRebinder>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<Nodes.NodeCertificateAuthority>();
        services.AddScoped<Nodes.NodeEnrollmentService>();
        services.AddScoped<Nodes.NodeCommandService>();
        services.AddScoped<Nodes.NodeChannelSession>();
        services.AddHostedService<Nodes.NodeHeartbeatMonitor>();
        services.AddHostedService<Nodes.NodeTunnelGateway>();

        // Scheduling onto nodes: the Server projection that makes a node visible to the scheduler,
        // the engine's read of what a node is, and the digest resolution a node insists on.
        services.AddScoped<Nodes.NodeServerLink>();
        services.AddScoped<Nodes.NodeHostFacts>();
        services.AddScoped<Nodes.ImageDigestResolver>();
        services.AddScoped<Nodes.NodeIngressRouter>();

        // Container runtime
        services.AddSingleton<IDockerClient>(_ =>
        {
            var host = Coalesce(config["Docker:Host"], Environment.GetEnvironmentVariable("DOCKER_HOST"))
                       ?? (OperatingSystem.IsWindows() ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock");
            return new DockerClientConfiguration(new Uri(host)).CreateClient();
        });
        services.AddScoped<IDockerEngine, DockerEngine>();
        // Per-server engine resolution (local in-process vs remote agent) for multi-server.
        services.AddScoped<IServerEngineFactory, Docker.ServerEngineFactory>();

        // C2 (2026-08-22 config-delivery plan): docker-cp-shaped read/write of one file inside a
        // container, local-engine only for now — see IContainerConfigFileWriter's own doc for why a
        // remote node is out of scope today rather than half-supported.
        services.AddScoped<Application.Abstractions.IContainerConfigFileWriter, Docker.DockerContainerConfigFileWriter>();
        services.AddSingleton<Configuration.ConfigFileEditorFactory>();
        services.AddScoped<Application.Abstractions.IConfigOverrideResolver, Configuration.ConfigOverrideResolver>();
        // C1 (same plan) fills this in once its attach-a-database work lands; until then every
        // AttachedServiceConnectionString-kind rule fails with an ordinary, actionable
        // ServiceReferenceUnavailable reason rather than a thrown exception or a silent placeholder.
        services.AddScoped<Application.Abstractions.IAttachedServiceConnectionStringResolver,
            Configuration.NullAttachedServiceConnectionStringResolver>();

        // Source + proxy engines
        services.AddSingleton<IGitService, LibGit2GitService>();
        // The proxy engine renders one file for the whole install, so it reads the platform's routes
        // itself through the catalog rather than being handed a caller's slice of them.
        services.AddSingleton<IRouteCatalog, Proxy.RouteCatalog>();
        services.AddSingleton<IProxyEngine, TraefikProxyEngine>();

        // Git providers (repo import) + webhook processing (deploy on push/tag).
        services.AddScoped<IGitProviderClient, Git.GitProviderClient>();
        services.AddScoped<IGitWebhookProcessor, Git.GitWebhookProcessor>();
        services.AddScoped<IGitOAuthService, Git.GitOAuthService>();

        // Security — resolve the master key fail-closed (ADR-009 / doc 10 §2.2): Production must
        // supply a key; only Development may fall back to the insecure dev key (with a loud warning).
        // Coalesce so a blank appsettings value (the shipped default) falls through to the env var.
        var configuredKey = Coalesce(config["Harbora:MasterKey"],
                                     Environment.GetEnvironmentVariable("HARBORA_MASTER_KEY"));
        var isProduction = !string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            "Development", StringComparison.OrdinalIgnoreCase);
        var masterKey = MasterKeyResolver.Resolve(configuredKey, isProduction);
        if (masterKey.UsedDevFallback)
            Console.Error.WriteLine(
                "⚠ HARBORA_MASTER_KEY is not set — using the INSECURE development key. " +
                "Never run Production like this.");
        services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(masterKey.Key));
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ISecretRedactor, SecretRedactor>();
        services.AddScoped<ITokenService, TokenService>();
        // Nobody is impersonating unless a host says otherwise. Registered here rather than left to
        // each host so a background worker's audit row can never be missing this answer; the web
        // host replaces it with the claims-reading one after this call returns.
        services.AddSingleton<ISupportSession>(NoSupportSession.Instance);
        services.AddScoped<IAuditLogger, Auditing.AuditLogger>();
        // "May this person do this here" — asked the same way by every screen.
        services.AddScoped<Security.ProjectAccessService>();

        // Platform services
        services.AddSingleton<ISystemClock, SystemClock>();
        // Durable job queue (P3): work is persisted before it runs, so a restart resumes from the
        // database rather than losing whatever was in memory.
        services.AddSingleton<IJobCancellationRegistry, JobCancellationRegistry>();
        services.AddSingleton<JobSignal>();
        // Holds the worker's claim loop until every startup reconciler below has finished. The
        // worker is a BackgroundService, so its StartAsync returns immediately and it would
        // otherwise be claiming work while the reconcilers are still deciding what that work means.
        services.AddSingleton<JobStartupGate>();
        // How much of the platform's background work may happen at once. One reproduces the worker
        // the platform ran before jobs went parallel, and is the rollback path.
        services.Configure<JobQueueOptions>(config.GetSection(JobQueueOptions.SectionName));
        services.AddScoped<IJobQueue, DatabaseJobQueue>();
        // Settles jobs orphaned by a crash BEFORE deployments are reconciled — order matters.
        services.AddHostedService<JobReconciler>();
        services.AddHostedService<JobWorker>();

        // Deployment engine
        services.AddScoped<IDeploymentEngine, DeploymentEngine>();
        services.AddScoped<DeploymentPipeline>();
        services.AddScoped<IAppOperationsService, AppOperationsService>();
        services.AddScoped<IRollbackPlanner, Deployments.RollbackPlanner>();
        // Remote-node host ports are reserved, not guessed (see HostPortRange).
        services.AddScoped<Deployments.HostPortAllocator>();
        // Scheduled jobs: each run is a short-lived container, and the history it leaves is the point.
        // The run itself is registered on its own too — the schedule and the "run now" button
        // share one path, so a job tested by hand behaves exactly as it will at 03:00.
        services.AddScoped<Deployments.CronJobRunner>();
        services.AddHostedService<Deployments.CronRunner>();
        // Crash recovery: reconcile in-flight deployments on startup (ADR-005).
        services.AddHostedService<Deployments.DeploymentReconciler>();
        // The panel's own network membership does not survive an update that recreates its container
        // (docker-compose.yml declares it on the shared network only; every tenant network is joined
        // imperatively, at deploy time). Rebinding here, before the gate below opens, means a cron or
        // event invocation queued the moment the worker starts claiming still finds its function app.
        services.AddHostedService<Deployments.PanelNetworkRebinder>();
        // Lets the job worker start claiming. Hosted services start in registration order, so every
        // startup reconciler — including any added later — must be registered ABOVE this line; that
        // is the whole guarantee, and it is on the reconciler's registration, not on this one.
        services.AddHostedService<JobStartupGateOpener>();

        // Managed services (databases/caches). Concrete type is registered too so background
        // jobs can resolve ProvisionAsync directly.
        services.AddScoped<Services.ManagedServiceEngine>();
        // Who is actually using a database — needed before deleting one, and by the architecture view.
        services.AddScoped<Services.ServiceUsageService>();
        services.AddScoped<Storage.VolumeFileService>();
        // Mints and redeems the one-time links that reach one file in a volume with no panel
        // session; the sweeper retires spent and expired rows the way AdminerSweeper does.
        services.AddScoped<Storage.VolumeDownloadTokens>();
        services.AddHostedService<Storage.VolumeDownloadTokenSweeper>();
        services.Configure<Storage.ObjectStorageOptions>(
            config.GetSection(Storage.ObjectStorageOptions.SectionName));
        services.AddScoped<Storage.ObjectStorageAdmin>();
        services.AddScoped<Storage.BucketObjectService>();

        // BYO SMTP providers (F6, 2026-08-21 functions-and-services plan). The real transport is
        // System.Net.Mail.SmtpClient, the same one PlatformMailer already uses for the platform's
        // own outgoing mail — registered behind ISmtpTransport so a test can substitute a fake at
        // this exact seam instead of opening a real socket.
        services.AddScoped<Email.ISmtpTransport, Email.SystemNetSmtpTransport>();
        services.AddScoped<Email.EmailProviderMailer>();

        services.AddScoped<Projects.EnvironmentCloner>();
        services.AddScoped<IManagedServiceEngine>(sp => sp.GetRequiredService<Services.ManagedServiceEngine>());

        // Backups (config + volume/db), storage (local + S3), and the schedule runner.
        services.Configure<Backups.BackupOptions>(config.GetSection("Backups"));
        services.AddSingleton<Backups.ArtifactRelayRegistry>();
        services.Configure<Terminals.TerminalFeatureOptions>(
            config.GetSection(Terminals.TerminalFeatureOptions.SectionName));
        // BackupStorage executes SFTP transfers through IDockerEngine, which is scoped because its
        // connection options may be resolved per request/node. Registering the storage adapter as a
        // singleton captured that scoped engine and made Development startup fail DI validation.
        services.AddScoped<IBackupStorage, Backups.BackupStorage>();
        // Idempotency-Key handling, shared by every module's API. Platform-level rather than in one
        // module, so a second module does not have to depend on the first to reuse the table.
        services.AddScoped<IIdempotencyStore, Common.IdempotencyStore>();
        services.AddScoped<Backups.BackupEngine>();
        // Mints and redeems the one-time links a self-serve database export is downloaded through —
        // sub-project 10. No dedicated sweeper: BackupEngine.EnforceRetentionAsync retires spent and
        // expired rows on the tick BackupScheduler already runs, the same reasoning that keeps this
        // from being a second sweeper beside VolumeDownloadTokenSweeper.
        services.AddScoped<Backups.BackupDownloadTokens>();
        // Sends a copy of each finished backup to Telegram/email, alongside the stored artifact.
        services.AddScoped<Backups.BackupDeliveryService>();
        // Runs before migrations at startup: an upgrade of an existing install gets a restore point.
        services.AddScoped<Backups.UpgradeSafetyService>();
        services.AddScoped<IBackupEngine>(sp => sp.GetRequiredService<Backups.BackupEngine>());
        services.AddHostedService<Backups.BackupScheduler>();
        // Checks backups on its own, so a backup that will not restore is found on an ordinary
        // afternoon rather than during an incident.
        services.AddHostedService<Backups.BackupVerifier>();

        // What the dashboard opens with: findings a person can act on, from stored facts only.
        services.AddScoped<Dashboard.AttentionService>();

        // Projects + environments: the grouping every screen and the private network hang off.
        services.AddScoped<Projects.ProjectService>();
        // The confirmed cascade behind "delete project": every app and database it names goes
        // through the same single-item delete paths AppsController and DatabasesController already
        // use, not a second way of tearing a workload down.
        services.AddScoped<Projects.ProjectDeletionService>();
        services.AddScoped<Security.WorkspaceAccountService>();
        services.AddScoped<Security.AccountSessionService>();
        // Opens, checks and closes the periods a platform administrator spends inside a customer's
        // account. Scoped: LiveAsync runs on every request under one and writes the expiry back.
        services.AddScoped<Identity.SupportSessionService>();
        services.AddScoped<Templates.TemplateDeploymentService>();
        // A branch gets an environment of its own, and loses it again when the branch goes.
        services.AddScoped<Projects.PreviewEnvironmentService>();
        // Branches get abandoned rather than deleted, so the webhook alone would leak services.
        services.AddHostedService<Projects.PreviewSweeper>();

        // Explaining a failed deployment, behind a flag and an administrator's own API key. The
        // redaction boundary it depends on is a pure rule — see AssistantRedaction.
        services.AddScoped<Assistant.AssistantClient>();
        services.AddScoped<Assistant.AssistantService>();

        // Turns the stored cumulative counters into a rate — see NetworkThroughput for why a
        // restart has to read as a gap rather than a spike.
        services.AddScoped<Monitoring.NetworkHistory>();
        // Uptime percent and restart totals from the app.up/app.restarts series — see LifecycleHistory
        // for why a restart rollup's Average alone is not the number to show.
        services.AddScoped<Monitoring.LifecycleHistory>();

        // P7 (2026-08-20 platform-options plan): assembles the public status page from App.Status and
        // LifecycleHistory — the one place both are asked on that page's behalf.
        services.AddScoped<Status.StatusPageReport>();
        // P8: makes the status page's hosts (platform subdomain, and a customer's own custom domain)
        // genuinely reachable through the same Route/IProxyEngine writer every other route uses.
        services.AddScoped<Status.StatusPageDomainService>();

        // The AI gateway. The adapter is registered against the interface so a second provider
        // shape can be added without touching the routing or metering above it.
        services.AddScoped<Ai.AiGatewayService>();
        services.AddScoped<Ai.IAiProviderAdapter, Ai.OpenRouterProviderAdapter>();

        // A singleton, because it is the counter. Scoped, every request would get a fresh one and
        // every limit would be judged against a history of exactly one request — a rate limiter that
        // is present, tested, configured and enforces nothing.
        services.AddSingleton<Ai.AiRateLimiter>();

        // Newer versions of the ready-made apps. The background job checks a setting before it does
        // anything, so registering it does not start talking to registries.
        services.AddScoped<IContainerRegistry, Templates.ContainerRegistryClient>();

        // Registered by its own type and then handed to the host, rather than AddHostedService<T>.
        // That overload registers it only as IHostedService, so the admin page's "check now" button
        // would fail to resolve it — at runtime, on a page that compiles and looks finished.
        services.AddSingleton<Templates.RegistryDiscoveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<Templates.RegistryDiscoveryService>());

        // Outside access to a managed database, and the sweeper that makes "temporary" true.
        // The gateway and the grant executor are what turn it from a contract into an open port:
        // on a single-server install the control plane already talks to the same Docker daemon the
        // databases run on, which was true the whole time this was documented as blocked on a node
        // agent that has not shipped.
        services.AddScoped<Services.DockerTcpGateway>();
        services.AddScoped<Services.DatabaseGrantExecutor>();
        services.AddScoped<Services.DatabaseAccessService>();
        services.AddHostedService<Services.DatabaseAccessSweeper>();
        services.AddHostedService<Storage.BucketMeasurementSweeper>();
        services.AddScoped<Maintenance.DiskCleanupService>();
        services.AddScoped<Notifications.PlatformMailer>();
        services.AddHostedService<Maintenance.UpdateCheckService>();
        // Bounds the seven tables that had no retention at all — build logs first among them. A
        // nightly timer, and so deliberately BELOW JobStartupGateOpener: it is not a startup
        // reconciler, it settles nothing the job worker is waiting on, and putting a delete pass on
        // the boot path would hold the worker behind it for no benefit.
        services.Configure<Maintenance.RetentionOptions>(
            config.GetSection(Maintenance.RetentionOptions.SectionName));
        services.AddHostedService<Maintenance.DataRetentionSweeper>();

        // The durable scheduler queues every ended UTC hour and retries incomplete accounting runs.
        services.Configure<Billing.BillingOptions>(config.GetSection(Billing.BillingOptions.SectionName));
        services.AddScoped<Billing.BillingTick>();
        services.AddScoped<Billing.BillingRunHandler>();
        services.AddScoped<Billing.BillingRunRetryService>();
        services.AddHostedService<Billing.BillingScheduler>();
        services.AddScoped<Billing.ResourceCreationBilling>();
        services.AddScoped<Billing.WorkspaceBudgetService>();
        services.AddScoped<Mail.StalwartClient>();
        services.AddScoped<Mail.MailPlatformService>();
        // Stopping what a workspace is running once its balance is gone, and bringing back exactly
        // what that stop took away. Registered beside the tick and, like it, scheduled by nothing
        // yet; it refuses to suspend anybody at all while Billing:Enabled is false.
        services.AddScoped<Billing.BillingSuspension>();
        // Money in, and the bill that says where the money went. Unlike the three above it is NOT
        // switched off by Billing:Enabled: taking a payment and showing somebody what they were
        // charged neither costs a customer money nor stops their workloads, and an install that
        // switched billing off after a suspension must still be able to lift it.
        services.AddScoped<Billing.WalletService>();
        services.AddScoped<Billing.VoucherService>();
        // The operator's cross-tenant "what is the platform earning" read. Depends on WalletService
        // for every runway it shows — registered after it for that reason, though DI order does not
        // itself matter here.
        services.AddScoped<Billing.RevenueReport>();
        // Sub-project 4 (2026-08-20 platform-options plan): fans a Warning-severity Announcement out
        // to every workspace's own N3 in-app rows via INotificationService.NotifyInAppOnlyAsync.
        services.AddScoped<Platform.AnnouncementNotifier>();
        // The gate every start path asks before a container runs. Registered here rather than beside
        // the deployment engine because the rule it holds is a billing rule, and a second copy of it
        // living next to the thing it refuses is how a rule quietly stops being one. Like the tick
        // and the suspension it refuses nothing at all while Billing:Enabled is false.
        services.AddScoped<Application.Abstractions.IBillingGate, Billing.BillingGate>();
        services.AddScoped<Services.AdminerService>();
        services.AddHostedService<Services.AdminerSweeper>();

        // Until the real agent ships, the fake stands in — and warns on every call, so a production
        // deployment that never configured an agent cannot quietly report tunnels it never made.
        services.AddSingleton<Application.Abstractions.INodeAgentClient, Nodes.FakeNodeAgentClient>();

        // Who is entitled to which feature. Scoped rather than singleton because it memoises per
        // instance, and a memo that outlived the request would keep serving a grant the owner just
        // revoked.
        services.AddScoped<IFeatureGate, Features.FeatureGate>();

        // Functions: code written in the panel. The generator turns rows into a build context, the
        // invoker knocks on a running host, and the scheduler decides when it should.
        services.AddScoped<Functions.FunctionAppService>();
        services.AddScoped<IFunctionInvoker, Functions.FunctionInvoker>();
        services.AddScoped<IFunctionEventBus, Functions.FunctionEventBus>();
        // F3, 2026-08-21 functions-and-services plan: the other direction through the same door.
        services.AddScoped<ICustomEventIngestService, Functions.CustomEventIngestService>();
        // F1 reversal (2026-08-21 functions-and-services plan follow-up): the generated host reports
        // a public call back here, fire-and-forget, over the same anonymous-door shape.
        services.AddScoped<IFunctionInvocationReportService, Functions.FunctionInvocationReportService>();
        services.AddScoped<IJobHandler, Functions.FunctionInvokeJobHandler>();
        services.AddHostedService<Functions.FunctionCronScheduler>();
        // F2 (2026-08-21 functions-and-services plan, "Queue-triggered functions"): the panel-side
        // RabbitMQ bridge. Unproven on this dev machine — no Docker, no live broker — see
        // RabbitMqBrokerConnectionFactory's own doc.
        services.AddSingleton<Application.Abstractions.IQueueBrokerConnectionFactory,
            Functions.RabbitMqBrokerConnectionFactory>();
        services.AddHostedService<Functions.QueueFunctionConsumerHost>();

        // Tenancy quotas + node capacity (PaaS).
        services.AddScoped<IQuotaService, Tenancy.QuotaService>();
        services.AddScoped<INodeCapacityService, Tenancy.NodeCapacityService>();
        services.AddScoped<ISchedulerService, Tenancy.SchedulerService>();
        services.AddHostedService<Tenancy.MeteringService>();
        // Measures one volume at a time, so the disk quota is checked against something real.
        services.AddHostedService<Tenancy.StorageMeasurer>();

        // Tells the truth about custom domains: where DNS points and what certificate is live.
        services.AddScoped<IDomainInspector, Networking.DomainInspector>();
        // The Cloudflare v4 calling convention, shared by the platform's own token below and a
        // workspace's BYO token (F9) — one HTTP transport, two entirely separate credential stores.
        services.AddSingleton<Networking.CloudflareApiClient>();
        services.AddScoped<Networking.CloudflarePlatformService>();
        services.AddScoped<Networking.CustomerCloudflareService>();
        // The one place every app-creation path asks what hostname an app should get.
        services.AddScoped<Networking.AppAddressAssigner>();

        // Monitoring + notifications.
        services.AddHttpClient();
        services.Configure<Notifications.NotificationOptions>(config.GetSection("Notifications"));
        // The disk ratio, the disk-alert interval, the threshold repeat window, and the dashboard's
        // own backup-staleness figure — see Monitoring.MonitoringOptions for why that last one is
        // deliberately not the same number as VerificationSchedule.StaleAfter or
        // StorageMeasurer.StaleAfter.
        services.Configure<Monitoring.MonitoringOptions>(config.GetSection(Monitoring.MonitoringOptions.SectionName));
        // N4 (2026-08-16 notification-system spec, "in the reader's own language"): stateless, so a
        // singleton — see NotificationTemplateCatalog's own doc for why this lives here rather than
        // behind Harbora.Web's SharedResource/.resx.
        services.AddSingleton<Application.Abstractions.INotificationTemplateCatalog, Notifications.NotificationTemplateCatalog>();
        services.AddScoped<INotificationService, Notifications.NotificationService>();
        // N1 (2026-08-16 notification-system spec): the job body for one queued NotificationDelivery.
        // Registered as an IJobHandler so JobDispatcher finds it without the core referencing this
        // namespace by name — the same seam the backup module and function invocations already use.
        services.AddScoped<IJobHandler, Notifications.NotificationDeliveryJobHandler>();
        // P6 (2026-08-20 platform-options plan): event subscriptions — a second, narrower fan-out
        // over the same lifecycle facts NotifyAsync's raise sites already produce. See
        // EventDispatcher's own doc for exactly what it reuses from the two registrations above.
        services.AddScoped<Application.Abstractions.IEventPublisher, Notifications.EventDispatcher>();
        services.AddScoped<IJobHandler, Notifications.EventDeliveryJobHandler>();
        // Opens, resolves, acknowledges and expires AlertIncident rows — the "things that fire also
        // stop firing" half of monitoring, kept separate from NotificationService because a resolve is
        // not a notification (see the type doc).
        services.AddScoped<Monitoring.IncidentService>();
        // N2 (2026-08-16 notification-system spec): persisted dedup key with a window, replacing the
        // in-memory AlertThrottle that used to be registered here — scoped like the db context it
        // writes through, not a singleton, because durability is now the database's job rather than a
        // process-lifetime dictionary's.
        services.AddScoped<Monitoring.AlertDedup>();
        // Summarises finished hours and days so history outlives the raw samples.
        services.AddScoped<Monitoring.MetricsRollupService>();
        services.AddScoped<IMetricsCollector, Monitoring.MetricsCollector>();
        services.AddHostedService<Monitoring.MetricsCollectorHostedService>();
        // Raises the SSL-expiry alert the rule engine has always offered but nothing ever fired.
        services.AddHostedService<Monitoring.CertificateWatcher>();

        // N5 (2026-08-16 notification-system spec, "noise control"): per-user preferences, the digest
        // job and the weekly report. The service reads/writes NotificationPreference directly; the
        // runner is what NotificationDigestScheduler's timer and its own tests both call.
        services.AddScoped<Notifications.NotificationPreferenceService>();
        services.Configure<Notifications.NotificationDigestOptions>(
            config.GetSection(Notifications.NotificationDigestOptions.SectionName));
        services.AddScoped<Notifications.NotificationDigestRunner>();
        services.AddHostedService<Notifications.NotificationDigestScheduler>();

        // The Learning Centre: the nine tutorial chapters in docs/tutorial, rendered on request (see
        // Learning.LearningLibrary). Left unregistered by the task that built the library, because
        // only its first consumer — the controller — knows the production root to give it, and that
        // root is not the same path in every place this runs it from.
        services.AddSingleton(sp => new Learning.LearningLibrary(ResolveChaptersRoot(config, sp)));

        return services;
    }

    /// <summary>
    /// Where <c>docs/tutorial</c> is, which is not the same answer in a dev run and in the container.
    ///
    /// <para>
    /// The shipped default, <c>docs/tutorial</c>, is relative to the content root and is correct for
    /// the container: the Dockerfile's runtime stage copies the chapters to sit right next to the
    /// published DLL, so the content root IS the directory that holds them. A <c>dotnet run</c> from
    /// <c>src/Harbora.Web</c> has a content root two levels below the repository root the chapters
    /// actually live under, so that relative path resolves to nothing there — in which case this
    /// walks upward from the content root looking for <c>docs/tutorial</c>, the same search
    /// <c>TestPaths</c> uses to find it for a test run. <c>Learning:ChaptersRoot</c> is left
    /// configurable (rooted or relative) for any layout that fits neither.
    /// </para>
    /// </summary>
    private static string ResolveChaptersRoot(IConfiguration config, IServiceProvider sp)
    {
        var env = sp.GetRequiredService<IHostEnvironment>();
        var configured = config["Learning:ChaptersRoot"] ?? Path.Combine("docs", "tutorial");
        var primary = Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);

        if (Directory.Exists(primary)) return primary;

        var directory = new DirectoryInfo(env.ContentRootPath);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "tutorial");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        // Neither resolved: return the configured path anyway rather than throwing here. Chapters()
        // then fails loudly on the first request that reaches it, which is diagnosable from the logs
        // — throwing during service construction would instead take the whole panel down at boot for
        // a docs folder nothing else in the panel depends on.
        return primary;
    }

    private static string? Coalesce(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
