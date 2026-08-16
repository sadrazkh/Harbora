using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Infrastructure.Monitoring;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="CertificateWatcher"/> is one of the two conditions M4 adds new open/close wiring for
/// (disk is the other) rather than reusing an existing "free" resolve — see its own note: a certificate
/// that stops being close to expiry is exactly as re-evaluated, once a day, as a threshold is every
/// 30 seconds.
/// </summary>
public class CertificateWatcherIncidentTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Workspace = Guid.CreateVersion7();

    private readonly ServiceProvider _provider;
    private readonly CertificateWatcher _watcher;
    private readonly FakeDomainInspector _inspector = new();
    private readonly RecordingNotificationService _notifications = new();
    private readonly HarboraDbContext _db;

    public CertificateWatcherIncidentTests()
    {
        // The database name must be computed ONCE, outside the configure lambda: AddDbContext calls
        // that lambda again every time a new scope builds its own DbContext, and Guid.NewGuid() inside
        // it would hand each scope a different, empty in-memory database — exactly the trap that made
        // this fixture look like it was writing to one store and reading from another.
        var dbName = "cert-watcher-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IncidentService>();
        services.AddSingleton<IDomainInspector>(_inspector);
        services.AddSingleton<INotificationService>(_notifications);
        services.AddSingleton<ISystemClock>(new FixedClock(Now));

        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<HarboraDbContext>();

        var app = new Harbora.Domain.Apps.App
        {
            WorkspaceId = Workspace, Name = "shop", Slug = "shop", ServerId = Guid.CreateVersion7()
        };
        _db.Apps.Add(app);
        _db.Domains.Add(new Harbora.Domain.Networking.DomainName
        {
            Host = "shop.example.com", AppId = app.Id, SslEnabled = true
        });
        _db.SaveChanges();

        _watcher = new CertificateWatcher(
            _provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CertificateWatcher>.Instance);
    }

    private sealed class FakeDomainInspector : IDomainInspector
    {
        public DateTimeOffset? ExpiresAt { get; set; }

        public Task<DomainStatus> InspectAsync(string host, CancellationToken ct) => Task.FromResult(new DomainStatus(
            host, DomainReadiness.Ready, "ok", null,
            new DomainProbe([], [], true, "CN=" + host, "issuer", ExpiresAt)));
    }

    /// <summary>
    /// Every assertion below reads through <c>.AsNoTracking()</c>. <c>CheckAllAsync</c> does its own
    /// work inside its own child scope, over its own <c>HarboraDbContext</c> instance — the same
    /// database, a different context — and a tracking read via <see cref="_db"/> after that write
    /// would hand back whatever this context's identity map already cached from an earlier query in
    /// the same test, not what the store actually holds now.
    /// </summary>
    private AlertIncident? LoadIncident() =>
        _db.AlertIncidents.AsNoTracking().SingleOrDefault();

    [Fact]
    public async Task A_certificate_inside_the_renewal_window_opens_an_incident()
    {
        _inspector.ExpiresAt = Now.AddDays(10); // inside the 14-day renewal window

        await _watcher.CheckAllAsync(default);

        var incident = LoadIncident();
        incident.Should().NotBeNull();
        incident!.Condition.Should().Be(AlertEvent.SslExpiring);
        incident.SubjectRef.Should().Be("shop.example.com");
        incident.ClosedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_certificate_that_renews_closes_its_incident_as_resolved()
    {
        _inspector.ExpiresAt = Now.AddDays(10);
        await _watcher.CheckAllAsync(default);
        LoadIncident()!.ClosedAt.Should().BeNull();

        // The next day's check finds a freshly-renewed certificate, well outside the window.
        _inspector.ExpiresAt = Now.AddDays(90);
        await _watcher.CheckAllAsync(default);

        var incident = LoadIncident();
        incident!.ClosedAt.Should().NotBeNull();
        incident.ClosedReason.Should().Be(IncidentClosedReason.Resolved);
    }

    [Fact]
    public async Task A_healthy_certificate_never_opens_an_incident_at_all()
    {
        _inspector.ExpiresAt = Now.AddDays(60);

        await _watcher.CheckAllAsync(default);

        LoadIncident().Should().BeNull();
    }

    public void Dispose() => _provider.Dispose();
}
