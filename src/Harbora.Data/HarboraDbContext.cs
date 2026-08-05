using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Auditing;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Git;
using Harbora.Domain.Identity;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Networking;
using Harbora.Domain.Servers;
using Harbora.Domain.Services;
using Harbora.Domain.Settings;
using Harbora.Domain.Templates;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Data;

public class HarboraDbContext : DbContext
{
    private readonly IWorkspaceScope _scope;

    /// <summary>
    /// System context — sees every tenant. Used by background jobs, the deploy pipeline,
    /// reconcilers and startup seeding, which legitimately operate across workspaces.
    /// </summary>
    public HarboraDbContext(DbContextOptions<HarboraDbContext> options)
        : this(options, SystemWorkspaceScope.Instance) { }

    public HarboraDbContext(DbContextOptions<HarboraDbContext> options, IWorkspaceScope scope)
        : base(options) => _scope = scope;

    // Referenced by the global query filters below. EF turns property access on the context into
    // query parameters, so one compiled model serves every workspace.
    private bool IgnoreWorkspaceFilter => _scope.IsUnscoped;
    private Guid CurrentWorkspaceId => _scope.WorkspaceId;


    public DbSet<User> Users => Set<User>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Harbora.Domain.Authorization.ProjectGrant> ProjectGrants => Set<Harbora.Domain.Authorization.ProjectGrant>();
    public DbSet<Harbora.Domain.Projects.Project> Projects => Set<Harbora.Domain.Projects.Project>();
    public DbSet<Harbora.Domain.Projects.Environment> Environments => Set<Harbora.Domain.Projects.Environment>();
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<Harbora.Domain.Nodes.Node> Nodes => Set<Harbora.Domain.Nodes.Node>();
    public DbSet<Harbora.Domain.Nodes.NodeEnrollmentToken> NodeEnrollmentTokens => Set<Harbora.Domain.Nodes.NodeEnrollmentToken>();
    public DbSet<Harbora.Domain.Nodes.NodeCommandRecord> NodeCommands => Set<Harbora.Domain.Nodes.NodeCommandRecord>();
    public DbSet<Harbora.Domain.Nodes.NodeEventRecord> NodeEvents => Set<Harbora.Domain.Nodes.NodeEventRecord>();
    public DbSet<HostPortAllocation> HostPortAllocations => Set<HostPortAllocation>();
    public DbSet<GitProvider> GitProviders => Set<GitProvider>();
    public DbSet<GitRepository> GitRepositories => Set<GitRepository>();
    public DbSet<App> Apps => Set<App>();
    public DbSet<EnvironmentVariable> EnvironmentVariables => Set<EnvironmentVariable>();
    public DbSet<Volume> Volumes => Set<Volume>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<CronRun> CronRuns => Set<CronRun>();
    public DbSet<DeploymentLog> DeploymentLogs => Set<DeploymentLog>();
    public DbSet<DomainName> Domains => Set<DomainName>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<ManagedService> ManagedServices => Set<ManagedService>();
    public DbSet<BackupDestination> BackupDestinations => Set<BackupDestination>();
    public DbSet<Backup> Backups => Set<Backup>();
    public DbSet<BackupSchedule> BackupSchedules => Set<BackupSchedule>();
    public DbSet<BackupDelivery> BackupDeliveries => Set<BackupDelivery>();

    // Backup module (docs/backup-sync/ARCHITECTURE.md). Separate tables from the four above rather
    // than extra columns on them: a repository is a managed store with its own history and garbage
    // collection, a destination is a path an artifact file is written to, and conflating the two
    // would make the two restore paths hard to tell apart.
    public DbSet<Harbora.Modules.Backup.Domain.BackupRepository> BackupRepositories =>
        Set<Harbora.Modules.Backup.Domain.BackupRepository>();
    public DbSet<Harbora.Modules.Backup.Domain.BackupPolicy> BackupPolicies =>
        Set<Harbora.Modules.Backup.Domain.BackupPolicy>();
    public DbSet<Harbora.Modules.Backup.Domain.BackupSnapshot> BackupSnapshots =>
        Set<Harbora.Modules.Backup.Domain.BackupSnapshot>();
    public DbSet<Harbora.Modules.Backup.Domain.RestoreJob> RestoreJobs =>
        Set<Harbora.Modules.Backup.Domain.RestoreJob>();
    public DbSet<Harbora.Modules.Backup.Domain.BackupIdempotencyRecord> BackupIdempotencyRecords =>
        Set<Harbora.Modules.Backup.Domain.BackupIdempotencyRecord>();

