using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Networking;
using Harbora.Domain.Status;
using Harbora.Infrastructure.Monitoring;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="CertificateWatcher"/> now also watches a status page's own custom domain (sub-project 8,
/// 2026-08-20 platform-options plan) — the same watcher, not a second one, because a status page's
/// certificate quietly failing to renew deserves the identical warning an app owner already gets.
/// </summary>
public sealed class CertificateWatcherStatusPageDomainTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Workspace = Guid.CreateVersion7();

    private readonly ServiceProvider _provider;
    private readonly HarboraDbContext _db;

    private sealed class FakeDomainInspector(DateTimeOffset? expiresAt) : IDomainInspector
    {
        public Task<DomainStatus> InspectAsync(string host, CancellationToken ct) => Task.FromResult(new DomainStatus(
            host, DomainReadiness.Ready, "ok", null,
            new DomainProbe([], [], true, "CN=" + host, "issuer", expiresAt)));
    }

    public CertificateWatcherStatusPageDomainTests()
    {
        // Computed once, outside the configure lambda — AddDbContext re-invokes that lambda for every
        // scope's own DbContext, and a Guid.NewGuid() inside it hands each one a different, empty
        // in-memory database (the exact trap CertificateWatcherIncidentTests' own constructor notes).
        var dbName = "cert-watcher-statuspage-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IncidentService>();
        services.AddScoped<AlertDedup>();
        services.AddSingleton<IDomainInspector>(new FakeDomainInspector(Now.AddDays(10))); // inside the 14-day window
        services.AddSingleton<INotificationService>(new RecordingNotificationService());
        services.AddSingleton<ISystemClock>(new FixedClock(Now));

        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<HarboraDbContext>();

        var pageId = Guid.CreateVersion7();
        _db.StatusPages.Add(new StatusPage { Id = pageId, WorkspaceId = Workspace, IsEnabled = true });
        _db.Domains.Add(new DomainName { Host = "status.acme.example", StatusPageId = pageId, SslEnabled = true });
        _db.SaveChanges();
    }

    [Fact]
    public async Task A_status_pages_own_domain_close_to_expiry_opens_an_incident_on_its_workspace()
    {
        var watcher = new CertificateWatcher(
            _provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CertificateWatcher>.Instance);

        await watcher.CheckAllAsync(default);

        var incident = _db.AlertIncidents.AsNoTracking().SingleOrDefault();
        incident.Should().NotBeNull("a status page's certificate is watched the same way an app's is");
        incident!.WorkspaceId.Should().Be(Workspace);
        incident.SubjectRef.Should().Be("status.acme.example");

        var record = _db.Certificates.AsNoTracking().Single(c => c.Host == "status.acme.example");
        record.Status.Should().Be(CertificateStatus.Issued);
    }

    public void Dispose() => _provider.Dispose();
}
