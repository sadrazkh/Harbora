using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What a dispatch target must still manage to write once its deadline has fired.
///
/// <para>
/// Every job now runs under a per-kind deadline and is dispatched with the token that deadline
/// cancels. Each of the four built-in targets catches <c>Exception</c> at its top level and records
/// the failure on its own domain row — and each of them used to make that write with the very token
/// that had just been cancelled, so <c>SaveChangesAsync</c> threw before the row reached the
/// database and the in-memory transition was thrown away with the scope.
/// </para>
/// <para>
/// The <c>Job</c> row settled <c>Failed</c> with a truthful message either way, because the worker
/// settles on <see cref="CancellationToken.None"/>. The domain row did not, and that is the half a
/// person looks at: a deployment stuck on "Building" that no later deploy of the app can get past, a
/// backup that never finished and never failed, a service reading "Provisioning" for ever, a cron
/// run with no finish time — which is also the row that blocks the next run of that job.
/// </para>
/// <para>
/// The deadline is fired by the container fake at the moment the work is entered
/// (<see cref="FakeDockerEngine.DeadlineFiresWhenTheWorkBegins"/>) rather than by a timer, so these
/// tests assert about a token that provably went while the work was underway.
/// </para>
/// </summary>
public class DeadlineSettlementTests
{
    // --- deployments ------------------------------------------------------------------------------