    // Sync module. Deliberately no overlap with the backup tables above: a sync space has no
    // snapshots, no retention and no restore, because there is no earlier state to go back to.
    // Sharing a model would have made the two look interchangeable in the UI.
    public DbSet<Harbora.Modules.Sync.Domain.SyncSpace> SyncSpaces =>
        Set<Harbora.Modules.Sync.Domain.SyncSpace>();
    public DbSet<Harbora.Modules.Sync.Domain.SyncDevice> SyncDevices =>
        Set<Harbora.Modules.Sync.Domain.SyncDevice>();
    public DbSet<Harbora.Modules.Sync.Domain.SyncSpaceMember> SyncSpaceMembers =>
        Set<Harbora.Modules.Sync.Domain.SyncSpaceMember>();
    public DbSet<Harbora.Modules.Sync.Domain.SyncConflict> SyncConflicts =>
        Set<Harbora.Modules.Sync.Domain.SyncConflict>();
    public DbSet<MonitoringMetric> MonitoringMetrics => Set<MonitoringMetric>();
    public DbSet<MetricRollup> MetricRollups => Set<MetricRollup>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AppTemplate> AppTemplates => Set<AppTemplate>();
    public DbSet<AppTemplateVersion> AppTemplateVersions => Set<AppTemplateVersion>();
    public DbSet<Harbora.Domain.Ai.AiProvider> AiProviders => Set<Harbora.Domain.Ai.AiProvider>();
    public DbSet<Harbora.Domain.Ai.AiProviderCredential> AiProviderCredentials => Set<Harbora.Domain.Ai.AiProviderCredential>();
    public DbSet<Harbora.Domain.Ai.AiModel> AiModels => Set<Harbora.Domain.Ai.AiModel>();
    public DbSet<Harbora.Domain.Ai.AiPlan> AiPlans => Set<Harbora.Domain.Ai.AiPlan>();
    public DbSet<Harbora.Domain.Ai.AiPlanModel> AiPlanModels => Set<Harbora.Domain.Ai.AiPlanModel>();
    public DbSet<Harbora.Domain.Ai.AiSubscription> AiSubscriptions => Set<Harbora.Domain.Ai.AiSubscription>();
    public DbSet<Harbora.Domain.Ai.AiUserApiKey> AiUserApiKeys => Set<Harbora.Domain.Ai.AiUserApiKey>();
    public DbSet<Harbora.Domain.Ai.AiUsageRecord> AiUsageRecords => Set<Harbora.Domain.Ai.AiUsageRecord>();
    public DbSet<Harbora.Domain.Services.DatabaseAccessGrant> DatabaseAccessGrants => Set<Harbora.Domain.Services.DatabaseAccessGrant>();
    public DbSet<Harbora.Domain.Services.DatabaseAccessAudit> DatabaseAccessAudits => Set<Harbora.Domain.Services.DatabaseAccessAudit>();
    public DbSet<AppTemplateAsset> AppTemplateAssets => Set<AppTemplateAsset>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Harbora.Domain.Jobs.Job> Jobs => Set<Harbora.Domain.Jobs.Job>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Harbora.Domain.Tenancy.Plan> Plans => Set<Harbora.Domain.Tenancy.Plan>();
    public DbSet<Harbora.Domain.Tenancy.InstanceSize> InstanceSizes => Set<Harbora.Domain.Tenancy.InstanceSize>();
    public DbSet<Harbora.Domain.Tenancy.UsageRecord> UsageRecords => Set<Harbora.Domain.Tenancy.UsageRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128);
        });

        b.Entity<ApiToken>(e =>
        {
            e.HasIndex(x => x.Prefix).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.Tokens).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Workspace>(e => e.HasIndex(x => x.Slug).IsUnique());

        b.Entity<WorkspaceMember>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.UserId }).IsUnique();
            e.HasOne(x => x.Workspace).WithMany(w => w.Members).HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GitProvider>(e =>
            e.HasMany(x => x.Repositories).WithOne(r => r.Provider).HasForeignKey(r => r.GitProviderId).OnDelete(DeleteBehavior.Cascade));

        b.Entity<App>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.Slug }).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(63).IsRequired();
            e.HasOne(x => x.GitRepository).WithMany().HasForeignKey(x => x.GitRepositoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.EnvironmentVariables).WithOne(v => v.App).HasForeignKey(v => v.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Volumes).WithOne(v => v.App).HasForeignKey(v => v.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Domains).WithOne(d => d.App).HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Deployments).WithOne(d => d.App).HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EnvironmentVariable>(e => e.HasIndex(x => new { x.AppId, x.Key }).IsUnique());

        // --- node agent v1 ---
        //
        // Deliberately NOT workspace-filtered. A node is platform infrastructure, like a Server:
        // it belongs to the provider, not to a tenant, and every path that reads one — enrollment,
        // the channel, the heartbeat sweeper — runs without a session. A filter here would make all
        // of them read an empty table and report success.

        b.Entity<Harbora.Domain.Nodes.Node>(e =>
        {
            e.HasIndex(x => x.NodeId).IsUnique();
            e.HasIndex(x => x.MachineFingerprint);
            e.HasIndex(x => x.CertificateThumbprint);
            e.HasIndex(x => x.Status);
            e.Property(x => x.NodeId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.CertificateThumbprint).HasMaxLength(128);
            e.Property(x => x.CertificateSerial).HasMaxLength(64);
            e.Property(x => x.Health).HasMaxLength(32);
            e.Property(x => x.Architecture).HasMaxLength(32);
            e.Property(x => x.AgentVersion).HasMaxLength(64);
        });

        b.Entity<Harbora.Domain.Nodes.NodeEnrollmentToken>(e =>
        {
            e.HasIndex(x => x.Prefix).IsUnique();
            e.HasIndex(x => x.TokenHash);
            e.Property(x => x.Prefix).HasMaxLength(32).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
        });

        b.Entity<Harbora.Domain.Nodes.NodeCommandRecord>(e =>
        {
            e.HasIndex(x => x.CommandId).IsUnique();
            e.HasIndex(x => new { x.NodeId, x.Status });
            e.HasIndex(x => x.IdempotencyKey);
            e.Property(x => x.CommandId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Command).HasMaxLength(64).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.Nonce).HasMaxLength(64);
        });

        b.Entity<Harbora.Domain.Nodes.NodeEventRecord>(e =>
        {
            e.HasIndex(x => new { x.NodeId, x.At });
            e.Property(x => x.Kind).HasMaxLength(64).IsRequired();
        });

        b.Entity<Harbora.Domain.Projects.Project>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.Slug }).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(63).IsRequired();
            e.HasMany(x => x.Environments).WithOne(v => v.Project)
                .HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Harbora.Domain.Projects.Environment>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Slug }).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(63).IsRequired();
        });

        // Deliberately SetNull, not Cascade: deleting an environment must never silently take a
        // customer's running apps and databases with it. Detaching them surfaces the mistake instead.
        b.Entity<App>(e => e.HasOne(x => x.Environment).WithMany()
            .HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.SetNull));
        b.Entity<ManagedService>(e => e.HasOne(x => x.Environment).WithMany()
            .HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.SetNull));

        b.Entity<Deployment>(e =>
        {
            e.HasIndex(x => new { x.AppId, x.Number }).IsUnique();
            // Every deployment read goes through the workspace filter.
            e.HasIndex(x => x.WorkspaceId);
            e.HasMany(x => x.Logs).WithOne(l => l.Deployment).HasForeignKey(l => l.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeploymentLog>(e => e.HasIndex(x => new { x.DeploymentId, x.Sequence }));

        b.Entity<CronRun>(e =>
        {
            // The history page reads newest-first for one job; the runner counts recent failures.
            e.HasIndex(x => new { x.AppId, x.StartedAt });
            e.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DomainName>(e =>
        {
            e.HasIndex(x => x.Host).IsUnique();
            e.HasOne(x => x.Certificate).WithMany().HasForeignKey(x => x.CertificateId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Route>(e => e.HasIndex(x => new { x.Host, x.PathPrefix }));

        b.Entity<HostPortAllocation>(e =>
        {
            // The reservation itself. Checking before inserting cannot stop two concurrent deploys
            // choosing the same number; this can.
            e.HasIndex(x => new { x.ServerId, x.Port }).IsUnique();
            e.HasIndex(x => new { x.ServerId, x.AppId, x.DeploymentNumber });
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Certificate>(e => e.HasIndex(x => x.Host));
        b.Entity<ManagedService>(e => e.HasIndex(x => x.ContainerName).IsUnique());
        b.Entity<MonitoringMetric>(e => e.HasIndex(x => new { x.ServerId, x.Name, x.Timestamp }));
        b.Entity<AppTemplate>(e => e.HasIndex(x => x.Key).IsUnique());

        // Providers, credentials, models and plans are platform configuration, not tenant data —
        // no workspace filter. Subscriptions and API keys belong to a tenant and carry one.
        b.Entity<Harbora.Domain.Ai.AiProviderCredential>(e =>
        {
            e.HasOne(x => x.AiProvider).WithMany(p => p.Credentials).HasForeignKey(x => x.AiProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Harbora.Domain.Ai.AiModel>(e =>
        {
            e.HasOne(x => x.AiProvider).WithMany().HasForeignKey(x => x.AiProviderId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Alias).IsUnique();
        });

        b.Entity<Harbora.Domain.Ai.AiPlanModel>(e =>
        {
            e.HasOne(x => x.AiPlan).WithMany(p => p.Models).HasForeignKey(x => x.AiPlanId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AiModel).WithMany().HasForeignKey(x => x.AiModelId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AiPlanId, x.AiModelId }).IsUnique();
        });

        b.Entity<Harbora.Domain.Ai.AiSubscription>(e =>
        {
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
            e.HasOne(x => x.AiPlan).WithMany().HasForeignKey(x => x.AiPlanId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.WorkspaceId);
        });

        // Not filtered: the gateway authenticates a request before it knows which tenant it is for,
        // so the key must be findable without a workspace already in scope. The lookup is by
        // prefix and the tenant is then taken from the row.
        // Usage is tenant data and filtered, but the meter writes it from a request that
        // authenticated by key rather than by session — so the writer sets WorkspaceId explicitly
        // from the key's row and reads back with IgnoreQueryFilters where it must.
        b.Entity<Harbora.Domain.Ai.AiUsageRecord>(e =>
        {
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            e.HasIndex(x => x.AiUserApiKeyId);
        });

        b.Entity<Harbora.Domain.Ai.AiUserApiKey>(e =>
        {
            e.HasIndex(x => x.Prefix);
            e.HasIndex(x => new { x.WorkspaceId, x.IsRevoked });
        });

        // A grant is one tenant's permission over one tenant's database, so it carries the same
        // filter as everything else they own. The audit trail is filtered too — but note the
        // sweeper runs without a session and reads these with IgnoreQueryFilters, deliberately.
        b.Entity<Harbora.Domain.Services.DatabaseAccessGrant>(e =>
        {
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
            e.HasOne(x => x.ManagedService).WithMany().HasForeignKey(x => x.ManagedServiceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ManagedServiceId, x.Status });
            e.HasIndex(x => x.ExpiresAt);
        });

        b.Entity<Harbora.Domain.Services.DatabaseAccessAudit>(e =>
        {
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
            e.HasIndex(x => new { x.ManagedServiceId, x.CreatedAt });
        });

        // Versions and assets belong to their template and go with it. No workspace filter: the
        // catalogue is platform-wide, and a template's own WorkspaceId already scopes a private one.
        b.Entity<AppTemplateVersion>(e =>
        {
            e.HasOne(x => x.AppTemplate).WithMany().HasForeignKey(x => x.AppTemplateId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AppTemplateId, x.Version }).IsUnique();
        });

        b.Entity<AppTemplateAsset>(e =>
        {
            e.HasOne(x => x.AppTemplate).WithMany().HasForeignKey(x => x.AppTemplateId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.AppTemplateId).IsUnique();
        });
        b.Entity<Setting>(e => e.HasIndex(x => x.Key).IsUnique());
        b.Entity<Harbora.Domain.Tenancy.InstanceSize>(e => e.HasIndex(x => x.Key).IsUnique());
        b.Entity<Harbora.Domain.Tenancy.Plan>(e => e.Property(x => x.MonthlyPrice).HasPrecision(10, 2));
        b.Entity<Harbora.Domain.Tenancy.UsageRecord>(e => e.HasIndex(x => new { x.WorkspaceId, x.Period }).IsUnique());
        b.Entity<AuditLog>(e => e.HasIndex(x => x.CreatedAt));

        b.Entity<Harbora.Domain.Jobs.Job>(e =>
        {
            // The worker's hot path: oldest Pending job first.
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            // Finding the live job for a deployment/backup when cancelling or reconciling.
            e.HasIndex(x => new { x.TargetId, x.Status });
            e.Property(x => x.ClaimedBy).HasMaxLength(128);
            e.Property(x => x.Error).HasMaxLength(2048);
            // Makes two workers claiming the same job a lost update rather than a double execution.
            e.Property(x => x.ClaimStamp).IsConcurrencyToken();
        });

        ConfigureBackupModule(b);

        DeclareApplicationGeneratedKeys(b);
        ApplyWorkspaceFilters(b);
    }

    /// <summary>
    /// Backup module schema (docs/backup-sync/ARCHITECTURE.md § 7). Kept in its own method so the
    /// module's storage shape is reviewable in one place and stays easy to lift out.
    /// </summary>
    private static void ConfigureBackupModule(ModelBuilder b)
    {
        b.Entity<Harbora.Modules.Backup.Domain.BackupRepository>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.Endpoint).HasMaxLength(512);
            e.Property(x => x.Bucket).HasMaxLength(255);
            e.Property(x => x.Region).HasMaxLength(64);
            e.Property(x => x.BasePath).HasMaxLength(1024);
            e.Property(x => x.EngineRepositoryId).HasMaxLength(256);
            // Bounded because it holds engine output. Redacted before it gets here, but a length cap
            // means a runaway stderr cannot bloat the row regardless.
            e.Property(x => x.LastError).HasMaxLength(2048);

            e.HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.Status });
        });

        b.Entity<Harbora.Modules.Backup.Domain.BackupPolicy>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.TargetRef).HasMaxLength(512).IsRequired();
            e.Property(x => x.Schedule).HasMaxLength(128).IsRequired();
            e.Property(x => x.Timezone).HasMaxLength(64).IsRequired();
            e.Property(x => x.CompressionAlgorithm).HasMaxLength(32);
            e.Property(x => x.IncludePatterns).HasMaxLength(4096);
            e.Property(x => x.ExcludePatterns).HasMaxLength(4096);
            e.Property(x => x.PreBackupHook).HasMaxLength(2048);
            e.Property(x => x.PostBackupHook).HasMaxLength(2048);

            // Retention has no identity apart from its policy and is always read and written with
            // it, so it is owned rather than a table of its own.
            e.OwnsOne(x => x.Retention);

            e.HasOne(x => x.Repository).WithMany().HasForeignKey(x => x.RepositoryId)
                // Restrict, not Cascade. Deleting a repository row must not silently delete the
                // policies pointing at it — the operator is told what still depends on it instead.
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.WorkspaceId, x.Enabled });
            // The scheduler's hot path: which policies are due.
            e.HasIndex(x => new { x.Enabled, x.NextRunAt });
        });

        b.Entity<Harbora.Modules.Backup.Domain.BackupSnapshot>(e =>
        {
            e.Property(x => x.TargetRef).HasMaxLength(512).IsRequired();
            e.Property(x => x.EngineSnapshotId).HasMaxLength(256);
            e.Property(x => x.FailureReason).HasMaxLength(2048);
            e.Property(x => x.VerificationNote).HasMaxLength(1024);
            e.Property(x => x.Warnings).HasMaxLength(4096);
            e.Property(x => x.CorrelationId).HasMaxLength(64);

            // Computed from StartedAt/CompletedAt; there is nothing to store.
            e.Ignore(x => x.Duration);
            e.Ignore(x => x.IsTerminal);
            e.Ignore(x => x.IsRestorable);

            e.HasOne(x => x.Repository).WithMany().HasForeignKey(x => x.RepositoryId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Policy).WithMany().HasForeignKey(x => x.PolicyId)
                // A deleted policy must not take its history with it: "what did we back up last
                // month" has to survive someone tidying up a schedule.
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            e.HasIndex(x => new { x.RepositoryId, x.Status });
            // Retention groups by target, newest first.
            e.HasIndex(x => new { x.WorkspaceId, x.TargetType, x.TargetRef, x.CreatedAt });
        });

        b.Entity<Harbora.Modules.Backup.Domain.RestoreJob>(e =>
        {
            e.Property(x => x.Destination).HasMaxLength(1024).IsRequired();
            e.Property(x => x.Entries).HasMaxLength(8192);
            e.Property(x => x.FailureReason).HasMaxLength(2048);
            e.Property(x => x.SafetySnapshotRef).HasMaxLength(1024);
            e.Property(x => x.CorrelationId).HasMaxLength(64);

            e.Ignore(x => x.IsTerminal);

            e.HasOne(x => x.Snapshot).WithMany().HasForeignKey(x => x.SnapshotId)
                // Restrict: a restore is an audit record of a destructive act. It must not vanish
                // because the snapshot it came from was later pruned.
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            e.HasIndex(x => x.Status);
        });

        b.Entity<Harbora.Modules.Backup.Domain.BackupIdempotencyRecord>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(128).IsRequired();
            e.Property(x => x.Endpoint).HasMaxLength(128).IsRequired();

            // Unique on the whole identity. This is what makes a concurrent retry lose the insert
            // rather than start a second restore: the second writer takes a duplicate-key error and
            // reads back the first one's result.
            e.HasIndex(x => new { x.WorkspaceId, x.Endpoint, x.Key }).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
        });

        ConfigureSyncModule(b);
    }

    /// <summary>Sync module schema. Its own method for the same reason the backup module has one.</summary>
    private static void ConfigureSyncModule(ModelBuilder b)
    {
        b.Entity<Harbora.Modules.Sync.Domain.SyncSpace>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.LocalPath).HasMaxLength(1024).IsRequired();
            e.Property(x => x.EngineFolderId).HasMaxLength(128);
            e.Property(x => x.IgnorePatterns).HasMaxLength(4096);
            e.Property(x => x.LastError).HasMaxLength(2048);

            e.HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique();
            e.HasIndex(x => x.EngineFolderId);
        });

        b.Entity<Harbora.Modules.Sync.Domain.SyncDevice>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            // 8 groups of 7 plus separators.
            e.Property(x => x.EngineDeviceId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Address).HasMaxLength(256);
            e.Property(x => x.ClientVersion).HasMaxLength(64);

            e.HasIndex(x => new { x.WorkspaceId, x.EngineDeviceId }).IsUnique();
        });

        b.Entity<Harbora.Modules.Sync.Domain.SyncSpaceMember>(e =>
        {
            e.HasOne(x => x.SyncSpace).WithMany(s => s.Members).HasForeignKey(x => x.SyncSpaceId)
                // A space that goes takes its memberships with it: a membership has no meaning
                // without the folder it shares.
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.SyncDevice).WithMany().HasForeignKey(x => x.SyncDeviceId)
                // A device does not: removing one that still shares folders should say so rather
                // than silently unsharing them.
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.SyncSpaceId, x.SyncDeviceId }).IsUnique();
        });

        b.Entity<Harbora.Modules.Sync.Domain.SyncConflict>(e =>
        {
            e.Property(x => x.RelativePath).HasMaxLength(1024).IsRequired();
            e.Property(x => x.OriginalRelativePath).HasMaxLength(1024).IsRequired();
            e.Property(x => x.OriginatingDevice).HasMaxLength(64);

            e.Ignore(x => x.IsOpen);

            e.HasOne(x => x.SyncSpace).WithMany().HasForeignKey(x => x.SyncSpaceId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.SyncSpaceId, x.Resolution });
            e.HasIndex(x => new { x.SyncSpaceId, x.RelativePath });
        });
    }

    /// <summary>
    /// Every <see cref="BaseEntity"/> assigns its own Id, so the store never generates one. EF's
    /// default for a Guid key assumes the opposite, and under that assumption a key that already holds
    /// a value can only mean the row exists: a child added to a parent that is already loaded was
    /// tracked as Modified and saved as an UPDATE matching no row.
    ///
    /// Observed in production as a 500 when adding a domain to an existing app
    /// ("expected to affect 1 row(s), but actually affected 0"). Creating an app hid it, because
    /// db.Apps.Add cascades Added through the whole graph. Fixed here rather than at each call site so
    /// the next collection someone appends to cannot bring it back. See ChildEntityTrackingTests.
    /// </summary>
    private static void DeclareApplicationGeneratedKeys(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            if (!typeof(BaseEntity).IsAssignableFrom(entity.ClrType)) continue;

            // Only the simple Id key: a composite or shadow key is not BaseEntity's Guid.
            if (entity.FindPrimaryKey() is { Properties: [{ Name: nameof(BaseEntity.Id) } key] }
                && key.ClrType == typeof(Guid))
                key.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }
    }

    /// <summary>
    /// Tenant isolation as a property of the model (completes P13). Controllers already scope their
    /// queries by hand; these filters mean a query that forgets to returns nothing instead of
    /// another tenant's data — the failure mode becomes "missing", not "leaked".
    ///
    /// Entities without a tenant (users, workspaces, servers, plans, settings, audit log, jobs,
    /// templates) are deliberately unfiltered: they are platform-level, and several are needed
    /// before a workspace is even known (login, setup).
    /// </summary>
    private void ApplyWorkspaceFilters(ModelBuilder b)
    {
        // Owns a WorkspaceId directly.
        b.Entity<App>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Route>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<ManagedService>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Backup>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<BackupDestination>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<BackupSchedule>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);

        // Backup module. Each carries its own WorkspaceId rather than being filtered through its
        // parent, for the reason spelled out on Deployment below: a navigation filter over a
        // non-nullable key becomes an INNER JOIN, which hides rows whose parent is momentarily
        // missing — and the prune and health jobs exist precisely to find those.
        //
        // The jobs that read these run unscoped (SystemWorkspaceScope). A sweeper that accidentally
        // runs with a REQUEST scope reads nothing here and reports success having done nothing:
        // no exception, no alert, and a backup schedule that looks healthy while never running.
        b.Entity<Harbora.Modules.Backup.Domain.BackupRepository>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Harbora.Modules.Backup.Domain.BackupPolicy>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Harbora.Modules.Backup.Domain.BackupSnapshot>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Harbora.Modules.Backup.Domain.RestoreJob>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        // The key is client-chosen, so two tenants can pick the same string. Filtered so one can
        // never replay the other's result.
        b.Entity<Harbora.Modules.Backup.Domain.BackupIdempotencyRecord>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);

        // Sync module. The status refresher runs unscoped, like every other sweeper here.
        b.Entity<Harbora.Modules.Sync.Domain.SyncSpace>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Harbora.Modules.Sync.Domain.SyncDevice>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Harbora.Modules.Sync.Domain.SyncSpaceMember>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Harbora.Modules.Sync.Domain.SyncConflict>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Alert>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<GitProvider>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<WorkspaceMember>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Harbora.Domain.Tenancy.UsageRecord>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Harbora.Domain.Projects.Project>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<CronRun>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        // Environments carry a denormalised WorkspaceId for the same reason deployments do: filtering
        // through the parent turns into a join that can hide rows whose parent is momentarily absent.
        b.Entity<Harbora.Domain.Projects.Environment>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);

        // Deployment ids appear in URLs, so this is the natural id-guessing target. It carries a
        // denormalised WorkspaceId rather than being filtered through App: because AppId is
        // non-nullable, a navigation filter becomes an INNER JOIN, which would hide any deployment
        // whose app row is missing — including from the crash reconciler whose entire job is to find
        // stranded deployments. A direct comparison has no such failure mode (and no join cost).
        b.Entity<Deployment>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);

        // EnvironmentVariable, Volume, DomainName and DeploymentLog are deliberately NOT filtered.
        // They are only ever reached through their parent — which is filtered — so a navigation
        // filter would add a join to every read, and the same inner-join hazard, for no extra
        // protection.
    }

    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return await base.SaveChangesAsync(ct);
    }
}
