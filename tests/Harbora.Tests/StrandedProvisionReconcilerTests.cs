using System.Security.Cryptography;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Jobs;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Jobs;
using Harbora.Infrastructure.Monitoring;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Agent;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Round nine, item two: a database is never left reading Provisioning with nothing working on it.
/// After a restart it is set going again where that is safe and marked Failed with a sentence, in
/// both languages, where it is not; and the same state reached with no restart is swept up too. The
/// context here sees no tenant at all, as a sessionless startup step must not depend on one.
/// </summary>
public sealed class StrandedProvisionReconcilerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly string _store = "stranded-" + Guid.NewGuid();
    private readonly ServiceProvider _provider;
    private readonly HarboraDbContext _db;
    private readonly FixedClock _clock = new(Now);
    private readonly NoopJobQueue _jobs = new();
    private readonly RecordingNotificationService _notifications = new();
    private readonly ListLogger<StrandedProvisionReconciler> _log = new();
    private readonly Guid _workspaceId = Guid.CreateVersion7();

    public StrandedProvisionReconcilerTests()
    {
        _provider = new ServiceCollection()
            .AddSingleton<IWorkspaceScope>(new FixedWorkspaceScope(Guid.CreateVersion7()))
            .AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(_store))
            .AddScoped(sp =>
            {
                var db = sp.GetRequiredService<HarboraDbContext>();
                return new ManagedServiceEngine(
                    db, new FakeServerEngineFactory(new FakeDockerEngine()), new PassthroughProtector(), _jobs,
                    new BillingGate(db, Options.Create(new BillingOptions { Enabled = false })),
                    Options.Create(new HarboraRuntimeOptions()), _clock, NullLogger<ManagedServiceEngine>.Instance,
                    new PassthroughRedactor(), _notifications, new IncidentService(db));
            })
            .BuildServiceProvider();

        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_store).Options);
        _db.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Acme", Slug = "acme" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _provider.Dispose();
    }

    private StrandedProvisionReconciler Reconciler() =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), _clock, _log);

    private ManagedService Service(
        string name, ServiceStatus status = ServiceStatus.Provisioning, Guid? primaryId = null, string? runningImage = null,
        TimeSpan? touchedAgo = null)
    {
        var at = Now - (touchedAgo ?? TimeSpan.FromHours(1));
        var service = new ManagedService
        {
            WorkspaceId = _workspaceId, EnvironmentId = Guid.CreateVersion7(), ServerId = Guid.CreateVersion7(),
            Name = name, Type = ManagedServiceType.PostgreSql, Version = "16", Status = status,
            ContainerName = "harbora-svc-" + name, VolumeName = "harbora-svc-" + name + "-data", InternalPort = 5432,
            Username = "harbora", EncryptedPassword = "pw", PrimaryManagedServiceId = primaryId, RunningImage = runningImage,
            CreatedAt = at, UpdatedAt = at
        };
        _db.ManagedServices.Add(service);
        _db.SaveChanges();
        return service;
    }

    private Job Job(ManagedService service, JobStatus status, string? error = null, TimeSpan? finishedAgo = null, int order = 0)
    {
        var finished = Now - (finishedAgo ?? TimeSpan.FromHours(1));
        var job = new Job
        {
            Kind = JobKind.ServiceProvision, TargetId = service.Id, WorkspaceId = _workspaceId, Status = status, Error = error,
            CreatedAt = finished - TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(order),
            FinishedAt = status is JobStatus.Pending or JobStatus.Running ? null : finished
        };
        _db.Jobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    private ManagedService Read(Guid id)
    {
        using var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_store).Options);
        return db.ManagedServices.AsNoTracking().Single(s => s.Id == id);
    }

    private List<Guid> Queued() => _jobs.Enqueued.Where(j => j.Kind == JobKind.ServiceProvision).Select(j => j.TargetId).ToList();

    // ---- after a restart: set going again --------------------------------------------------------

    [Fact]
    public async Task A_database_whose_setup_a_restart_cut_off_is_set_going_again()
    {
        var orders = Service("orders");
        Job(orders, JobStatus.Failed, JobReconciler.InterruptedByRestart);

        var settled = await Reconciler().ReconcileAsync(atStartup: true, default);

        settled.Should().Be((1, 0));
        Queued().Should().Equal(orders.Id);
        var row = Read(orders.Id);
        row.Status.Should().Be(ServiceStatus.Provisioning, "it is being set up again, which is what Provisioning means");
        row.ErrorMessage.Should().BeNull();
        _db.AlertIncidents.IgnoreQueryFilters().Should().BeEmpty("nothing failed");
    }

    [Fact]
    public async Task A_database_whose_setup_was_never_queued_is_set_going()
    {
        var orders = Service("orders");

        await Reconciler().ReconcileAsync(atStartup: true, default);

        Queued().Should().Equal(orders.Id);
    }

    [Fact]
    public async Task A_rebuild_of_a_replica_already_seeded_is_set_going_like_any_other_database()
    {
        var primary = Service("orders", ServiceStatus.Running, runningImage: "postgres:16");
        var replica = Service("orders-replica", primaryId: primary.Id, runningImage: "postgres:16");
        Job(replica, JobStatus.Failed, JobReconciler.InterruptedByRestart);

        await Reconciler().ReconcileAsync(atStartup: true, default);

        Queued().Should().Equal(replica.Id);
    }

    [Fact]
    public async Task A_queued_setup_a_graceful_shutdown_returned_to_the_queue_is_left_to_resume()
    {
        var orders = Service("orders");
        Job(orders, JobStatus.Failed, JobReconciler.InterruptedByRestart, order: 0);
        Job(orders, JobStatus.Pending, order: 1);

        var settled = await Reconciler().ReconcileAsync(atStartup: true, default);

        settled.Should().Be((0, 0));
        Queued().Should().BeEmpty("a second provision queued beside the one already owed would run it twice");
        Read(orders.Id).Status.Should().Be(ServiceStatus.Provisioning);
    }

    [Fact]
    public async Task Databases_that_are_not_being_set_up_are_not_touched()
    {
        var running = Service("running", ServiceStatus.Running, runningImage: "postgres:16");
        var failed = Service("failed", ServiceStatus.Failed);
        var stopped = Service("stopped", ServiceStatus.Stopped);

        var settled = await Reconciler().ReconcileAsync(atStartup: true, default);

        settled.Should().Be((0, 0));
        Read(running.Id).Status.Should().Be(ServiceStatus.Running);
        Read(failed.Id).Status.Should().Be(ServiceStatus.Failed);
        Read(stopped.Id).Status.Should().Be(ServiceStatus.Stopped);
    }

    // ---- after a restart: marked Failed, with a sentence ------------------------------------------

    [Fact]
    public async Task A_replica_still_copying_its_primary_is_failed_with_a_sentence_and_left_for_the_person()
    {
        var primary = Service("orders", ServiceStatus.Running, runningImage: "postgres:16");
        var replica = Service("orders-replica", primaryId: primary.Id);
        Job(replica, JobStatus.Failed, JobReconciler.InterruptedByRestart);

        var settled = await Reconciler().ReconcileAsync(atStartup: true, default);

        settled.Should().Be((0, 1));
        Queued().Should().BeEmpty("a new copy wipes this one and loads the primary; that is not a startup's call");
        var row = Read(replica.Id);
        row.Status.Should().Be(ServiceStatus.Failed);
        row.ErrorMessage.Should().Be(StrandedProvision.ReplicaInterrupted);
        StrandedProvision.InReadersLanguage(row.ErrorMessage, isFa: true).Should().Contain("رپلیکا").And.Contain("تلاش دوباره");

        // The same three surfaces any other failed provision reaches.
        _db.AlertIncidents.IgnoreQueryFilters().Should().ContainSingle(i =>
            i.Condition == AlertEvent.ServiceProvisionFailed && i.SubjectRef == replica.Id.ToString());
        _notifications.Notifications.Should().ContainSingle(n => n.Event == AlertEvent.ServiceProvisionFailed);
    }

    [Fact]
    public async Task A_setup_a_restart_has_cut_off_twice_in_a_row_is_not_started_a_third_time()
    {
        var orders = Service("orders");
        Job(orders, JobStatus.Failed, JobReconciler.InterruptedByRestart, finishedAgo: TimeSpan.FromHours(2), order: 0);
        Job(orders, JobStatus.Failed, JobReconciler.InterruptedByRestart, finishedAgo: TimeSpan.FromHours(1), order: 1);

        await Reconciler().ReconcileAsync(atStartup: true, default);

        Queued().Should().BeEmpty();
        var row = Read(orders.Id);
        row.Status.Should().Be(ServiceStatus.Failed);
        row.ErrorMessage.Should().Be(StrandedProvision.InterruptedTwice);
        StrandedProvision.InReadersLanguage(row.ErrorMessage, isFa: true).Should().Contain("دو بار");
    }

    [Fact]
    public async Task One_interruption_long_after_an_earlier_successful_setup_is_still_set_going()
    {
        var orders = Service("orders");
        Job(orders, JobStatus.Succeeded, finishedAgo: TimeSpan.FromDays(30), order: 0);
        Job(orders, JobStatus.Failed, JobReconciler.InterruptedByRestart, order: 1);

        await Reconciler().ReconcileAsync(atStartup: true, default);

        Queued().Should().Equal(orders.Id);
    }

    // ---- the same state with no restart ----------------------------------------------------------

    [Fact]
    public async Task A_setup_cancelled_before_it_ran_is_failed_with_a_sentence_once_it_has_sat_untouched()
    {
        var orders = Service("orders", touchedAgo: TimeSpan.FromMinutes(30));
        Job(orders, JobStatus.Cancelled, finishedAgo: TimeSpan.FromMinutes(11));

        var settled = await Reconciler().ReconcileAsync(atStartup: false, default);

        settled.Should().Be((0, 1));
        var row = Read(orders.Id);
        row.Status.Should().Be(ServiceStatus.Failed);
        row.ErrorMessage.Should().Be(StrandedProvision.Cancelled);
        StrandedProvision.InReadersLanguage(row.ErrorMessage, isFa: true).Should().Contain("لغو شد");
    }

    [Fact]
    public async Task A_setup_that_failed_before_the_engine_could_record_it_is_failed_with_the_job_s_own_words()
    {
        var orders = Service("orders");
        Job(orders, JobStatus.Failed, "Node n-1 ('east') was revoked and takes no commands");

        await Reconciler().ReconcileAsync(atStartup: false, default);

        var row = Read(orders.Id);
        row.Status.Should().Be(ServiceStatus.Failed);
        row.ErrorMessage.Should().Be(
            "Setting this database up stopped before it finished: Node n-1 ('east') was revoked and takes no commands. Press Retry to try again.");
        StrandedProvision.InReadersLanguage(row.ErrorMessage, isFa: true).Should()
            .Be("راه‌اندازی این دیتابیس پیش از تمام شدن متوقف شد: Node n-1 ('east') was revoked and takes no commands. برای امتحان دوباره، «تلاش دوباره» را بزن.");
        Queued().Should().BeEmpty("the same failure would come back");
    }

    [Fact]
    public async Task A_periodic_pass_leaves_a_database_touched_a_moment_ago_alone()
    {
        // The Retry button and Launch's own retry mark the row Provisioning and queue a moment later.
        var justMarked = Service("just-marked", touchedAgo: TimeSpan.FromMinutes(1));
        Job(justMarked, JobStatus.Failed, "the first try failed", finishedAgo: TimeSpan.FromHours(3));
        var justEnded = Service("just-ended");
        Job(justEnded, JobStatus.Cancelled, finishedAgo: TimeSpan.FromMinutes(2));

        var settled = await Reconciler().ReconcileAsync(atStartup: false, default);

        settled.Should().Be((0, 0));
        Read(justMarked.Id).Status.Should().Be(ServiceStatus.Provisioning);
        Read(justEnded.Id).Status.Should().Be(ServiceStatus.Provisioning);
    }

    [Fact]
    public async Task At_startup_there_is_no_grace_because_nothing_can_be_setting_anything_up()
    {
        var orders = Service("orders", touchedAgo: TimeSpan.FromSeconds(30));
        Job(orders, JobStatus.Failed, JobReconciler.InterruptedByRestart, finishedAgo: TimeSpan.FromSeconds(10));

        await Reconciler().ReconcileAsync(atStartup: true, default);

        Queued().Should().Equal(orders.Id);
    }

    // ---- the reason, in the reader's language ----------------------------------------------------

    [Fact]
    public void A_reason_this_did_not_write_is_shown_exactly_as_it_was_stored()
    {
        StrandedProvision.InReadersLanguage("pull access denied", isFa: true).Should().Be("pull access denied");
        StrandedProvision.InReadersLanguage(StrandedProvision.Cancelled, isFa: false).Should().Be(StrandedProvision.Cancelled);
        StrandedProvision.InReadersLanguage(null, isFa: true).Should().BeNull();
    }

    // ---- the engine half: a server it cannot reach no longer strands the row ----------------------

    [Fact]
    public async Task A_provision_onto_a_server_that_cannot_be_reached_fails_the_database_instead_of_leaving_it_provisioning()
    {
        using var h = new ServiceFailureHarness();
        var svc = await h.SeedServiceAsync();
        var engines = new FakeServerEngineFactory(h.Docker).Unreachable(svc.ServerId, "Node n-1 ('east') was revoked and takes no commands.");
        var engine = new ManagedServiceEngine(
            h.Db, engines, h.Protector, new NoopJobQueue(),
            new BillingGate(h.Db, Options.Create(new BillingOptions { Enabled = false })),
            Options.Create(new HarboraRuntimeOptions()), h.Clock, NullLogger<ManagedServiceEngine>.Instance,
            new PassthroughRedactor(), h.Notifications, new IncidentService(h.Db));

        await engine.ProvisionAsync(svc.Id, default);

        var row = await h.ReadServiceAsync(svc.Id);
        row.Status.Should().Be(ServiceStatus.Failed);
        row.ErrorMessage.Should().Contain("revoked");
    }

    // ---- as a hosted service ---------------------------------------------------------------------

    [Fact]
    public async Task A_startup_that_cannot_reach_the_database_is_logged_and_does_not_stop_the_panel()
    {
        var broken = new ServiceCollection().BuildServiceProvider();
        var reconciler = new StrandedProvisionReconciler(broken.GetRequiredService<IServiceScopeFactory>(), _clock, _log);

        await reconciler.Invoking(r => r.StartAsync(default)).Should().NotThrowAsync();
        await reconciler.StopAsync(default);
        _log.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task Its_startup_pass_is_over_by_the_time_StartAsync_returns()
    {
        var orders = Service("orders");
        Job(orders, JobStatus.Failed, JobReconciler.InterruptedByRestart);
        var reconciler = Reconciler();

        await reconciler.StartAsync(default);
        var queuedWhenStarted = Queued();
        await reconciler.StopAsync(default);

        queuedWhenStarted.Should().Equal([orders.Id], "the job gate opens on the next hosted service's StartAsync");
    }

    [Fact]
    public void It_is_registered_above_the_job_gate_and_the_container_can_build_it()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Harbora:MasterKey"] = Convert.ToBase64String(SHA256.HashData("tests"u8.ToArray()))
        }).Build();
        var services = new ServiceCollection();
        Harbora.Infrastructure.DependencyInjection.AddHarboraInfrastructure(services, config);

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();
        var mine = hosted.IndexOf(typeof(StrandedProvisionReconciler));
        mine.Should().BeGreaterThan(hosted.IndexOf(typeof(JobReconciler)), "the jobs a restart cut off must already read Failed");
        mine.Should().BeLessThan(hosted.IndexOf(typeof(JobStartupGateOpener)), "the worker must not claim before this has run");

        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        ActivatorUtilities.CreateInstance<StrandedProvisionReconciler>(provider).Should().NotBeNull();
    }
}
