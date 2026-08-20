using Harbora.Application.Abstractions;
using Harbora.Data;
using System.Reflection;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Dashboard;

/// <summary>
/// Reads the workspace's state and hands it to <see cref="AttentionRules"/>.
///
/// Everything here is a stored fact. Nothing probes the network on a page load: a dashboard that waits
/// on DNS and TLS for every domain is a dashboard nobody opens. The certificate facts come from the
/// daily watcher, which does perform real handshakes and records what it found.
/// </summary>
public sealed class AttentionService(
    HarboraDbContext db, ISystemClock clock, IOptions<Monitoring.MonitoringOptions> monitoringOptions)
{
    /// <summary>How far back a failure still counts as news.</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);

    /// <param name="isOperator">
    /// Whether this account can act on platform-wide news. A customer has no update button to
    /// reach, and telling them their provider's panel is out of date is not their problem.
    /// </param>
    public async Task<IReadOnlyList<AttentionItem>> BuildAsync(
        Guid workspaceId, CancellationToken ct, bool isOperator = false)
    {
        var since = clock.UtcNow - RecentWindow;

        // A failed deployment matters while it is still the app's most recent one. A failure that was
        // followed by a success is history, and history does not belong on a dashboard.
        var failedDeployments = await db.Deployments
            .Where(d => d.WorkspaceId == workspaceId && d.Status == DeploymentStatus.Failed
                        && d.CreatedAt >= since
                        && !db.Deployments.Any(later => later.AppId == d.AppId && later.Number > d.Number
                                                        && later.Status == DeploymentStatus.Succeeded))
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new { App = d.App!.Name, d.Id, d.ErrorMessage })
            .Take(5)
            .ToListAsync(ct);

        var crashed = await db.Apps
            .Where(a => a.WorkspaceId == workspaceId && a.Status == AppStatus.Crashed)
            .Select(a => new { a.Name, a.Id })
            .Take(5)
            .ToListAsync(ct);

        var failedBackups = await db.Backups
            .Where(b => b.WorkspaceId == workspaceId && b.Status == BackupStatus.Failed && b.CreatedAt >= since)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new { b.TargetRef, b.Type, b.ErrorMessage })
            .Take(3)
            .ToListAsync(ct);

        // P4 (2026-08-17 app-environment-management design): a failed provision used to say only
        // Status = Failed on its own page and nothing here — the same gap a failed deployment or a
        // failed backup never had.
        var failedServices = await db.ManagedServices
            .Where(s => s.WorkspaceId == workspaceId && s.Status == ServiceStatus.Failed)
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new { s.Name, s.Id, s.ErrorMessage })
            .Take(3)
            .ToListAsync(ct);

        var brokenAlerts = await db.Alerts
            .Where(a => a.WorkspaceId == workspaceId && a.IsEnabled && a.LastError != null)
            .Select(a => new { a.Name, a.LastError })
            .Take(3)
            .ToListAsync(ct);

        var brokenDeliveries = await db.BackupDeliveries
            .Where(d => d.WorkspaceId == workspaceId && d.IsEnabled && d.LastError != null)
            .Select(d => new { d.Name, d.LastError })
            .Take(3)
            .ToListAsync(ct);

        // P6 (2026-08-20 platform-options plan): an event subscription whose deliveries keep failing
        // surfaces here exactly like a broken Alert or backup-delivery channel does — extending
        // ChannelKind rather than a second "broken channel" list.
        var brokenEventSubscriptions = await db.EventSubscriptions
            .Where(s => s.WorkspaceId == workspaceId && s.IsEnabled && s.LastError != null)
            .Select(s => new { s.Name, s.LastError })
            .Take(3)
            .ToListAsync(ct);

        // Recorded by the certificate watcher's daily TLS handshake, not probed here.
        var hosts = await db.Domains
            .Where(d => d.SslEnabled && d.App!.WorkspaceId == workspaceId)
            .Select(d => d.Host)
            .ToListAsync(ct);

        var certificates = hosts.Count == 0
            ? []
            : await db.Certificates
                .Where(c => hosts.Contains(c.Host)
                            && (c.Status == CertificateStatus.Failed || c.Status == CertificateStatus.Expired
                                || (c.ExpiresAt != null && c.ExpiresAt < clock.UtcNow.AddDays(14))))
                .Select(c => new { c.Host, c.Status, c.ExpiresAt, c.LastError })
                .Take(5)
                .ToListAsync(ct);

        var appIds = await db.Apps.Where(a => a.WorkspaceId == workspaceId).Select(a => a.Id).ToListAsync(ct);
        var neverDeployed = await db.Apps
            .Where(a => a.WorkspaceId == workspaceId && a.ActiveDeploymentId == null)
            .Select(a => new { a.Name, a.Id })
            .Take(3)
            .ToListAsync(ct);

        var disk = await DiskRatioAsync(ct);

        return AttentionRules.Build(
            new AttentionFacts
            {
                FailedDeployments = failedDeployments.Select(d => (d.App, d.Id, d.ErrorMessage)).ToList(),
                CrashedApps = crashed.Select(a => (a.Name, a.Id)).ToList(),
                FailedBackups = failedBackups.Select(b =>
                    (string.IsNullOrWhiteSpace(b.TargetRef) ? b.Type.ToString() : b.TargetRef, b.ErrorMessage)).ToList(),
                FailedServices = failedServices.Select(s => (s.Name, s.Id, s.ErrorMessage)).ToList(),
                BrokenChannels =
                    brokenAlerts.Select(a => (a.Name, ChannelKind.Alert, a.LastError!))
                        .Concat(brokenDeliveries.Select(d => (d.Name, ChannelKind.BackupDelivery, d.LastError!)))
                        .Concat(brokenEventSubscriptions.Select(s => (s.Name, ChannelKind.EventSubscription, s.LastError!)))
                        .ToList(),
                CertificateProblems = certificates.Select(c => DescribeCertificate(c.Host, c.Status, c.ExpiresAt, c.LastError)).ToList(),
                DiskUsedRatio = disk,
                UpdateAvailableTag = isOperator ? await NewerReleaseAsync(ct) : null,
                NeverDeployed = neverDeployed.Select(a => (a.Name, a.Id)).ToList(),
                HasAnyApp = appIds.Count > 0,
                HasAnyBackupSchedule = await db.BackupSchedules.AnyAsync(s => s.WorkspaceId == workspaceId && s.IsEnabled, ct)
            },
            // Same figure the disk-warning alert and the monitoring page's own banner use — see
            // MonitoringOptions.DiskWarnRatio for why all three deliberately read one configured
            // number rather than each carrying its own copy.
            monitoringOptions.Value.DiskWarnRatio);
    }

    /// <summary>
    /// The certificate's problem as a fact. The prose lives in the view's language, not here — the
    /// only text that travels through is a summarised issuance error, which is data.
    /// </summary>
    private (string Host, CertificateIssue Issue, string? Argument) DescribeCertificate(
        string host, CertificateStatus status, DateTimeOffset? expiresAt, string? error)
    {
        if (status == CertificateStatus.Failed)
            return (host, CertificateIssue.IssueFailed, AttentionRules.Summarise(error));
        if (status == CertificateStatus.Expired || (expiresAt is not null && expiresAt <= clock.UtcNow))
            return (host, CertificateIssue.Expired, expiresAt?.ToString("yyyy-MM-dd"));

        var days = expiresAt is null ? 0 : (int)(expiresAt.Value - clock.UtcNow).TotalDays;
        return (host, CertificateIssue.ExpiringSoon, days.ToString());
    }

    /// <summary>
    /// The tag the daily check last saw, when it is genuinely ahead of this build. The comparison
    /// lives in <see cref="Maintenance.PanelVersion"/> so "newer" is decided in one tested place
    /// rather than by whatever string the API happened to return.
    /// </summary>
    private async Task<string?> NewerReleaseAsync(CancellationToken ct)
    {
        var latest = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == Harbora.Domain.Settings.SettingKeys.UpdateLatestTag)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        var running = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return Maintenance.PanelVersion.IsNewer(running, latest) ? latest : null;
    }

    /// <summary>Latest disk sample from the collector; 0 when nothing has been sampled yet.</summary>
    private async Task<double> DiskRatioAsync(CancellationToken ct)
    {
        var used = await LatestAsync("disk.used", ct);
        var total = await LatestAsync("disk.total", ct);
        return total > 0 ? used / total : 0;
    }

    private async Task<double> LatestAsync(string name, CancellationToken ct) =>
        await db.MonitoringMetrics
            .Where(m => m.Name == name && m.ResourceRef == null)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => m.Value)
            .FirstOrDefaultAsync(ct);
}
