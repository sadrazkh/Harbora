using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Raises <see cref="AlertEvent.SslExpiring"/> for certificates that are running out.
///
/// The alert rule already had a checkbox in the UI and a branch in the notification router — but
/// nothing anywhere raised the event, so a user who ticked "tell me when SSL is expiring" was
/// promised something that could never happen.
///
/// The threshold is meaningful rather than arbitrary: Let's Encrypt certificates last 90 days and
/// Traefik renews at 30 remaining, so a certificate still inside the 14-day window is evidence that
/// renewal is failing — usually because port 80 stopped being reachable or DNS moved. That is worth
/// waking someone for; a healthy certificate never gets close.
/// </summary>
public sealed class CertificateWatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<CertificateWatcher> logger) : BackgroundService
{
    /// <summary>Daily. Renewal problems play out over days, and each pass is a real TLS handshake per domain.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Let the panel finish starting before making outbound connections.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await CheckAllAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Certificate check failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var inspector = scope.ServiceProvider.GetRequiredService<IDomainInspector>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();

        // Only domains we actually issue certificates for; one with SSL off has nothing to expire.
        var domains = await db.Domains
            .Where(d => d.SslEnabled && d.App != null)
            .Select(d => new { d.Host, d.App!.Name, d.App.WorkspaceId })
            .ToListAsync(ct);

        foreach (var domain in domains)
        {
            var status = await inspector.InspectAsync(domain.Host, ct);

            // Record what the handshake actually found. The Certificates table existed from the first
            // migration and nothing had ever written to it, so every screen that wanted to say
            // something about a certificate had nothing to say. This is the only place that performs a
            // real handshake, so it is the place that knows.
            await RecordAsync(db, domain.Host, status, clock.UtcNow, ct);

            if (CertificateAlert.Evaluate(domain.Host, domain.Name,
                    status.Probe.CertificateExpiresAt, clock.UtcNow) is not { } alert) continue;

            logger.LogWarning("Certificate for {Host} expires {Expiry:yyyy-MM-dd}.",
                domain.Host, status.Probe.CertificateExpiresAt);
            await notifications.NotifyAsync(domain.WorkspaceId, AlertEvent.SslExpiring,
                alert.Severity, alert.Headline, alert.Detail, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Upserts one row per host with what was observed. Never throws into the check.</summary>
    private static async Task RecordAsync(
        HarboraDbContext db, string host, DomainStatus status, DateTimeOffset now, CancellationToken ct)
    {
        var expiry = status.Probe.CertificateExpiresAt;
        var record = await db.Certificates.FirstOrDefaultAsync(c => c.Host == host, ct);
        if (record is null)
        {
            record = new Harbora.Domain.Networking.Certificate { Host = host };
            db.Certificates.Add(record);
        }

        record.ExpiresAt = expiry;
        record.Issuer = status.Probe.CertificateIssuer ?? record.Issuer;
        record.UpdatedAt = now;

        (record.Status, record.LastError) = expiry switch
        {
            null when status.Readiness == DomainReadiness.Ready => (CertificateStatus.Issued, null),
            // No certificate and not ready: the domain check knows why, and its wording is better than
            // anything invented here.
            null => (CertificateStatus.Failed, (string?)status.Summary),
            _ when expiry <= now => (CertificateStatus.Expired, null),
            _ => (CertificateStatus.Issued, null)
        };

        if (record.Status == CertificateStatus.Issued && record.IssuedAt is null) record.IssuedAt = now;
    }
}
