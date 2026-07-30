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
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<HostPortAllocation> HostPortAllocations => Set<HostPortAllocation>();
    public DbSet<GitProvider> GitProviders => Set<GitProvider>();
    public DbSet<GitRepository> GitRepositories => Set<GitRepository>();
    public DbSet<App> Apps => Set<App>();
    public DbSet<EnvironmentVariable> EnvironmentVariables => Set<EnvironmentVariable>();
    public DbSet<Volume> Volumes => Set<Volume>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<DeploymentLog> DeploymentLogs => Set<DeploymentLog>();
    public DbSet<DomainName> Domains => Set<DomainName>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<ManagedService> ManagedServices => Set<ManagedService>();
    public DbSet<BackupDestination> BackupDestinations => Set<BackupDestination>();
    public DbSet<Backup> Backups => Set<Backup>();
    public DbSet<BackupSchedule> BackupSchedules => Set<BackupSchedule>();
    public DbSet<BackupDelivery> BackupDeliveries => Set<BackupDelivery>();
    public DbSet<MonitoringMetric> MonitoringMetrics => Set<MonitoringMetric>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AppTemplate> AppTemplates => Set<AppTemplate>();
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

        b.Entity<Deployment>(e =>
        {
            e.HasIndex(x => new { x.AppId, x.Number }).IsUnique();
            // Every deployment read goes through the workspace filter.
            e.HasIndex(x => x.WorkspaceId);
            e.HasMany(x => x.Logs).WithOne(l => l.Deployment).HasForeignKey(l => l.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeploymentLog>(e => e.HasIndex(x => new { x.DeploymentId, x.Sequence }));

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

        DeclareApplicationGeneratedKeys(b);
        ApplyWorkspaceFilters(b);
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
        b.Entity<Alert>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<GitProvider>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<WorkspaceMember>().HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
        b.Entity<Harbora.Domain.Tenancy.UsageRecord>()
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
