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

    /// <summary>
    /// The deadline fires while the container is up and the health check is running — the only
    /// window in which giving up leaves something behind. The cleanup in the pipeline's catch is
    /// what removes it, and it used to be handed the very token that had just been cancelled.
    /// </summary>
    [Fact]
    public async Task A_deployment_the_deadline_kills_does_not_leave_its_container_running()
    {
        using var h = new PipelineHarness();
        using var deadline = new CancellationTokenSource();
        h.Docker.DeadlineFiresOnceTheContainerIsUp = deadline;

        var deployment = h.QueueDeployment(number: 2);

        await RunUntilTheDeadlineEndsIt(() =>
            h.BuildPipeline().ExecuteAsync(deployment.Id, deadline.Token));

        h.Docker.OperationsOn(h.ContainerFor(2)).Should().Contain("RemoveContainerAsync",
            "the deployment is recorded Failed, so the container it started is a container nothing " +
            "owns — still holding the app's memory, its volumes and its port on the node");
        h.Docker.LiveContainerNames.Should().NotContain(h.ContainerFor(2));
    }

    /// <summary>
    /// The half of the same leak that outlives the node's memory. The host-port range is per-node
    /// and shared by every app on it, so one tenant's slow builds draining it stops every other
    /// tenant deploying — one tenant's work freezing another's, which is what this phase exists to
    /// stop.
    /// </summary>
    [Fact]
    public async Task A_deployment_the_deadline_kills_gives_its_host_port_back()
    {
        using var h = new PipelineHarness(localServer: false);
        using var deadline = new CancellationTokenSource();
        h.Docker.DeadlineFiresOnceTheContainerIsUp = deadline;

        var deployment = h.QueueDeployment(number: 2);

        await RunUntilTheDeadlineEndsIt(() =>
            h.BuildPipeline().ExecuteAsync(deployment.Id, deadline.Token));

        h.Db.HostPortAllocations.Should().BeEmpty(
            "a reservation held by a deployment that is over is a port this node never gets back " +
            "until it is restarted, and the range is shared with every other app on it");
    }

    // --- a deadline that fires after the deployment already succeeded ------------------------------

    /// <summary>
    /// The window the other three tests do not reach: everything after the success transition.
    ///
    /// <para>
    /// By the time image retention runs, <c>SetStatus(Succeeded)</c> has already committed — the
    /// database records that this deployment worked, and the container it started is serving. But
    /// retention logs through the pipeline's ordinary <c>Log</c>, which publishes on the work's own
    /// token, so a deadline firing here throws out of a deployment that is over and successful. The
    /// pipeline's failure path then stamped <c>ErrorMessage</c>, published <c>Failed</c> and raised
    /// <c>DeployFailed</c> over a row that says <c>Succeeded</c>.
    /// </para>
    /// <para>
    /// That is the platform lying about a deployment — told to the user as a failure, and
    /// contradicted by its own stored row a refresh later. Housekeeping running out of time is a
    /// fact about housekeeping.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_deadline_during_image_retention_leaves_a_succeeded_deployment_succeeded()
    {
        using var h = new PipelineHarness();
        using var deadline = new CancellationTokenSource();
        h.WithPreviousDeployment(number: 1);
        h.Docker.DeadlineFiresWhenImagesAreListed = deadline;

        var deployment = h.QueueDeployment(number: 2);

        await RunUntilTheDeadlineEndsIt(() =>
            h.BuildPipeline().ExecuteAsync(deployment.Id, deadline.Token));

        var stored = await h.Db.Deployments.AsNoTracking().FirstAsync(d => d.Id == deployment.Id);
        stored.Status.Should().Be(DeploymentStatus.Succeeded,
            "the release went live and the row already said so before retention was reached");
        stored.ErrorMessage.Should().BeNull(
            "a deployment page showing a reason it failed, above a status that says it succeeded, " +
            "leaves the user no way to know which half to believe");
    }

    [Fact]
    public async Task Nobody_is_told_a_succeeded_deployment_failed_because_retention_ran_out_of_time()
    {
        using var h = new PipelineHarness();
        using var deadline = new CancellationTokenSource();
        h.WithPreviousDeployment(number: 1);
        h.Docker.DeadlineFiresWhenImagesAreListed = deadline;

        var deployment = h.QueueDeployment(number: 2);

        await RunUntilTheDeadlineEndsIt(() =>
            h.BuildPipeline().ExecuteAsync(deployment.Id, deadline.Token));

        h.Stream.Statuses.Should().NotContain(DeploymentStatus.Failed,
            "the page is live; a Failed after the Succeeded it already showed is the platform " +
            "contradicting itself in front of the person watching");
        h.Notifications.Notifications.Should().NotContain(n => n.Event == AlertEvent.DeployFailed,
            "an alert wakes somebody who is not looking at the panel, and this one would send them " +
            "to investigate a deployment that worked");
    }

    /// <summary>
    /// The consequence that outlives the lie. The failure path removes the container named for this
    /// deployment number — correct while the deploy is in flight, because that container is the one
    /// nothing owns yet. After the cutover it is the release that is serving traffic, so reporting
    /// the deployment failed would also make it fail.
    /// </summary>
    [Fact]
    public async Task Housekeeping_running_out_of_time_does_not_tear_down_the_release_it_follows()
    {
        using var h = new PipelineHarness();
        using var deadline = new CancellationTokenSource();
        h.WithPreviousDeployment(number: 1);
        h.Docker.DeadlineFiresWhenImagesAreListed = deadline;

        var deployment = h.QueueDeployment(number: 2);

        await RunUntilTheDeadlineEndsIt(() =>
            h.BuildPipeline().ExecuteAsync(deployment.Id, deadline.Token));

        h.Docker.LiveContainerNames.Should().Contain(h.ContainerFor(2),
            "this container IS the deployment that succeeded — removing it takes the app down " +
            "minutes after telling the user the release is live");

        var app = await h.Db.Apps.AsNoTracking().FirstAsync(a => a.Id == h.App.Id);
        app.ActiveDeploymentId.Should().Be(deployment.Id);
        app.Status.Should().Be(AppStatus.Running);
    }

    /// <summary>
    /// The other half: not reporting retention's failure as the deployment's must not mean not
    /// reporting it. Superseded images are still on the node's disk, which is a thing an operator
    /// acts on — so it belongs on the deployment's own page, where somebody reading this deploy
    /// will find it, and not only in a host log nobody opens.
    /// </summary>
    [Fact]
    public async Task Retention_that_could_not_finish_still_says_so_on_the_deployment()
    {
        using var h = new PipelineHarness();
        using var deadline = new CancellationTokenSource();
        h.WithPreviousDeployment(number: 1);
        h.Docker.DeadlineFiresWhenImagesAreListed = deadline;

        var deployment = h.QueueDeployment(number: 2);

        await RunUntilTheDeadlineEndsIt(() =>
            h.BuildPipeline().ExecuteAsync(deployment.Id, deadline.Token));

        // Persisted, not merely streamed: the deploy is over, so the only reader left is somebody
        // opening the page afterwards.
        var stored = await h.Db.DeploymentLogs.AsNoTracking()
            .Where(l => l.DeploymentId == deployment.Id)
            .OrderBy(l => l.Sequence).ToListAsync();

        stored.Should().Contain(l => l.Message.Contains("✅ Deployment"),
            "the line that says it worked has to survive the throw as well — nothing saved after it "
            + "on this path but the block that handles this");
        stored.Should().Contain(l => l.Message.Contains("housekeeping"),
            "silence would leave images accumulating on the node with nothing anywhere saying why");
        stored.Should().NotContain(l => l.Message.Contains("❌ Deployment failed"),
            "the deploy log is the account of the deployment, and it did not fail");
    }

    // --- what a person watching is told ------------------------------------------------------------

    /// <summary>
    /// The durable row is truthful after the last wave, so the deployment page is right on reload.
    /// The deploy log is not reloaded — it is streamed, and it simply stopped mid-build, which reads
    /// as "still going". A job the clock killed recording the truth in the database and telling
    /// nobody is the exact failure the deadline work exists to make visible.
    /// </summary>
    [Fact]
    public async Task The_deploy_log_of_a_deployment_the_deadline_killed_ends_by_saying_it_failed()
    {
        using var h = new PipelineHarness();
        using var deadline = new CancellationTokenSource();
        h.Docker.PullNeverFinishes = true;
        h.Docker.DeadlineFiresWhenTheWorkBegins = deadline;

        var deployment = h.QueueDeployment();

        await RunUntilTheDeadlineEndsIt(() =>
            h.BuildPipeline().ExecuteAsync(deployment.Id, deadline.Token));

        // Persisted, not merely streamed: nobody is watching a build at the moment its deadline
        // fires, so the line that matters is the one still there when they open the page.
        var stored = await h.Db.DeploymentLogs.AsNoTracking()
            .Where(l => l.DeploymentId == deployment.Id)
            .OrderBy(l => l.Sequence).ToListAsync();

        stored.Should().NotBeEmpty();
        stored[^1].Message.Should().Contain("❌ Deployment failed",
            "a log that stops mid-build reads as a build still running");

        h.Stream.Lines.Should().Contain(l => l.Contains("❌ Deployment failed"),
            "and anyone who did have the page open is told without refreshing it");
        h.Stream.Statuses.Should().EndWith([DeploymentStatus.Failed],
            "the status the page is bound to has to move too, or the spinner never stops");
    }

    [Fact]
    public async Task A_deployment_the_deadline_killed_still_raises_the_deploy_failed_alert()
    {
        // The one surface that reaches somebody who is not looking at the panel at all.
        using var h = new PipelineHarness();
        using var deadline = new CancellationTokenSource();
        h.Docker.PullNeverFinishes = true;
        h.Docker.DeadlineFiresWhenTheWorkBegins = deadline;

        var deployment = h.QueueDeployment();

        await RunUntilTheDeadlineEndsIt(() =>
            h.BuildPipeline().ExecuteAsync(deployment.Id, deadline.Token));

        var alert = h.Notifications.Notifications.Should().ContainSingle().Subject;
        alert.Event.Should().Be(AlertEvent.DeployFailed);
        alert.Severity.Should().Be(AlertSeverity.Critical);
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