    /// <summary>
    /// The worst of the four, because the consequence outlives the deployment.
    /// <c>DeploymentEngine.QueueDeploymentAsync</c> coalesces onto any in-flight deployment of the
    /// app, so a deployment left mid-flight hands its id back to every later deploy of that app and
    /// none of them run anything — until someone restarts the panel or cancels by hand.
    /// </summary>
    [Fact]
    public async Task A_deployment_given_up_on_at_its_deadline_records_that_it_failed()
    {
        using var h = new PipelineHarness();
        using var deadline = new CancellationTokenSource();
        h.Docker.PullNeverFinishes = true;
        h.Docker.DeadlineFiresWhenTheWorkBegins = deadline;

        var deployment = h.QueueDeployment();

        await RunUntilTheDeadlineEndsIt(() =>
            h.BuildPipeline().ExecuteAsync(deployment.Id, deadline.Token));

        var stored = await h.Db.Deployments.AsNoTracking().FirstAsync(d => d.Id == deployment.Id);
        stored.Status.Should().Be(DeploymentStatus.Failed,
            "the queue has already given up on this deployment; a row that still says Building is " +
            "the panel telling an operator work is happening that nothing is doing");
        stored.FinishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task The_next_deploy_of_that_app_is_not_coalesced_onto_the_abandoned_one()
    {
        using var h = new PipelineHarness();
        using var deadline = new CancellationTokenSource();
        h.Docker.PullNeverFinishes = true;
        h.Docker.DeadlineFiresWhenTheWorkBegins = deadline;

        var abandoned = h.QueueDeployment();
        await RunUntilTheDeadlineEndsIt(() =>
            h.BuildPipeline().ExecuteAsync(abandoned.Id, deadline.Token));

        var engine = new DeploymentEngine(h.Db, new NoopJobQueue(), h.Clock);
        var next = await engine.QueueDeploymentAsync(
            new DeploymentRequest(h.App.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        next.Should().NotBe(abandoned.Id,
            "coalescing onto a deployment nothing is running turns every later deploy of this app " +
            "into a no-op that reports the abandoned id as if it were live");
    }

    // --- backups ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_backup_given_up_on_at_its_deadline_records_that_it_failed()
    {
        using var h = new BackupHarness();
        using var deadline = new CancellationTokenSource();
        h.Docker.OneOffNeverFinishes = true;
        h.Docker.DeadlineFiresWhenTheWorkBegins = deadline;

        var backup = await h.SeedPendingBackupAsync(BackupType.Volume, "uploads");

        await RunUntilTheDeadlineEndsIt(() => h.Engine().RunAsync(backup.Id, deadline.Token));

        var stored = await h.Db.Backups.AsNoTracking().FirstAsync(b => b.Id == backup.Id);
        stored.Status.Should().Be(BackupStatus.Failed,
            "a backup left reading Running is one the Backup Center shows as still protecting the " +
            "target it stopped protecting");
        stored.FinishedAt.Should().NotBeNull();
    }

    // --- managed services -------------------------------------------------------------------------

    [Fact]
    public async Task A_service_given_up_on_at_its_deadline_records_that_it_failed()
    {
        using var h = new ProvisionHarness();
        using var deadline = new CancellationTokenSource();
        h.Docker.PullNeverFinishes = true;
        h.Docker.DeadlineFiresWhenTheWorkBegins = deadline;

        var service = await h.SeedServiceAsync();

        await RunUntilTheDeadlineEndsIt(() => h.Engine().ProvisionAsync(service.Id, deadline.Token));

        var stored = await h.Read().ManagedServices.AsNoTracking().FirstAsync(s => s.Id == service.Id);
        stored.Status.Should().Be(ServiceStatus.Failed,
            "a database that never came up must say so; Provisioning for ever is a spinner with " +
            "nothing behind it");
    }

    // --- cron runs --------------------------------------------------------------------------------

    /// <summary>
    /// The cron run carries the same second consequence a deployment does: <c>CronJobRunner</c>
    /// refuses to start a job while a run of it has no <c>FinishedAt</c>, so a run the deadline
    /// abandoned without finishing the row ends that job's schedule until the next restart.
    /// </summary>
    [Fact]
    public async Task A_cron_run_given_up_on_at_its_deadline_records_that_it_finished()
    {
        using var h = new CronHarness();
        using var deadline = new CancellationTokenSource();
        h.Docker.OneOffNeverFinishes = true;
        h.Docker.DeadlineFiresWhenTheWorkBegins = deadline;

        var job = await h.SeedJobAsync();

        await RunUntilTheDeadlineEndsIt(() => h.Runner().RunAsync(job.Id, deadline.Token));

        var run = h.Read().CronRuns.AsNoTracking().Should().ContainSingle().Subject;
        run.FinishedAt.Should().NotBeNull(
            "a run with no finish time reads as still going, and the next tick of this job is " +
            "skipped because of it");
        run.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_job_can_run_again_after_a_run_the_deadline_ended()
    {
        using var h = new CronHarness();
        using var deadline = new CancellationTokenSource();
        h.Docker.OneOffNeverFinishes = true;
        h.Docker.DeadlineFiresWhenTheWorkBegins = deadline;

        var job = await h.SeedJobAsync();
        await RunUntilTheDeadlineEndsIt(() => h.Runner().RunAsync(job.Id, deadline.Token));

        h.Docker.OneOffNeverFinishes = false;
        h.Docker.DeadlineFiresWhenTheWorkBegins = null;
        await h.Runner().RunAsync(job.Id, default);

        h.Read().CronRuns.Count().Should().Be(2,
            "one job that outran its deadline must cost that run, not the schedule");
    }

    // --- shared -----------------------------------------------------------------------------------

    /// <summary>
    /// Runs a dispatch target whose token is cancelled underneath it.
    ///
    /// <para>
    /// The cancellation is allowed to leave the call — that is exactly what the worker's
    /// <c>catch (Exception) when (Stopped(...))</c> is for, and it settles the <c>Job</c> row from
    /// there. What is under test is what the target managed to write on its own row before it left.
    /// </para>
    /// </summary>
    private static async Task RunUntilTheDeadlineEndsIt(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { /* the deadline; the worker handles this */ }
    }
}

/// <summary>The real <see cref="ManagedServiceEngine"/> over a fake daemon and an in-memory database.</summary>
internal sealed class ProvisionHarness : IDisposable
{
    private readonly string _database = "provision-" + Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();

    public FakeDockerEngine Docker { get; } = new();
    public FixedClock Clock { get; } = new();

    public HarboraDbContext Read() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase(_database).Options);

    private readonly HarboraDbContext _db;

    public ProvisionHarness()
    {
        _db = Read();
        _db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
        { Id = _workspaceId, Name = "Acme", Slug = "acme" });
        _db.SaveChanges();
    }

    public async Task<ManagedService> SeedServiceAsync()
    {
        var service = new ManagedService
        {
            Id = Guid.NewGuid(), WorkspaceId = _workspaceId, ServerId = Guid.NewGuid(),
            Name = "orders", Type = ManagedServiceType.PostgreSql, Version = "16-alpine",
            ContainerName = "harbora-orders", InternalPort = 5432,
            Username = "harbora", EncryptedPassword = "s3cret", DatabaseName = "orders",
            VolumeName = "orders-data", Status = ServiceStatus.Provisioning
        };
        _db.ManagedServices.Add(service);
        await _db.SaveChangesAsync();
        return service;
    }

    public Harbora.Infrastructure.Services.ManagedServiceEngine Engine() => new(
        _db,
        new SingleEngineFactory(Docker),
        new PassthroughProtector(),
        new NoopJobQueue(),
        Options.Create(new HarboraRuntimeOptions()),
        Clock,
        NullLogger<Harbora.Infrastructure.Services.ManagedServiceEngine>.Instance);

    public void Dispose() => _db.Dispose();
}

/// <summary>The real <see cref="CronJobRunner"/> over a fake daemon and an in-memory database.</summary>
internal sealed class CronHarness : IDisposable
{
    private readonly string _database = "cron-deadline-" + Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();

    public FakeDockerEngine Docker { get; } = new();
    public FixedClock Clock { get; } = new();

    public HarboraDbContext Read() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase(_database).Options);

    private readonly HarboraDbContext _db;

    public CronHarness()
    {
        _db = Read();
        _db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
        { Id = _workspaceId, Name = "Acme", Slug = "acme" });
        _db.SaveChanges();
    }

    public async Task<App> SeedJobAsync()
    {
        var job = new App
        {
            Id = Guid.NewGuid(), WorkspaceId = _workspaceId, ServerId = Guid.NewGuid(),
            Name = "nightly", Slug = "nightly", Kind = ServiceKind.Cron,
            CronExpression = "0 3 * * *", Command = "backup.sh",
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "alpine:3.20"
        };
        _db.Apps.Add(job);
        await _db.SaveChangesAsync();
        return job;
    }

    public CronJobRunner Runner() => new(
        _db,
        new SingleEngineFactory(Docker),
        new PassthroughProtector(),
        Options.Create(new HarboraRuntimeOptions()),
        Clock,
        NullLogger<CronJobRunner>.Instance);

    public void Dispose() => _db.Dispose();
}

/// <summary>Every server resolves to the one fake daemon these harnesses hold.</summary>
internal sealed class SingleEngineFactory(FakeDockerEngine engine) : IServerEngineFactory
{
    public IDockerEngine Local => engine;

    public Task<IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct) =>
        Task.FromResult<IDockerEngine>(engine);
}
