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

public class HarboraDbContext(DbContextOptions<HarboraDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Server> Servers => Set<Server>();
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
    public DbSet<MonitoringMetric> MonitoringMetrics => Set<MonitoringMetric>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AppTemplate> AppTemplates => Set<AppTemplate>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Setting> Settings => Set<Setting>();

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
            e.HasMany(x => x.Logs).WithOne(l => l.Deployment).HasForeignKey(l => l.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeploymentLog>(e => e.HasIndex(x => new { x.DeploymentId, x.Sequence }));

        b.Entity<DomainName>(e =>
        {
            e.HasIndex(x => x.Host).IsUnique();
            e.HasOne(x => x.Certificate).WithMany().HasForeignKey(x => x.CertificateId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Route>(e => e.HasIndex(x => new { x.Host, x.PathPrefix }));
        b.Entity<Certificate>(e => e.HasIndex(x => x.Host));
        b.Entity<ManagedService>(e => e.HasIndex(x => x.ContainerName).IsUnique());
        b.Entity<MonitoringMetric>(e => e.HasIndex(x => new { x.ServerId, x.Name, x.Timestamp }));
        b.Entity<AppTemplate>(e => e.HasIndex(x => x.Key).IsUnique());
        b.Entity<Setting>(e => e.HasIndex(x => x.Key).IsUnique());
        b.Entity<AuditLog>(e => e.HasIndex(x => x.CreatedAt));
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
