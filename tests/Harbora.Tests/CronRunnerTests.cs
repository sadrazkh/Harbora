using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// When a scheduled job fires, and what it leaves behind.
///
/// The failures worth designing against here are all quiet ones: a job that fires the instant it is
/// created rather than at 03:00; a job that fires again while the previous run is still going; a
/// panel that was down overnight waking up and running yesterday's job twenty-four times. None of
/// them raise an error — they are only visible in the history, which is exactly why the history is
/// the feature.
/// </summary>
public class CronRunnerTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly FakeDockerEngine _docker = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 7, 31, 2, 40, 0, TimeSpan.Zero));
    private readonly Guid _workspaceId = Guid.NewGuid();

    public CronRunnerTests()
    {
        // Named once, not per context: the factory runs for every instance, so building the name
        // inside the lambda would give each scope its own empty database.
        var database = "cron-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddSingleton<ISystemClock>(_clock);
        services.AddSingleton<ISecretProtector>(new PassthroughProtector());
        services.AddSingleton<IServerEngineFactory>(new SingleEngine(_docker));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new HarboraRuntimeOptions()));
        // The real billing gate with Billing:Enabled false — the shipped default — rather than a
        // fake that always allows. Every test in this file then runs the line production runs, and
        // the one that watches a job refused for an empty balance lives beside the gate itself.
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            new Harbora.Infrastructure.Billing.BillingOptions()));
        services.AddScoped<Harbora.Application.Abstractions.IBillingGate,
            Harbora.Infrastructure.Billing.BillingGate>();
        // The real runner over the real context: the schedule and the button share this path, so a
        // fake here would test the fake rather than the guarantee.
        services.AddScoped<CronJobRunner>();
        services.AddLogging();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    private sealed class SingleEngine(FakeDockerEngine engine) : IServerEngineFactory
    {
        public IDockerEngine Local => engine;
        public Task<IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct) =>
            Task.FromResult<IDockerEngine>(engine);
    }

    private CronRunner Runner() =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CronRunner>.Instance);

    /// <summary>The run itself, as the "run now" button and the job queue reach it.</summary>
    private CronJobRunner JobRunner(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<CronJobRunner>();

    /// <summary>
    /// EnvironmentId is required now (P2, 2026-08-17 app-environment-management design), so every app
    /// this builds is placed in a project and environment of its own — "cron-default"/"production" —
    /// rather than the workspace network it fell back to before every app had one. A test that needs
    /// a DIFFERENT environment (a non-default one, or none at all is no longer legal) still seeds and
    /// re-points the app itself, exactly as it did before.
    /// </summary>
    private App Given(string cron, DateTimeOffset? nextRunAt = null, AppStatus status = AppStatus.Running,
                      string? command = "backup.sh", string image = "alpine:3.20")
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.NewGuid(), WorkspaceId = _workspaceId, Name = "Cron Default", Slug = "cron-default" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.NewGuid(), WorkspaceId = _workspaceId, ProjectId = project.Id,
            Name = "Production", Slug = "production", IsDefault = true
        };
        db.Projects.Add(project);
        db.Environments.Add(environment);

        var app = new App
        {
            WorkspaceId = _workspaceId,
            EnvironmentId = environment.Id,
            Name = "nightly", Slug = "nightly",
            Kind = ServiceKind.Cron,
            CronExpression = cron,
            NextRunAt = nextRunAt,
            Status = status,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = image,
            Command = command
        };
        db.Apps.Add(app);
        db.SaveChanges();
        return app;
    }

    private (App App, List<CronRun> Runs) Reload(Guid appId)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        return (db.Apps.AsNoTracking().Single(a => a.Id == appId),
                db.CronRuns.AsNoTracking().Where(r => r.AppId == appId).OrderBy(r => r.StartedAt).ToList());
    }

    [Fact]
    public async Task A_job_seen_for_the_first_time_is_scheduled_rather_than_fired()
    {
        // "Never run" is not "overdue". Firing on sight would mean every nightly backup also runs
        // the moment someone saves the form — at whatever time of day that happens to be.
        var app = Given("0 3 * * *", nextRunAt: null);

        await Runner().TickAsync(default);

        var (reloaded, runs) = Reload(app.Id);
        runs.Should().BeEmpty();
        reloaded.NextRunAt.Should().Be(new DateTimeOffset(2026, 7, 31, 3, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_job_that_is_due_runs_and_records_what_it_did()
    {
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1));
        _docker.OneOffExitCode = 0;

        await Runner().TickAsync(default);

        var (_, runs) = Reload(app.Id);
        var run = runs.Should().ContainSingle().Subject;
        run.ExitCode.Should().Be(0);
        run.Error.Should().BeNull();
        run.FinishedAt.Should().NotBeNull("a run with no end is indistinguishable from one still going");
        _docker.OneOffCommands.Should().ContainSingle().Which.Should().Contain("backup.sh");
    }

    [Fact]
    public async Task A_job_that_is_not_due_yet_is_left_alone()
    {
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(20));

        await Runner().TickAsync(default);

        Reload(app.Id).Runs.Should().BeEmpty();
        _docker.OneOffCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_job_that_failed_still_moves_on_to_its_next_time()
    {
        // Otherwise a failing job re-fires every minute for ever, and the history that was supposed
        // to explain the failure is buried under a thousand identical rows.
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1));
        _docker.OneOffExitCode = 7;

        await Runner().TickAsync(default);

        var (reloaded, runs) = Reload(app.Id);
        runs.Should().ContainSingle().Which.ExitCode.Should().Be(7);
        reloaded.NextRunAt.Should().BeAfter(_clock.UtcNow);
    }

    [Fact]
    public async Task A_gap_in_uptime_is_not_replayed()
    {
        // A panel that was down for a day must not wake up and fire yesterday's job repeatedly. It
        // runs once, then goes back on schedule — and the gap stays visible in the history.
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddDays(-3));

        await Runner().TickAsync(default);
        await Runner().TickAsync(default);
        await Runner().TickAsync(default);

        var (reloaded, runs) = Reload(app.Id);
        runs.Should().ContainSingle("the missed days are gone, not queued up");
        reloaded.NextRunAt.Should().Be(new DateTimeOffset(2026, 7, 31, 3, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_stopped_job_does_not_run()
    {
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1), status: AppStatus.Stopped);

        await Runner().TickAsync(default);

        Reload(app.Id).Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unreadable_schedule_is_recorded_once_and_never_run()
    {
        // Shouting once a minute for ever in the log is how a real problem becomes invisible.
        var app = Given("not a schedule", nextRunAt: _clock.UtcNow.AddMinutes(-1));

        await Runner().TickAsync(default);
        await Runner().TickAsync(default);

        var (reloaded, runs) = Reload(app.Id);
        runs.Should().BeEmpty();
        reloaded.NextRunAt.Should().BeNull("nothing is due, and the row says so");
    }

    [Fact]
    public async Task A_job_that_has_never_been_deployed_says_so_instead_of_failing_silently()
    {
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1), image: "");
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            var tracked = db.Apps.Single(a => a.Id == app.Id);
            tracked.SourceType = AppSourceType.GitRepository;
            tracked.ActiveDeploymentId = null;
            db.SaveChanges();
        }

        await Runner().TickAsync(default);

        var run = Reload(app.Id).Runs.Should().ContainSingle().Subject;
        run.Error.Should().Contain("never been deployed");
        run.ExitCode.Should().BeNull("it never got as far as an exit code");
    }

    [Fact]
    public async Task A_secret_reaches_the_job_as_its_real_value()
    {
        // A backup script handed an encrypted password fails for a reason nobody could guess from
        // the message it prints.
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1));
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            db.EnvironmentVariables.Add(new EnvironmentVariable
            {
                AppId = app.Id, Key = "PGPASSWORD", Value = protector.Protect("s3cret"), IsSecret = true
            });
            db.SaveChanges();
        }

        await Runner().TickAsync(default);

        var request = _docker.OneOffRequests.Should().ContainSingle().Subject;
        request.Env!["PGPASSWORD"].Should().Be("s3cret");
    }

    [Fact]
    public async Task What_the_job_printed_is_what_gets_recorded()
    {
        // "What did it say" is one of the three questions run history exists to answer, and the one
        // that is useless if it arrives empty. Observed live: a run recorded the image pull and not
        // a word the job itself printed, because the output was handed over asynchronously and the
        // last lines had not arrived when the row was written.
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1));
        _docker.OneOffOutput.Add("NIGHTLY_JOB_RAN");

        await Runner().TickAsync(default);

        Reload(app.Id).Runs.Should().ContainSingle()
            .Which.Output.Should().Contain("NIGHTLY_JOB_RAN");
    }

    [Fact]
    public async Task Framing_bytes_in_a_jobs_output_do_not_lose_the_whole_run()
    {
        // The run row is written to the same PostgreSQL that cannot hold a NUL byte.
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1));
        _docker.OneOffOutput.Add("\0\0\0\0\0\0BACKUP_COMPLETE");

        await Runner().TickAsync(default);

        var run = Reload(app.Id).Runs.Should().ContainSingle().Subject;
        run.Output.Should().Be("BACKUP_COMPLETE");
    }

    [Fact]
    public async Task A_run_joins_the_projects_own_network()
    {
        // Otherwise a job is handed the environment variables naming its database and no route to
        // reach it — a failure that looks exactly like wrong credentials and is not. EnvironmentId is
        // required now (P2, 2026-08-17 app-environment-management design), so Given() already placed
        // this app in its own project and environment — the network it must run on is theirs, not the
        // workspace's.
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1));
        string expectedNetwork;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
            {
                Id = _workspaceId, Name = "Acme", Slug = "acme"
            });
            db.SaveChanges();

            var placement = db.Environments
                .Where(e => e.Id == app.EnvironmentId)
                .Select(e => new { e.Slug, ProjectSlug = e.Project!.Slug })
                .Single();
            expectedNetwork = Harbora.Infrastructure.Networking.EnvironmentNetwork.For(
                placement.ProjectSlug, placement.Slug, app.EnvironmentId);
        }

        await Runner().TickAsync(default);

        _docker.OneOffRequests.Should().ContainSingle()
            .Which.NetworkMode.Should().Be(expectedNetwork);
    }

    [Fact]
    public async Task A_cron_job_in_a_non_default_environment_runs_on_that_environments_network()
    {
        // The database this job talks to was deployed onto its environment's network, not the
        // workspace's. Before this fix the runner always built the workspace network, so a cron
        // app in staging could not resolve its own database — a failure that looks exactly like
        // wrong credentials and is not.
        var project = new Harbora.Domain.Projects.Project
        {
            WorkspaceId = _workspaceId, Name = "Acme API", Slug = "acme-api"
        };
        var environment = new Harbora.Domain.Projects.Environment
        {
            WorkspaceId = _workspaceId, ProjectId = project.Id, Name = "Staging", Slug = "staging",
            IsDefault = false
        };
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1));
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.Workspaces.Add(new Harbora.Domain.Identity.Workspace { Id = _workspaceId, Name = "Acme", Slug = "acme" });
            db.Projects.Add(project);
            db.Environments.Add(environment);
            db.Apps.Single(a => a.Id == app.Id).EnvironmentId = environment.Id;
            db.SaveChanges();
        }

        await Runner().TickAsync(default);

        var expectedNetwork = Harbora.Infrastructure.Networking.EnvironmentNetwork.For(
            project.Slug, environment.Slug, environment.Id);
        _docker.OneOffRequests.Should().ContainSingle()
            .Which.NetworkMode.Should().Be(expectedNetwork);
    }

    // The test that used to live here, A_cron_job_with_no_environment_still_runs_on_the_workspace_network,
    // asserted the opposite of what P2 (2026-08-17 app-environment-management design) makes true:
    // EnvironmentId is a required Guid now, so "app.EnvironmentId is deliberately left null" — its own
    // comment — is no longer something a test in this file can construct. Deleted rather than inverted:
    // Given() places every app in a real environment, and A_run_joins_the_projects_own_network already
    // proves a cron run reaches it.

    [Fact]
    public async Task Running_a_job_by_hand_does_not_move_its_schedule()
    {
        // The whole point of a "run now" button is to try tonight's job now. Shifting the schedule
        // would mean testing a backup quietly cancels the one that mattered.
        var app = Given("0 3 * * *", nextRunAt: new DateTimeOffset(2026, 7, 31, 3, 0, 0, TimeSpan.Zero));

        using (var scope = _sp.CreateScope())
            await JobRunner(scope).RunAsync(app.Id, default);

        var (reloaded, runs) = Reload(app.Id);
        reloaded.NextRunAt.Should().Be(new DateTimeOffset(2026, 7, 31, 3, 0, 0, TimeSpan.Zero));
        runs.Should().ContainSingle().Which.IsManual.Should().BeTrue();
    }

    [Fact]
    public async Task A_scheduled_run_is_not_recorded_as_one_someone_asked_for()
    {
        // The guard on the flag above: if everything looked manual it would explain nothing.
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1));

        await Runner().TickAsync(default);

        Reload(app.Id).Runs.Should().ContainSingle().Which.IsManual.Should().BeFalse();
    }

    [Fact]
    public async Task A_job_that_is_already_running_is_not_started_a_second_time()
    {
        // A held-down button would otherwise start a container per press, and a job that outlasts its
        // own interval would overlap itself.
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1));
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.CronRuns.Add(new CronRun { WorkspaceId = _workspaceId, AppId = app.Id, StartedAt = _clock.UtcNow });
            db.SaveChanges();
        }

        using (var scope = _sp.CreateScope())
            await JobRunner(scope).RunAsync(app.Id, default);

        _docker.OneOffCommands.Should().BeEmpty();
        Reload(app.Id).Runs.Should().ContainSingle("the run in flight is the only one");
    }

    [Fact]
    public async Task A_run_a_restart_interrupted_is_settled_rather_than_left_running_for_ever()
    {
        // A row with no finish time is shown as still running — and would block the job from ever
        // starting again, because of the guard above.
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1));
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.CronRuns.Add(new CronRun
            {
                WorkspaceId = _workspaceId, AppId = app.Id, StartedAt = _clock.UtcNow.AddHours(-2)
            });
            db.SaveChanges();
        }

        using (var scope = _sp.CreateScope())
            await JobRunner(scope).ReconcileAsync(default);

        var settled = Reload(app.Id).Runs.Should().ContainSingle().Subject;
        settled.FinishedAt.Should().NotBeNull();
        settled.Error.Should().Contain("Interrupted");

        // And the job can run again afterwards, which is the reason for settling it.
        using (var scope = _sp.CreateScope())
            await JobRunner(scope).RunAsync(app.Id, default);
        Reload(app.Id).Runs.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_queued_run_whose_service_is_no_longer_a_scheduled_job_is_dropped()
    {
        // The queue is durable: a request can be claimed long after it was made. By then the service
        // may be something that has no business being started as a one-off container.
        var app = Given("0 3 * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1));
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.Apps.Single(a => a.Id == app.Id).Kind = ServiceKind.Web;
            db.SaveChanges();
        }

        using (var scope = _sp.CreateScope())
            await JobRunner(scope).RunAsync(app.Id, default);

        _docker.OneOffCommands.Should().BeEmpty();
        Reload(app.Id).Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Running_a_job_that_does_not_exist_does_nothing()
    {
        using var scope = _sp.CreateScope();
        var act = async () => await JobRunner(scope).RunAsync(Guid.NewGuid(), default);

        await act.Should().NotThrowAsync("a deleted job must not fail the queue that carries it");
    }
    [Fact]
    public async Task Only_scheduled_services_are_considered()
    {
        // A web service with a stale schedule left on the row must not start running containers.
        var app = Given("* * * * *", nextRunAt: _clock.UtcNow.AddMinutes(-1));
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.Apps.Single(a => a.Id == app.Id).Kind = ServiceKind.Web;
            db.SaveChanges();
        }

        await Runner().TickAsync(default);

        Reload(app.Id).Runs.Should().BeEmpty();
    }
}
