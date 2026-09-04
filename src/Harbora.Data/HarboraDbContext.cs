using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Auditing;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Git;
using Harbora.Domain.Identity;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Mail;
using Harbora.Domain.Networking;
using Harbora.Domain.Platform;
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
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<EmailVerificationToken> EmailVerificationTokens => Set<EmailVerificationToken>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();

    /// <summary>
    /// Periods during which a platform administrator was signed in as a customer's user.
    ///
    /// Deliberately carries NO global workspace filter, like <see cref="ApiTokens"/> and
    /// <see cref="UserSessions"/> beside it: the request that reads a row here is running inside the
    /// customer's workspace scope while the row belongs to the platform's side of the arrangement,
    /// and the expiry check happens in middleware where there is no scope to speak of yet. Every
    /// tenant-facing read of this table therefore carries an explicit
    /// <c>TargetWorkspaceId ==</c>, which is the only thing keeping one customer's support history
    /// out of another's page.
    /// </summary>
    public DbSet<SupportSession> SupportSessions => Set<SupportSession>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<WorkspaceInvitation> WorkspaceInvitations => Set<WorkspaceInvitation>();
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
    public DbSet<ConfigGroup> ConfigGroups => Set<ConfigGroup>();
    public DbSet<ConfigGroupEntry> ConfigGroupEntries => Set<ConfigGroupEntry>();
    public DbSet<AppConfigGroup> AppConfigGroups => Set<AppConfigGroup>();
    public DbSet<Volume> Volumes => Set<Volume>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<CronRun> CronRuns => Set<CronRun>();
    public DbSet<DeploymentLog> DeploymentLogs => Set<DeploymentLog>();
    /// <summary>Persisted container output (2.2, 2026-09 log-retention plan) — see the type's own doc
    /// for why it exists alongside the fetched-tail search <c>DeploymentLog</c> never answered.</summary>
    public DbSet<Harbora.Domain.Logging.AppLogLine> AppLogLines => Set<Harbora.Domain.Logging.AppLogLine>();
    public DbSet<DomainName> Domains => Set<DomainName>();
    /// <summary>A workspace's own BYO Cloudflare token (F9) — never the platform's own, which lives
    /// in <see cref="Setting"/> rows instead.</summary>
    public DbSet<CustomerDnsCredential> CustomerDnsCredentials => Set<CustomerDnsCredential>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<ManagedService> ManagedServices => Set<ManagedService>();
    /// <summary>An app's attachment to a managed database/cache (C1, 2026-08-22 config-delivery
    /// plan) — see <see cref="Harbora.Domain.Services.AppManagedService"/>.</summary>
    public DbSet<Harbora.Domain.Services.AppManagedService> AppManagedServices => Set<Harbora.Domain.Services.AppManagedService>();
    /// <summary>A logical database inside a <see cref="ManagedService"/> instance (D1, 2026-08-25
    /// shared-databases plan) — see <see cref="Harbora.Domain.Services.ManagedServiceDatabase"/>.</summary>
    public DbSet<Harbora.Domain.Services.ManagedServiceDatabase> ManagedServiceDatabases => Set<Harbora.Domain.Services.ManagedServiceDatabase>();
    public DbSet<BackupDestination> BackupDestinations => Set<BackupDestination>();
    public DbSet<Backup> Backups => Set<Backup>();
    public DbSet<BackupDownloadToken> BackupDownloadTokens => Set<BackupDownloadToken>();
    public DbSet<BackupSchedule> BackupSchedules => Set<BackupSchedule>();
    public DbSet<BackupDelivery> BackupDeliveries => Set<BackupDelivery>();
    public DbSet<MailServer> MailServers => Set<MailServer>();
    public DbSet<MailDomain> MailDomains => Set<MailDomain>();
    public DbSet<MailMailbox> MailMailboxes => Set<MailMailbox>();

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
    public DbSet<IdempotencyRecord> IdempotencyRecords =>
        Set<IdempotencyRecord>();

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
    public DbSet<ContainerLifecycleCursor> ContainerLifecycleCursors => Set<ContainerLifecycleCursor>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AlertIncident> AlertIncidents => Set<AlertIncident>();
    public DbSet<AlertDedupMark> AlertDedupMarks => Set<AlertDedupMark>();
    public DbSet<UptimeCheck> UptimeChecks => Set<UptimeCheck>();
    public DbSet<UptimeCheckResult> UptimeCheckResults => Set<UptimeCheckResult>();
    public DbSet<Harbora.Domain.Notifications.NotificationDelivery> NotificationDeliveries =>
        Set<Harbora.Domain.Notifications.NotificationDelivery>();
    public DbSet<Harbora.Domain.Notifications.UserNotification> UserNotifications =>
        Set<Harbora.Domain.Notifications.UserNotification>();
    public DbSet<Harbora.Domain.Notifications.NotificationPreference> NotificationPreferences =>
        Set<Harbora.Domain.Notifications.NotificationPreference>();
    public DbSet<Harbora.Domain.Notifications.NotificationDigestEntry> NotificationDigestEntries =>
        Set<Harbora.Domain.Notifications.NotificationDigestEntry>();
    // P6 (2026-08-20 platform-options plan): event subscriptions, not a new channel — see the type's
    // own doc.
    public DbSet<Harbora.Domain.Notifications.EventSubscription> EventSubscriptions =>
        Set<Harbora.Domain.Notifications.EventSubscription>();
    public DbSet<Harbora.Domain.Notifications.EventDelivery> EventDeliveries =>
        Set<Harbora.Domain.Notifications.EventDelivery>();
    // P7 (2026-08-20 platform-options plan): a workspace's public status page, opt-in, and the apps
    // and manual incident notes it shows — see StatusPage's own doc.
    public DbSet<Harbora.Domain.Status.StatusPage> StatusPages => Set<Harbora.Domain.Status.StatusPage>();
    public DbSet<Harbora.Domain.Status.StatusPageComponent> StatusPageComponents =>
        Set<Harbora.Domain.Status.StatusPageComponent>();
    public DbSet<Harbora.Domain.Status.StatusIncident> StatusIncidents =>
        Set<Harbora.Domain.Status.StatusIncident>();
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

    /// <summary>Sub-project 4 (2026-08-20 platform-options plan). Unfiltered by workspace, like
    /// <see cref="Users"/> beside it: an announcement is a platform-wide notice, not a tenant's.</summary>
    public DbSet<Announcement> Announcements => Set<Announcement>();

    /// <summary>Keyed by (AnnouncementId, UserId) — see that type's own doc for why that pair, and
    /// only that pair, is what keeps one dismissal from ever touching another announcement or
    /// another person's copy of the same one.</summary>
    public DbSet<AnnouncementDismissal> AnnouncementDismissals => Set<AnnouncementDismissal>();
    public DbSet<Harbora.Domain.Tenancy.Plan> Plans => Set<Harbora.Domain.Tenancy.Plan>();
    public DbSet<Harbora.Domain.Tenancy.InstanceSize> InstanceSizes => Set<Harbora.Domain.Tenancy.InstanceSize>();

    /// <summary>
    /// What each server charges for each tier. Unscoped by workspace on purpose: it is the
    /// provider's price list, read by the hourly pass and by every tenant's chooser alike.
    /// </summary>
    public DbSet<ServerInstanceOffer> ServerInstanceOffers => Set<ServerInstanceOffer>();

    public DbSet<Harbora.Domain.Storage.StorageBucket> StorageBuckets => Set<Harbora.Domain.Storage.StorageBucket>();
    public DbSet<Harbora.Domain.Storage.AppStorageBucket> AppStorageBuckets => Set<Harbora.Domain.Storage.AppStorageBucket>();

    /// <summary>File-override rules (C2, 2026-08-22 config-delivery plan) — per-app, never shared,
    /// so unlike ConfigGroup/StorageBucket/EmailProvider there is no separate "the shared thing"
    /// table to restrict deletion against.</summary>
    public DbSet<Harbora.Domain.Configuration.ConfigOverrideRule> ConfigOverrideRules => Set<Harbora.Domain.Configuration.ConfigOverrideRule>();
    public DbSet<Harbora.Domain.Storage.StoragePlan> StoragePlans => Set<Harbora.Domain.Storage.StoragePlan>();
    public DbSet<Harbora.Domain.Storage.VolumeDownloadToken> VolumeDownloadTokens => Set<Harbora.Domain.Storage.VolumeDownloadToken>();
    public DbSet<Harbora.Domain.Tenancy.UsageRecord> UsageRecords => Set<Harbora.Domain.Tenancy.UsageRecord>();

    /// <summary>BYO SMTP providers, F6 2026-08-21 functions-and-services plan (HARBORA-0038 phase
    /// 1) — not to be confused with <see cref="MailDomains"/>/<see cref="MailMailboxes"/>, the
    /// already-shipped, separate feature where Harbora hosts mailboxes on its own mail server.</summary>
    public DbSet<Harbora.Domain.Email.EmailProvider> EmailProviders => Set<Harbora.Domain.Email.EmailProvider>();
    public DbSet<Harbora.Domain.Email.AppEmailProvider> AppEmailProviders => Set<Harbora.Domain.Email.AppEmailProvider>();

    /// <summary>Bring-your-own Sentry/GlitchTip DSNs (1.8, 2026-09 market-gaps round two) — the
    /// error-tracking mirror of <see cref="EmailProviders"/>/<see cref="AppEmailProviders"/>.</summary>
    public DbSet<Harbora.Domain.ErrorTracking.ErrorTrackingProvider> ErrorTrackingProviders => Set<Harbora.Domain.ErrorTracking.ErrorTrackingProvider>();
    public DbSet<Harbora.Domain.ErrorTracking.AppErrorTrackingProvider> AppErrorTrackingProviders => Set<Harbora.Domain.ErrorTracking.AppErrorTrackingProvider>();

    /// <summary>Per-workspace private-registry pull credentials (1.3, 2026-09 market-gaps round two)
    /// — matched to an app's image by registry host, not attached app-by-app like
    /// <see cref="AppEmailProviders"/>, since a credential is a fact about the registry, not any one
    /// app.</summary>
    public DbSet<Harbora.Domain.Registries.RegistryCredential> RegistryCredentials => Set<Harbora.Domain.Registries.RegistryCredential>();

    public DbSet<Harbora.Domain.Billing.Wallet> Wallets => Set<Harbora.Domain.Billing.Wallet>();
    public DbSet<Harbora.Domain.Billing.BillingLedgerEntry> BillingLedger => Set<Harbora.Domain.Billing.BillingLedgerEntry>();
    public DbSet<Harbora.Domain.Billing.BillingRun> BillingRuns => Set<Harbora.Domain.Billing.BillingRun>();
    public DbSet<Harbora.Domain.Billing.BillingVoucher> BillingVouchers => Set<Harbora.Domain.Billing.BillingVoucher>();

    /// <summary>
    /// Who is entitled to which feature. Platform configuration rather than tenant data, and
    /// deliberately unfiltered — see <see cref="Harbora.Domain.Features.FeatureGrant"/>.
    /// </summary>
    public DbSet<Harbora.Domain.Features.FeatureGrant> FeatureGrants => Set<Harbora.Domain.Features.FeatureGrant>();

    public DbSet<Harbora.Domain.Functions.FunctionDefinition> FunctionDefinitions => Set<Harbora.Domain.Functions.FunctionDefinition>();
    public DbSet<Harbora.Domain.Functions.FunctionInvocation> FunctionInvocations => Set<Harbora.Domain.Functions.FunctionInvocation>();
    public DbSet<Harbora.Domain.Functions.FunctionCodeRevision> FunctionCodeRevisions => Set<Harbora.Domain.Functions.FunctionCodeRevision>();
    public DbSet<Harbora.Domain.Functions.FunctionCustomEventKey> FunctionCustomEventKeys => Set<Harbora.Domain.Functions.FunctionCustomEventKey>();
    public DbSet<Harbora.Domain.Functions.FunctionQueueDeadLetter> FunctionQueueDeadLetters => Set<Harbora.Domain.Functions.FunctionQueueDeadLetter>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128);

            // N5 (2026-08-16 notification-system spec): a real column default, not merely the C#
            // property initializer — every other new column this codebase has added backfills
            // existing rows with the CLR zero value (an empty string), which would leave every
            // account that predates this migration with no time zone at all rather than the sensible
            // default the spec asks for. HasDefaultValue is what makes the migration itself carry
            // "Asia/Tehran" into the ALTER TABLE, so an existing account gets the same answer a freshly
            // created one does.
            e.Property(x => x.TimeZoneId).HasDefaultValue("Asia/Tehran");
        });

        b.Entity<ApiToken>(e =>
        {
            e.HasIndex(x => x.Prefix).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.Tokens).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserSession>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.RevokedAt, x.ExpiresAt });
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.HasOne(x => x.User).WithMany(u => u.Sessions).HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SupportSession>(e =>
        {
            // Two reads, two indexes. The middleware looks a live session up by its own id on every
            // request under it; the customer's page lists their workspace's sessions newest first.
            e.HasIndex(x => new { x.TargetWorkspaceId, x.StartedAt });
            e.HasIndex(x => new { x.TargetUserId, x.EndedAt });
            e.Property(x => x.AdminEmail).HasMaxLength(256).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(SupportAccess.MaxReasonLength).IsRequired();
            e.Property(x => x.IpAddress).HasMaxLength(64);
            // No navigation to User on purpose: AdminEmail is copied onto the row so a deleted
            // administrator does not turn a customer's support history into a list of blanks.
        });

        // Sub-project 4 (2026-08-20 platform-options plan).
        b.Entity<Announcement>(e =>
        {
            // The banner partial's own read: every announcement whose window covers now, newest
            // first — see AnnouncementRules.IsActiveAt for the window itself.
            e.HasIndex(x => new { x.StartsAt, x.EndsAt });
            e.Property(x => x.Title).HasMaxLength(AnnouncementRules.MaxTitleLength).IsRequired();
            e.Property(x => x.TitleFa).HasMaxLength(AnnouncementRules.MaxTitleLength).IsRequired();
            e.Property(x => x.Body).HasMaxLength(AnnouncementRules.MaxBodyLength).IsRequired();
            e.Property(x => x.BodyFa).HasMaxLength(AnnouncementRules.MaxBodyLength).IsRequired();
            e.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
            // No navigation to User, the same reasoning SupportSession.AdminEmail documents just
            // above: the byline must survive the administrator's own account being renamed or removed.
        });

        b.Entity<AnnouncementDismissal>(e =>
        {
            // The only read this table ever serves — see the type's own doc — and what makes
            // dismissing idempotent: a second POST to the same announcement finds the row already
            // there instead of adding a duplicate for AnnouncementNotifier.
            e.HasIndex(x => new { x.AnnouncementId, x.UserId }).IsUnique();
            e.HasOne(x => x.Announcement).WithMany().HasForeignKey(x => x.AnnouncementId)
                .OnDelete(DeleteBehavior.Cascade);
            // No navigation to User for the same reason WorkspaceMembers keeps none of its own to
            // Announcement/AnnouncementDismissal: a person's dismissals are looked up by UserId alone,
            // matched against the request's own signed-in id, never enumerated off the User entity.
        });

        b.Entity<EmailVerificationToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.UsedAt, x.ExpiresAt });
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Deliberately NOT workspace-filtered — unfiltered-but-user-keyed, the same pattern ApiToken
        // and UserSession already use (doc 14 §3). It has to be: the sign-in page reads this table
        // before any workspace is known, and a filter would make every external sign-in find nothing
        // and read as "no such account".
        b.Entity<ExternalLogin>(e =>
        {
            // The identity, and the reason this table exists: one person at one provider is one row,
            // whatever address that provider reports today.
            e.HasIndex(x => new { x.Provider, x.Subject }).IsUnique();

            // And one account of each provider per person. Nothing in the product needs a second
            // Google on the same account, and allowing it would make the settings page ambiguous
            // about which row an unlink button means.
            e.HasIndex(x => new { x.UserId, x.Provider }).IsUnique();

            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MailServer>(e =>
        {
            e.Property(x => x.PublicHostname).HasMaxLength(253).IsRequired();
            e.Property(x => x.ApiBaseUrl).HasMaxLength(512).IsRequired();
            e.Property(x => x.Image).HasMaxLength(256).IsRequired();
            e.Property(x => x.ContainerName).HasMaxLength(128).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(2048);
            e.HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\" = TRUE");
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MailDomain>(e =>
        {
            e.Property(x => x.Domain).HasMaxLength(253).IsRequired();
            e.Property(x => x.ProviderObjectId).HasMaxLength(128);
            e.Property(x => x.ExternalProviderName).HasMaxLength(128);
            e.Property(x => x.ExternalAdminUrl).HasMaxLength(512);
            e.Property(x => x.ExternalImapHost).HasMaxLength(253);
            e.Property(x => x.ExternalSmtpHost).HasMaxLength(253);
            e.Property(x => x.DnsZone).HasMaxLength(8192);
            e.Property(x => x.LastError).HasMaxLength(2048);
            e.HasIndex(x => x.Domain).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.Status });
            e.HasOne(x => x.MailServer).WithMany().HasForeignKey(x => x.MailServerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MailMailbox>(e =>
        {
            e.Property(x => x.LocalPart).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.Property(x => x.ProviderObjectId).HasMaxLength(128);
            e.Property(x => x.LastError).HasMaxLength(2048);
            e.HasIndex(x => new { x.MailDomainId, x.LocalPart }).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.Status });
            e.HasOne(x => x.MailDomain).WithMany(x => x.Mailboxes).HasForeignKey(x => x.MailDomainId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Workspace>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.OwnerUserId).HasFilter("\"IsPersonal\" = TRUE").IsUnique();
            e.HasOne(x => x.OwnerUser).WithMany(u => u.OwnedWorkspaces)
                .HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<WorkspaceMember>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.UserId }).IsUnique();
            e.HasOne(x => x.Workspace).WithMany(w => w.Members).HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WorkspaceInvitation>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.Email });
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.TokenHint).HasMaxLength(16).IsRequired();
            e.HasOne(x => x.Workspace).WithMany(w => w.Invitations)
                .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GitProvider>(e =>
            e.HasMany(x => x.Repositories).WithOne(r => r.Provider).HasForeignKey(r => r.GitProviderId).OnDelete(DeleteBehavior.Cascade));

        b.Entity<App>(e =>
        {
            // Platform-wide, not per-workspace: a container is retired by matching a
            // harbora.workspace label, but the one narrow bridge for a container that predates that
            // label falls back to "no other workspace holds this slug" — which this index is what
            // makes true. Checked against production before this changed: 3 apps, 3 distinct slugs,
            // so this migration is one index with no rename step, and it fails loudly rather than
            // silently renaming anything if a duplicate ever exists on another install.
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(63).IsRequired();
            e.HasOne(x => x.GitRepository).WithMany().HasForeignKey(x => x.GitRepositoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.EnvironmentVariables).WithOne(v => v.App).HasForeignKey(v => v.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Volumes).WithOne(v => v.App).HasForeignKey(v => v.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Domains).WithOne(d => d.App).HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Deployments).WithOne(d => d.App).HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EnvironmentVariable>(e => e.HasIndex(x => new { x.AppId, x.Key }).IsUnique());

        // --- shared environment-variable groups (Sub-project 9, 2026-08-20 platform-options plan) ---
        b.Entity<ConfigGroup>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.HasMany(x => x.Entries).WithOne(v => v.ConfigGroup).HasForeignKey(v => v.ConfigGroupId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ConfigGroupEntry>(e => e.HasIndex(x => new { x.ConfigGroupId, x.Key }).IsUnique());
        b.Entity<AppConfigGroup>(e =>
        {
            e.HasIndex(x => new { x.AppId, x.ConfigGroupId }).IsUnique();
            e.HasOne(x => x.App).WithMany(a => a.ConfigGroups).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            // Restrict, not Cascade: a group with apps still attached must be refused by the named-list
            // check in ConfigGroupsController.Delete (the ProjectsController.Delete idiom) before this
            // is ever reached — the same relationship EnvironmentId has to Project, where the comment on
            // App's own FK explains why the application-level refusal has to exist regardless of what
            // the database would do on its own.
            e.HasOne(x => x.ConfigGroup).WithMany(g => g.Apps).HasForeignKey(x => x.ConfigGroupId).OnDelete(DeleteBehavior.Restrict);
        });

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

        // Restrict, not SetNull and not Cascade (P2, 2026-08-17 app-environment-management design).
        // EnvironmentId is required now, so SetNull is no longer legal — a cascade would silently
        // destroy a customer's running apps and databases along with the environment, which is the
        // outcome the original SetNull comment existed to prevent. Restrict keeps that guarantee the
        // right way: the database refuses to delete an environment that still holds a workload, and
        // every path that removes an Environment row (ProjectsController.Delete,
        // AppOperationsService.RemoveEmptyPreviewEnvironmentAsync) already checks for exactly that
        // before it tries, so this is the backstop for what those checks are supposed to guarantee,
        // not the only thing standing between a delete and a customer's data.
        b.Entity<App>(e => e.HasOne(x => x.Environment).WithMany()
            .HasForeignKey(x => x.EnvironmentId).IsRequired().OnDelete(DeleteBehavior.Restrict));
        b.Entity<ManagedService>(e => e.HasOne(x => x.Environment).WithMany()
            .HasForeignKey(x => x.EnvironmentId).IsRequired().OnDelete(DeleteBehavior.Restrict));

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

        b.Entity<Harbora.Domain.Logging.AppLogLine>(e =>
        {
            // Search and both sweeps all read "this app's lines, newest/oldest within a window" —
            // the exact shape CronRun's own index above serves for the same reason.
            e.HasIndex(x => new { x.AppId, x.Timestamp });
            // The ingestion cursor's own read: "this app's CURRENT container's newest line" — a
            // different container id must never see an old container's cursor (AppLogLine's own doc
            // explains why), so this index exists purely to make that lookup cheap.
            e.HasIndex(x => new { x.AppId, x.ContainerId, x.Timestamp });
            // The global budget trim orders across every app at once; see LogBudgetEnforcer.
            e.HasIndex(x => x.Timestamp);
            e.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
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

        // The same question one level up, and until now the summaries had nothing but their primary
        // key — a random Guid, which answers nothing anybody asks. Every chart load scanned every
        // summary of every metric of every resource, on a table built to hold a year.
        //
        // The four columns MonitoringController fixes come first and PeriodStart last, because it is
        // the only one ranged over: equality columns ahead of the range column keep the match to one
        // contiguous stretch of the index, and having PeriodStart at the end also returns the rows in
        // the ORDER BY the chart asks for, so nothing is sorted afterwards. Turning that around —
        // PeriodStart first — would make every read scan the whole window and filter.
        b.Entity<MetricRollup>(e =>
            e.HasIndex(x => new { x.ServerId, x.Name, x.ResourceRef, x.Period, x.PeriodStart }));

        // One cursor per container per server — the collector loads its whole set for a server in one
        // query and looks each container up by name, so this is the index that query actually uses.
        // Unique because two cursors for the same container would leave the delta computation reading
        // (and updating) whichever one it happened to find first.
        b.Entity<ContainerLifecycleCursor>(e =>
            e.HasIndex(x => new { x.ServerId, x.ResourceRef }).IsUnique());

        // P7 (2026-08-20 platform-options plan): at most one status page per workspace — the settings
        // screen creates it lazily on first open, so a second concurrent open must find the same row
        // rather than a second one racing it into existence.
        b.Entity<Harbora.Domain.Status.StatusPage>(e =>
        {
            e.HasIndex(x => x.WorkspaceId).IsUnique();

            // P8: at most one custom domain per status page. The FK lives on DomainName (the
            // dependent, same shape App.Domains already uses) rather than a DomainId column here —
            // HasForeignKey<DomainName> is what makes this a true one-to-one, which is also what
            // gives DomainName.StatusPageId its own unique index for free.
            e.HasOne(x => x.Domain).WithOne(d => d.StatusPage)
                .HasForeignKey<Harbora.Domain.Networking.DomainName>(d => d.StatusPageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // One row per (page, app) — choosing the same app twice would give the public page two cards
        // for one component rather than refusing the second pick.
        b.Entity<Harbora.Domain.Status.StatusPageComponent>(e =>
            e.HasIndex(x => new { x.StatusPageId, x.AppId }).IsUnique());

        // The public page's own read: one page's incidents. StartedAt trails because
        // StatusPageReport orders the loaded rows in memory (open before resolved, then newest
        // first) — the index still earns its keep by narrowing every read to one page's rows.
        b.Entity<Harbora.Domain.Status.StatusIncident>(e =>
            e.HasIndex(x => new { x.StatusPageId, x.StartedAt }));

        // Two different questions, two different indexes. IncidentService.OpenAsync/ResolveAsync ask
        // "is this exact condition, on this exact subject, already open?" on every collector tick —
        // WorkspaceId, Condition and SubjectRef are equality columns there, ClosedAt narrows to the
        // (usually singular) open row. The timeline and the bell badge ask a different question —
        // "what does this workspace have open, newest first?" — which is WorkspaceId and ClosedAt
        // equality with OpenedAt as the one ranged/sorted column, so it goes last for the same reason
        // MetricRollup's own index puts PeriodStart last.
        b.Entity<AlertIncident>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.Condition, x.SubjectRef, x.ClosedAt });
            e.HasIndex(x => new { x.WorkspaceId, x.ClosedAt, x.OpenedAt });
        });

        // 2.1 (2026-09 market-gaps round two): one row per app. UptimeChecker.CheckDueAsync's own read
        // is "which checks are due", so NextCheckAt leads; the unique AppId index is what makes "one
        // outside-in check per app" a real constraint rather than a convention nothing enforces.
        b.Entity<UptimeCheck>(e =>
        {
            e.HasIndex(x => x.AppId).IsUnique();
            e.HasIndex(x => new { x.IsEnabled, x.NextCheckAt });
            e.Property(x => x.LastDetail).HasMaxLength(1000);
        });

        // The app page and the public status page both ask "this app's own history, newest first" —
        // never a cross-app query, so AppId leads and CheckedAt narrows, the same shape
        // StatusIncident's own index gives StatusPageReport's per-page read.
        b.Entity<UptimeCheckResult>(e =>
        {
            e.HasIndex(x => new { x.AppId, x.CheckedAt });
            e.Property(x => x.Detail).HasMaxLength(1000);
        });

        // N2 (2026-08-16 notification-system spec): the persisted replacement for AlertThrottle.
        // Unique on Key, not workspace-scoped — AlertDedup.ShouldFireAsync's whole mechanism is "try
        // to insert this exact key, see if it was already there", and that is only race-safe with a
        // real constraint behind it, the same reasoning IX_ContainerLifecycleCursors_ServerId_ResourceRef
        // above already applies.
        b.Entity<AlertDedupMark>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(200);
            e.HasIndex(x => x.Key).IsUnique();
        });

        // N1 (2026-08-16 notification-system spec): a delivery row. Deliberately NOT
        // workspace-filtered, like Job — a null WorkspaceId (every transactional purpose) would
        // otherwise pass an "own it or nothing" filter for every tenant at once, which is worse than
        // no filter. The delivery log on /monitoring and the retention sweep both filter explicitly.
        b.Entity<Harbora.Domain.Notifications.NotificationDelivery>(e =>
        {
            e.Property(x => x.Subject).HasMaxLength(200);
            // Longer than Alert.LastError's 400: this is the whole message body, and a transactional
            // one carries a link. Encrypted, so the stored length already includes that overhead.
            e.Property(x => x.EncryptedBody).HasMaxLength(4000);
            e.Property(x => x.RecipientAddress).HasMaxLength(256);
            e.Property(x => x.LastError).HasMaxLength(2048);

            // The delivery log's own read: one workspace's rows, newest first. NotificationDeliveryJobHandler
            // reads by Id (the primary key) and needs no index of its own.
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
        });

        // N3 (2026-08-16 notification-system spec): a per-user copy of a workspace event. Deliberately
        // NOT workspace-filtered — unfiltered-but-user-keyed, the same pattern ApiToken already uses
        // (doc 14 §3): a workspace-only filter would still merge every member's rows into one set, so
        // every reader (the bell, /notifications, the retention sweep) filters by UserId explicitly.
        b.Entity<Harbora.Domain.Notifications.UserNotification>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Body).HasMaxLength(4000);

            // The bell's unread count and the /notifications page's paginated list are the only two
            // reads, and both start from UserId + WorkspaceId, narrow by whether ReadAt is null, and
            // sort newest first — so all four columns are in the index, ReadAt ahead of the ranged
            // CreatedAt for the same reason MetricRollup's own index puts its ranged column last.
            e.HasIndex(x => new { x.UserId, x.WorkspaceId, x.ReadAt, x.CreatedAt });
        });

        // N5 (2026-08-16 notification-system spec, "noise control"): the preference matrix. Not
        // workspace-filtered, for the same reason UserNotification is not — a preference is one
        // person's. Unique on (UserId, EventType, Channel): "an absent row means the default" only
        // holds if there is never more than one explicit choice for the same triple to disagree about.
        b.Entity<Harbora.Domain.Notifications.NotificationPreference>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.EventType, x.Channel }).IsUnique();
        });

        // N5: digest/weekly-report lines waiting to be folded into a NotificationDelivery. Not
        // workspace-filtered, same reasoning as UserNotification. The digest runner's own read is
        // "this user's still-pending rows" (DeliveryId == null); UserId leads the index for that,
        // DeliveryId narrows it, the same shape NotificationDelivery's own retention rule already
        // reads by status before age.
        b.Entity<Harbora.Domain.Notifications.NotificationDigestEntry>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Body).HasMaxLength(4000);
            e.HasIndex(x => new { x.UserId, x.DeliveryId });
        });

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

        b.Entity<ServerInstanceOffer>(e =>
        {
            // One row per server per tier. Two rows for the same pair would each claim a price and
            // whichever the dictionary happened to keep would decide the bill — a disagreement no
            // screen would show, because both rows look correct on their own.
            e.HasIndex(x => new { x.ServerId, x.InstanceSizeKey }).IsUnique();

            // Bounded to the same length a tier's key is, so a key that fits in InstanceSize.Key
            // always fits here. Without it the two could diverge and a long key would be silently
            // truncated on one side of the join.
            e.Property(x => x.InstanceSizeKey)
                .HasMaxLength(Harbora.Domain.Tenancy.InstanceSize.KeyMaxLength);

            // Deleting a server takes its price list with it. The rows name a server that no longer
            // exists otherwise, and the pricing matrix would list a host nobody can place on.
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Harbora.Domain.Storage.StorageBucket>(e =>
        {
            // A bucket belongs to a workspace and is only ever shown through it. Without the filter
            // the storage page would list every tenant's buckets to whoever opened it.
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);

            // Unique across the platform, not per workspace: the storage server has one namespace,
            // so two tenants asking for "uploads" is a collision wherever it is not caught first.
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.StoragePlan).WithMany().HasForeignKey(x => x.StoragePlanId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Harbora.Domain.Storage.StoragePlan>(e => e.Property(x => x.MonthlyPrice).HasPrecision(10, 2));

        // --- bucket attach (F5, 2026-08-21 functions-and-services plan) ---
        // The AppConfigGroup shape exactly: Restrict, not Cascade, on the bucket side — a bucket with
        // apps still attached must be refused by the named-list check in StorageController.Delete
        // (the ProjectsController.Delete idiom) before this is ever reached.
        b.Entity<Harbora.Domain.Storage.AppStorageBucket>(e =>
        {
            e.HasIndex(x => new { x.AppId, x.StorageBucketId }).IsUnique();
            e.HasOne(x => x.App).WithMany(a => a.StorageBuckets).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.StorageBucket).WithMany(sb => sb.Apps).HasForeignKey(x => x.StorageBucketId).OnDelete(DeleteBehavior.Restrict);
        });

        // --- database attach (C1, 2026-08-22 config-delivery plan) ---
        // The AppStorageBucket shape exactly: Restrict, not Cascade, on the ManagedService side — a
        // database with apps still attached must be refused by the named-list check in
        // DatabasesController.Remove (the ProjectsController.Delete idiom) before this is ever
        // reached. Unlike AppStorageBucket, Alias also carries a per-app uniqueness constraint,
        // because two databases attached to the same app is the ordinary case (see
        // AppManagedServiceAlias's own doc) where two buckets sharing a key is not.
        //
        // D1 (2026-08-25 shared-databases plan): the uniqueness that used to sit on
        // (AppId, ManagedServiceId) alone now splits into two partial indexes, one per side of
        // ManagedServiceDatabaseId being null. An attachment with a logical database (every engine
        // that has one, from here on) is unique on (AppId, ManagedServiceDatabaseId) — which is what
        // lets one app attach to two different logical databases on the very same instance, the
        // capability this plan exists to add. An attachment with none (Redis/RabbitMQ/NATS, and any
        // Postgres/MySQL/MariaDB row a migration has not reached) keeps the old guarantee verbatim on
        // (AppId, ManagedServiceId): the same instance still cannot be attached to the same app twice.
        // Postgres already treats every NULL in a unique index as distinct from every other, so
        // without the filters a plain unique index on (AppId, ManagedServiceDatabaseId) would let the
        // no-logical-database case collide silently — the exact hole these two indexes together close.
        b.Entity<Harbora.Domain.Services.AppManagedService>(e =>
        {
            e.HasIndex(x => new { x.AppId, x.ManagedServiceDatabaseId }).IsUnique()
                .HasFilter("\"ManagedServiceDatabaseId\" IS NOT NULL");
            e.HasIndex(x => new { x.AppId, x.ManagedServiceId }).IsUnique()
                .HasFilter("\"ManagedServiceDatabaseId\" IS NULL");
            e.HasIndex(x => new { x.AppId, x.Alias }).IsUnique();
            e.HasOne(x => x.App).WithMany(a => a.ManagedServices).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ManagedService).WithMany(s => s.Apps).HasForeignKey(x => x.ManagedServiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Database).WithMany(d => d.Apps).HasForeignKey(x => x.ManagedServiceDatabaseId).OnDelete(DeleteBehavior.Restrict);
        });

        // --- logical databases (D1, 2026-08-25 shared-databases plan) ---
        // Cascade on the ManagedService side, unlike AppManagedService's Restrict above: a logical
        // database only ever means something inside the instance that owns it, and ManagedServiceEngine
        // .RemoveAsync only reaches a service with none of its logical databases still attached to an
        // app (DatabasesController.Remove's named-list refusal already guarantees that before this is
        // ever reached) — so removing the instance is safe to take every logical database with it, the
        // way ConfigOverrideRule's Cascade on App already does for a row that only means something
        // inside its own parent.
        b.Entity<Harbora.Domain.Services.ManagedServiceDatabase>(e =>
        {
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
            e.HasIndex(x => new { x.ManagedServiceId, x.Name }).IsUnique();
            e.HasOne(x => x.ManagedService).WithMany(s => s.Databases).HasForeignKey(x => x.ManagedServiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // --- file overrides (C2, 2026-08-22 config-delivery plan) ---
        // Cascade on the app side, and no other side to restrict against: a rule is not a shared
        // thing another app could also point at (unlike ConfigGroup/StorageBucket/EmailProvider),
        // it only ever means something in the context of the one app it belongs to.
        b.Entity<Harbora.Domain.Configuration.ConfigOverrideRule>(e =>
        {
            e.HasOne(x => x.App).WithMany(a => a.ConfigOverrideRules).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.AppId);
        });

        // --- customer DNS (F9, same plan) ---
        b.Entity<CustomerDnsCredential>(e =>
        {
            // A workspace's own Cloudflare token, visible only through this workspace — without the
            // filter the Domains page's DNS section would resolve whichever tenant's token a request
            // happened to be scoped to, the exact platform/customer credential mixing F9 forbids.
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
            // One token per workspace: Cloudflare's own token is already scoped to whatever zones its
            // owner granted, so a second row for the same workspace would only be a second grant of
            // the same kind, not a new capability the page would know how to offer differently.
            e.HasIndex(x => x.WorkspaceId).IsUnique();
        });

        // --- BYO SMTP providers (F6, 2026-08-21 functions-and-services plan, HARBORA-0038 phase 1) ---
        b.Entity<Harbora.Domain.Email.EmailProvider>(e =>
        {
            // A provider belongs to a workspace and is only ever shown through it — the same reason
            // StorageBucket is filtered above.
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        });

        // The AppStorageBucket shape exactly (F5): Restrict, not Cascade, on the provider side — a
        // provider with apps still attached must be refused by the named-list check in
        // EmailProvidersController.Delete before this is ever reached.
        b.Entity<Harbora.Domain.Email.AppEmailProvider>(e =>
        {
            e.HasIndex(x => new { x.AppId, x.EmailProviderId }).IsUnique();
            e.HasOne(x => x.App).WithMany(a => a.EmailProviders).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.EmailProvider).WithMany(p => p.Apps).HasForeignKey(x => x.EmailProviderId).OnDelete(DeleteBehavior.Restrict);
        });

        // --- BYO Sentry/GlitchTip DSNs (1.8, 2026-09 market-gaps round two) ---
        b.Entity<Harbora.Domain.ErrorTracking.ErrorTrackingProvider>(e =>
        {
            // A provider belongs to a workspace and is only ever shown through it — the same reason
            // EmailProvider is filtered above.
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        });

        // The AppEmailProvider shape exactly (F6): Restrict, not Cascade, on the provider side — a
        // provider with apps still attached must be refused by the named-list check in
        // ErrorTrackingProvidersController.Delete before this is ever reached.
        b.Entity<Harbora.Domain.ErrorTracking.AppErrorTrackingProvider>(e =>
        {
            e.HasIndex(x => new { x.AppId, x.ErrorTrackingProviderId }).IsUnique();
            e.HasOne(x => x.App).WithMany(a => a.ErrorTrackingProviders).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ErrorTrackingProvider).WithMany(p => p.Apps).HasForeignKey(x => x.ErrorTrackingProviderId).OnDelete(DeleteBehavior.Restrict);
        });

        // --- private-registry pull credentials (1.3, 2026-09 market-gaps round two) ---
        b.Entity<Harbora.Domain.Registries.RegistryCredential>(e =>
        {
            // A workspace's own credential, visible only through it — the same reason EmailProvider
            // and StorageBucket are filtered above.
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);

            // At most one credential per registry host per workspace. This is the whole of what makes
            // matching a pulled image to a credential deterministic — RegistryCredentialsController
            // refuses to create a second one for a host that already has one, so there is never a
            // "which one wins" question for DeploymentPipeline.ResolveRegistryCredentialAsync to
            // answer at pull time, only "is there one or not".
            e.HasIndex(x => new { x.WorkspaceId, x.RegistryHost }).IsUnique();
        });

        // Deliberately NOT workspace-filtered, unlike StorageBucket just above. The route that
        // redeems this token runs with no session and so no workspace in scope at all — a filter
        // here would make every redemption match nothing, which is the same failure mode the AI
        // gateway's key lookup and the node channel already avoid for the same reason. The tenant
        // check happens once, at mint time, through the app's own filtered collection; this table is
        // reached afterwards by the token's hash alone.
        b.Entity<Harbora.Domain.Storage.VolumeDownloadToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            // 1024, matching VolumePath.MaxLength — the longest path VolumePath.Normalise ever hands
            // back, so nothing this column stores can be longer than what was validated.
            e.Property(x => x.Path).HasMaxLength(1024).IsRequired();
            e.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Volume>().WithMany().HasForeignKey(x => x.VolumeId).OnDelete(DeleteBehavior.Cascade);
        });

        // Sub-project 10: same reasoning as VolumeDownloadToken just above — the route that redeems
        // this token runs with no session, so no workspace filter here either. The cascade on Backup
        // means a self-serve export's artifact and the link that reaches it are removed together,
        // whether that happens by hand or by BackupEngine.EnforceRetentionAsync's expiry sweep.
        b.Entity<BackupDownloadToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasOne<Backup>().WithMany().HasForeignKey(x => x.BackupId).OnDelete(DeleteBehavior.Cascade);
        });
        // Entitlements. No workspace query filter here, and that is the decision rather than an
        // omission: these rows are platform configuration read by the cron scheduler and the event
        // bus, neither of which has a session. A filtered table read without one comes back empty,
        // and empty reads as "nobody is entitled to anything" — the failure that looks like success.
        b.Entity<Harbora.Domain.Features.FeatureGrant>(e =>
        {
            e.HasIndex(x => new { x.Scope, x.TargetId, x.FeatureKey }).IsUnique();
            e.Property(x => x.FeatureKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.Note).HasMaxLength(500);
        });

        b.Entity<Harbora.Domain.Functions.FunctionDefinition>(e =>
        {
            // Unique inside the app, not platform-wide: the slug is an address within one host, and
            // two customers both having a "webhook" function is the normal case.
            e.HasIndex(x => new { x.AppId, x.Slug }).IsUnique();
            e.HasIndex(x => x.NextRunAt);
            // F2: what the queue consumer's reconciliation pass reads every tick — every enabled
            // queue-triggered function, platform-wide, with no session. Narrow on Trigger first: this
            // is a tiny slice of an otherwise Http/Cron/Event-heavy table.
            e.HasIndex(x => new { x.Trigger, x.IsEnabled });
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            e.Property(x => x.Route).HasMaxLength(200);
            e.Property(x => x.CronExpression).HasMaxLength(120);
            e.Property(x => x.EventKey).HasMaxLength(64);
            e.Property(x => x.QueueName).HasMaxLength(255);
            e.Property(x => x.QueueLastError).HasMaxLength(1000);
            e.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        });

        // F2 (2026-08-21 functions-and-services plan, "Queue-triggered functions"): a message the
        // consumer could not get accepted twice in a row. See the entity's own doc for why this is a
        // separate table from FunctionInvocation rather than a flag on it.
        b.Entity<Harbora.Domain.Functions.FunctionQueueDeadLetter>(e =>
        {
            e.HasIndex(x => new { x.FunctionId, x.CreatedAt });
            e.Property(x => x.QueueName).HasMaxLength(255).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.HasOne<Harbora.Domain.Functions.FunctionDefinition>().WithMany()
                .HasForeignKey(x => x.FunctionId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        });

        b.Entity<Harbora.Domain.Functions.FunctionInvocation>(e =>
        {
            // The history page reads one function's most recent runs, and the sweeper deletes the
            // oldest across all of them; both are this index.
            e.HasIndex(x => new { x.FunctionId, x.StartedAt });
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasOne<Harbora.Domain.Functions.FunctionDefinition>().WithMany()
                .HasForeignKey(x => x.FunctionId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        });

        b.Entity<Harbora.Domain.Functions.FunctionCodeRevision>(e =>
        {
            // The editor reads one function's revisions newest-first, and FunctionAppService prunes
            // the same way — both are this index, same shape as FunctionInvocation's above.
            e.HasIndex(x => new { x.FunctionId, x.CreatedAt });
            e.HasOne<Harbora.Domain.Functions.FunctionDefinition>().WithMany()
                .HasForeignKey(x => x.FunctionId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        });

        b.Entity<Harbora.Domain.Functions.FunctionCustomEventKey>(e =>
        {
            // One row per key per workspace: the ingest endpoint upserts this on every call, never
            // inserts a duplicate.
            e.HasIndex(x => new { x.WorkspaceId, x.Key }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(64).IsRequired();
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        });

        b.Entity<Harbora.Domain.Tenancy.Plan>(e => e.Property(x => x.MonthlyPrice).HasPrecision(10, 2));
        b.Entity<Harbora.Domain.Tenancy.UsageRecord>(e => e.HasIndex(x => new { x.WorkspaceId, x.Period }).IsUnique());
        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            // The customer's support-access page asks "what did support do while they were me" once
            // per session; without this it is a table scan of every audit row the platform ever
            // wrote, on a page a worried customer opens.
            e.HasIndex(x => x.SupportSessionId);

            // HARBORA-0056: deliberately NOT workspace-filtered, the same reasoning Job's and
            // NotificationDelivery's own remarks give — WorkspaceId is null for most rows (every
            // platform-level action, and every row written before this column existed), so an "own
            // it or nothing" filter would pass all of those to whichever workspace happened to be
            // ambient instead of to nobody. AuditController (platform-wide) and the workspace-scoped
            // WorkspacesController.AuditLog both filter explicitly instead.
            //
            // The workspace-scoped reader's own read: one workspace's rows, newest first.
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
        });

        b.Entity<Harbora.Domain.Billing.Wallet>(e =>
        {
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
            e.HasIndex(x => x.WorkspaceId).IsUnique();
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
        });

        b.Entity<Harbora.Domain.Billing.BillingLedgerEntry>(e =>
        {
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
            e.Property(x => x.ResourceName).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(400);

            // Reading a bill: one workspace, newest first.
            e.HasIndex(x => new { x.WorkspaceId, x.BillingHour });

            // Reading one resource's history: "what did this app cost me".
            e.HasIndex(x => new { x.WorkspaceId, x.ResourceType, x.ResourceId });

            // The idempotency key. Covers BOTH kinds the tick writes: scoping it to Charge alone
            // would leave PlanMinimumTopUp free to be written twice by a retried tick, which is the
            // same double-charge this index exists to prevent, arriving through the one line with no
            // resource behind it. Credit and Adjustment are made by a person and may legitimately
            // repeat within an hour, so they are outside the filter.
            //
            // PlanMinimumTopUp rows carry a null ResourceId by design (BilledResourceType.PlanBase's
            // doc comment) — there is no resource behind that line. Postgres's default treats two
            // NULLs as distinct, which would let a retried tick write the plan-minimum line twice
            // right through this index. AreNullsDistinct(false) closes that: NULLS NOT DISTINCT
            // (PG15+) makes the two NULL ResourceIds collide like any other equal value.
            e.HasIndex(x => new { x.WorkspaceId, x.ResourceType, x.ResourceId, x.BillingHour })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasFilter("\"Kind\" IN (0, 2)");
        });

        b.Entity<Harbora.Domain.Billing.BillingRun>(e =>
        {
            e.HasIndex(x => x.BillingHour).IsUnique();
            e.Property(x => x.FailureSummary).HasMaxLength(4000);
        });

        b.Entity<Harbora.Domain.Billing.BillingVoucher>(e =>
        {
            e.HasIndex(x => x.CodeHash).IsUnique();
            e.HasIndex(x => new { x.IsDisabled, x.RedeemedAt, x.ExpiresAt });
            // Settles the race a read cannot: SignupTrialCreditService always names the workspace
            // owner as CreatedByUserId (see its own class comment for why), so at most one
            // IsTrialCredit row may ever exist per owner. Two concurrent grants for the same new
            // account both pass a pre-check read and both try to insert — this index lets exactly
            // one through and refuses the other with 23505, the same shape WalletService's own
            // ledger primary key already uses to settle a credit race. Filtered so it says nothing
            // about ordinary support vouchers, which repeat CreatedByUserId (the admin) freely.
            e.HasIndex(x => x.CreatedByUserId)
                .IsUnique()
                .HasFilter("\"IsTrialCredit\"")
                .HasDatabaseName("IX_BillingVouchers_TrialCreditOwner");
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.Property(x => x.CodeHint).HasMaxLength(8);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Note).HasMaxLength(200);
            e.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
        });

        b.Entity<PasswordResetToken>(e =>
        {
            // Looked up by the hash of whatever arrived in the URL, on every attempt.
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Harbora.Domain.Jobs.Job>(e =>
        {
            // The worker's hot path: oldest Pending job first. NextAttemptAt is filtered on top of
            // this and gets no index of its own — it only ever excludes the handful of rows serving
            // a retry backoff, which this index has already narrowed to Pending.
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            // At most one live dispatch may point at a billing hour. Completed jobs stay as the
            // audit trail and a later retry may create a new row for the same BillingRun.
            e.HasIndex(x => new { x.Kind, x.TargetId })
                .IsUnique()
                .HasFilter("\"Kind\" = 9 AND \"Status\" IN (0, 1)");
            // Finding the live job for a deployment/backup when cancelling or reconciling.
            e.HasIndex(x => new { x.TargetId, x.Status });
            // /activity's own read (P5): one workspace's rows, newest first. WorkspaceId is not a
            // query filter (see the property's own doc comment) but it is a WHERE clause every
            // caller who wants one tenant's jobs writes by hand, so it earns an index the same way
            // NotificationDelivery's WorkspaceId does.
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
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
            e.Property(x => x.StagingPath).HasMaxLength(1024);

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

            // One active backup per target, enforced by the database rather than by a query.
            //
            // BackupSnapshotService checks first and gives a sentence a person can act on, but that
            // check is a read followed by an insert: two requests — a manual one and the scheduler's
            // — can both pass it and both write a row. Then two runs stage the same 200 GB volume
            // and disagree about what the data looked like. The filter is what keeps this to
            // "active": without it a target could be backed up exactly once, ever.
            //
            // Numeric literals because that is what the column holds, and BackupSnapshotStatus's
            // wire values are frozen: 0 Pending, 1 Preparing, 2 Running.
            e.HasIndex(x => new { x.WorkspaceId, x.TargetType, x.TargetRef })
                .IsUnique()
                .HasFilter("\"Status\" IN (0, 1, 2)")
                .HasDatabaseName("IX_BackupSnapshots_ActiveTarget");
        });

        b.Entity<Harbora.Modules.Backup.Domain.RestoreJob>(e =>
        {
            // 1024, unchanged. This column carries a btree unique index below and a btree index row
            // is capped near 2704 bytes, which 1024 multi-byte characters could exceed — but the
            // answer to that is RestoreService refusing anything over
            // RestoreJob.MaxDestinationLength before the insert, not an ALTER COLUMN that an
            // install with a longer row already stored would meet as a failed boot.
            e.Property(x => x.Destination)
                .HasMaxLength(Harbora.Modules.Backup.Domain.RestoreJob.StoredDestinationLength)
                .IsRequired();
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

            // One active restore per destination, for the same reason and with the same race.
            // Deliberately NOT scoped by workspace: a destination is a resolved absolute path or a
            // managed database's id, both of which name one thing on the machine. Two tenants
            // racing for the same directory is precisely the case a workspace-scoped index would
            // wave through. 0 Pending, 1 Running.
            e.HasIndex(x => x.Destination)
                .IsUnique()
                .HasFilter("\"Status\" IN (0, 1)")
                .HasDatabaseName("IX_RestoreJobs_ActiveDestination");
        });

        b.Entity<IdempotencyRecord>(e =>
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
        b.Entity<Harbora.Domain.Logging.AppLogLine>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Route>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<ManagedService>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<MailDomain>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<MailMailbox>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
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
        b.Entity<IdempotencyRecord>()
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
        // C1 (2026-08-27 "warn before the refusal"): a real column default, the same reasoning as
        // User.TimeZoneId above — an installation with Alert rows that predate this column should get
        // the same "on" every other event flag already ships with, not the CLR false the ALTER TABLE
        // would otherwise backfill.
        b.Entity<Alert>().Property(x => x.OnQuotaWarning).HasDefaultValue(true);
        // 2.1 (2026-09 market-gaps round two): same reasoning as OnQuotaWarning immediately above — an
        // installation with Alert rows that predate this column gets the same "on" every other event
        // flag ships with, not the CLR false the ALTER TABLE would otherwise backfill.
        b.Entity<Alert>().Property(x => x.OnUptimeCheckFailed).HasDefaultValue(true);
        b.Entity<AlertIncident>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        // 2.1 (2026-09 market-gaps round two): filtered the same way Alert/AlertIncident are —
        // UptimeChecker is a sessionless background path and scopes every write explicitly by
        // check.WorkspaceId with IgnoreQueryFilters(), never relying on this filter's always-Guid.Empty
        // ambient ID; the settings screen and the app page both read under the normal signed-in scope.
        b.Entity<UptimeCheck>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<UptimeCheckResult>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        // P6 (2026-08-20 platform-options plan): unlike NotificationDelivery, every EventSubscription
        // and EventDelivery row always belongs to exactly one workspace (no platform-level rows), so
        // both are filtered the same way Alert/AlertIncident are. The background dispatcher scopes
        // explicitly with IgnoreQueryFilters + WorkspaceId == — see EventDispatcher.
        b.Entity<Harbora.Domain.Notifications.EventSubscription>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Harbora.Domain.Notifications.EventDelivery>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        // P7 (2026-08-20 platform-options plan): filtered like EventSubscription/EventDelivery above,
        // for the identical reason — the settings screen reads these under the normal ambient scope,
        // and the anonymous public route has none (Guid.Empty, deny-by-default) so it must scope
        // every read explicitly with IgnoreQueryFilters() + WorkspaceId == the workspace the host
        // resolved to. StatusPageComponent and StatusIncident both carry their own denormalised
        // WorkspaceId rather than being reached only through StatusPage, for the same reason
        // EventDelivery does: the anonymous route must never depend on a join through a row that
        // could be momentarily absent.
        b.Entity<Harbora.Domain.Status.StatusPage>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Harbora.Domain.Status.StatusPageComponent>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Harbora.Domain.Status.StatusIncident>()
            .HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<GitProvider>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<WorkspaceMember>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<WorkspaceInvitation>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
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
        // protection. DomainName now has two possible parents (App, and StatusPage as of P8) rather
        // than one; the reasoning is unchanged because both are filtered and a row is always reached
        // through whichever one it belongs to, never both.

        // ConfigGroup is workspace-level like GitProvider, so it is filtered directly. Its entries and
        // its join to App follow the EnvironmentVariable rule above instead: reached only through the
        // group (or through App, itself filtered), so they stay unfiltered on purpose.
        b.Entity<ConfigGroup>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
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
