using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Monitoring;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 2.1 (2026-09 market-gaps round two): <see cref="UptimeChecker"/> is the outside-in half of
/// monitoring — the only HTTP probe of a customer's app that existed before this ran once, at the end
/// of a deploy. These tests exercise the same "did this pass open/close the right incident" shape
/// <c>CertificateWatcherIncidentTests</c> already proves for the certificate watcher, plus the honesty
/// rule 2.1 was written around: a probe that could not run is its own third state, never a pass and
/// never a failure — see <see cref="UptimeCheckOutcome"/>'s own doc.
/// </summary>
public class UptimeCheckerIncidentTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Workspace = Guid.CreateVersion7();

    private readonly ServiceProvider _provider;
    private readonly UptimeChecker _checker;
    private readonly FakeUptimeProbe _probe = new();
    private readonly RecordingNotificationService _notifications = new();
    private readonly HarboraDbContext _db;
    private readonly Guid _appId;
    private readonly Guid _checkId;

    public UptimeCheckerIncidentTests()
    {
        // Computed once, outside the AddDbContext configure lambda — see CertificateWatcherIncidentTests'
        // own comment: a fresh Guid inside that lambda hands each new scope its own empty database.
        var dbName = "uptime-checker-" + Guid.NewGuid();
        var services = BuildServices(dbName, _probe, _notifications);

        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<HarboraDbContext>();

        var app = new Harbora.Domain.Apps.App
        {
            WorkspaceId = Workspace, Name = "shop", Slug = "shop", ServerId = Guid.CreateVersion7()
        };
        _db.Apps.Add(app);
        _db.Domains.Add(new DomainName { Host = "shop.example.com", AppId = app.Id, SslEnabled = true, IsPrimary = true });
        var check = new UptimeCheck { WorkspaceId = Workspace, AppId = app.Id, IntervalSeconds = 60 };
        _db.UptimeChecks.Add(check);
        _db.SaveChanges();

        _appId = app.Id;
        _checkId = check.Id;

        _checker = new UptimeChecker(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<UptimeChecker>.Instance);
    }

    private static ServiceCollection BuildServices(
        string dbName, IUptimeProbe probe, INotificationService notifications, ISystemClock? clock = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IncidentService>();
        services.AddScoped<AlertDedup>();
        services.AddSingleton(probe);
        services.AddSingleton(notifications);
        services.AddSingleton(clock ?? new FixedClock(Now));
        services.AddSingleton(Options.Create(new MonitoringOptions()));
        return services;
    }

    /// <summary>Every assertion reads through a fresh, untracked query — <see cref="UptimeChecker.CheckDueAsync"/>
    /// does its work inside its own child scope over its own <see cref="HarboraDbContext"/>, the same
    /// "same store, different context" reasoning <c>CertificateWatcherIncidentTests</c> gives for why a
    /// tracking read on <see cref="_db"/> after the fact would return stale identity-map state.</summary>
    private AlertIncident? LoadIncident(Guid workspaceId) =>
        _db.AlertIncidents.AsNoTracking().SingleOrDefault(i => i.WorkspaceId == workspaceId);

    private UptimeCheck LoadCheck() =>
        _db.UptimeChecks.AsNoTracking().Single(c => c.Id == _checkId);

    private List<UptimeCheckResult> LoadResults() =>
        _db.UptimeCheckResults.AsNoTracking().Where(r => r.AppId == _appId).ToList();

    /// <summary>
    /// Forces the check due again for a second <see cref="UptimeChecker.CheckDueAsync"/> pass, through a
    /// brand-new scope/context rather than <see cref="_db"/>'s own tracked instance. <see cref="_db"/>
    /// still holds the entity exactly as it was at construction time (its identity map was never
    /// refreshed by the checker's own, separate context), so writing through it would push that stale
    /// snapshot — including a stale <c>LastOutcome</c> — back over whatever the first pass just recorded.
    /// A fresh context is the same "a second panel process" shape
    /// <c>CertificateWatcherIncidentTests</c>' own class doc describes needing for exactly this reason.
    /// </summary>
    private async Task MakeCheckDueAgainAsync()
    {
        using var scope = _provider.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var check = await scopedDb.UptimeChecks.SingleAsync(c => c.Id == _checkId);
        check.NextCheckAt = null;
        await scopedDb.SaveChangesAsync();
    }

    [Fact]
    public async Task A_failing_probe_raises_an_incident_through_the_existing_lifecycle()
    {
        _probe.Next = new UptimeProbeResult(ProbeOutcome.Down, 503, 42, "answered 503, expected 200.");

        await _checker.CheckDueAsync(default);

        var incident = LoadIncident(Workspace);
        incident.Should().NotBeNull();
        incident!.Condition.Should().Be(AlertEvent.UptimeCheckFailed);
        incident.SubjectRef.Should().Be(_appId.ToString());
        incident.Severity.Should().Be(AlertSeverity.Critical);
        incident.ClosedAt.Should().BeNull();

        _notifications.Notifications.Should().ContainSingle(n => n.Event == AlertEvent.UptimeCheckFailed);
    }

    [Fact]
    public async Task A_recovering_probe_closes_its_incident_as_resolved()
    {
        _probe.Next = new UptimeProbeResult(ProbeOutcome.Down, 503, 42, "answered 503, expected 200.");
        await _checker.CheckDueAsync(default);
        LoadIncident(Workspace)!.ClosedAt.Should().BeNull();

        // Force the check due again — it just ran, so its own NextCheckAt is in the future.
        await MakeCheckDueAgainAsync();

        _probe.Next = new UptimeProbeResult(ProbeOutcome.Up, 200, 12, "answered 200.");
        await _checker.CheckDueAsync(default);

        var incident = LoadIncident(Workspace);
        incident!.ClosedAt.Should().NotBeNull();
        incident.ClosedReason.Should().Be(IncidentClosedReason.Resolved);
    }

    [Fact]
    public async Task A_healthy_probe_never_opens_an_incident_at_all()
    {
        _probe.Next = new UptimeProbeResult(ProbeOutcome.Up, 200, 12, "answered 200.");

        await _checker.CheckDueAsync(default);

        LoadIncident(Workspace).Should().BeNull();
        _notifications.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task A_probe_that_could_not_run_is_stored_and_rendered_as_its_own_state()
    {
        _probe.Next = new UptimeProbeResult(ProbeOutcome.CouldNotRun, null, null, "the check itself failed before it could ask.");

        await _checker.CheckDueAsync(default);

        var results = LoadResults();
        results.Should().ContainSingle();
        results[0].Outcome.Should().Be(UptimeCheckOutcome.CouldNotRun);

        var check = LoadCheck();
        check.LastOutcome.Should().Be(UptimeCheckOutcome.CouldNotRun);
        check.LastOutcome.Should().NotBe(UptimeCheckOutcome.Up, "a probe that never ran is not a pass");
        check.LastOutcome.Should().NotBe(UptimeCheckOutcome.Down, "a probe that never ran is not a confirmed failure either");

        // Neither a failure nor a pass: no incident, no notification.
        LoadIncident(Workspace).Should().BeNull();
        _notifications.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task A_probe_that_could_not_run_does_not_clear_a_standing_incident()
    {
        _probe.Next = new UptimeProbeResult(ProbeOutcome.Down, 503, 42, "answered 503, expected 200.");
        await _checker.CheckDueAsync(default);
        LoadIncident(Workspace)!.ClosedAt.Should().BeNull();

        await MakeCheckDueAgainAsync();

        // A transient checker-side failure on the very next tick must not read as "it recovered".
        _probe.Next = new UptimeProbeResult(ProbeOutcome.CouldNotRun, null, null, "the check itself failed.");
        await _checker.CheckDueAsync(default);

        LoadIncident(Workspace)!.ClosedAt.Should().BeNull("a probe that could not run is not evidence of recovery");
    }

    [Fact]
    public async Task An_app_with_no_public_domain_could_not_run_rather_than_failing()
    {
        _db.Domains.RemoveRange(_db.Domains.Where(d => d.AppId == _appId));
        await _db.SaveChangesAsync();

        await _checker.CheckDueAsync(default);

        var results = LoadResults();
        results.Should().ContainSingle();
        results[0].Outcome.Should().Be(UptimeCheckOutcome.CouldNotRun);
        results[0].Detail.Should().Contain("no public domain");

        LoadIncident(Workspace).Should().BeNull();
    }

    [Fact]
    public async Task A_check_not_yet_due_is_left_alone()
    {
        _db.UptimeChecks.Single(c => c.Id == _checkId).NextCheckAt = Now.AddMinutes(5);
        await _db.SaveChangesAsync();
        _probe.Next = new UptimeProbeResult(ProbeOutcome.Down, 500, 5, "answered 500.");

        await _checker.CheckDueAsync(default);

        LoadResults().Should().BeEmpty();
        LoadCheck().LastCheckedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_failing_probe_in_one_workspace_stays_scoped_to_that_workspace()
    {
        var otherWorkspace = Guid.CreateVersion7();
        var otherApp = new Harbora.Domain.Apps.App
        {
            WorkspaceId = otherWorkspace, Name = "other", Slug = "other", ServerId = Guid.CreateVersion7()
        };
        _db.Apps.Add(otherApp);
        _db.Domains.Add(new DomainName { Host = "other.example.com", AppId = otherApp.Id, SslEnabled = true, IsPrimary = true });
        _db.UptimeChecks.Add(new UptimeCheck { WorkspaceId = otherWorkspace, AppId = otherApp.Id, IntervalSeconds = 60 });
        await _db.SaveChangesAsync();

        // Both due checks fail on the one pass a single probe fake can only answer once for — up front,
        // not per-call — is enough here: the point under test is which workspace each incident lands in,
        // not distinguishing which app failed which way.
        _probe.Next = new UptimeProbeResult(ProbeOutcome.Down, 503, 10, "answered 503, expected 200.");

        await _checker.CheckDueAsync(default);

        // Right tenant sees its own incident.
        var mine = LoadIncident(Workspace);
        mine.Should().NotBeNull();
        mine!.SubjectRef.Should().Be(_appId.ToString());

        // Wrong tenant does not see it, and its own incident is its own, not a mix-up of the two.
        var theirs = LoadIncident(otherWorkspace);
        theirs.Should().NotBeNull();
        theirs!.SubjectRef.Should().Be(otherApp.Id.ToString());
        theirs.WorkspaceId.Should().Be(otherWorkspace);

        _notifications.Notifications.Select(n => n.Workspace).Should()
            .BeEquivalentTo([Workspace, otherWorkspace]);
    }

    private sealed class FakeUptimeProbe : IUptimeProbe
    {
        public UptimeProbeResult Next { get; set; } = new(ProbeOutcome.Up, 200, 1, "answered 200.");

        public Task<UptimeProbeResult> ProbeAsync(
            Uri url, int expectedStatus, string? bodyContains, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(Next);
    }

    public void Dispose() => _provider.Dispose();
}
