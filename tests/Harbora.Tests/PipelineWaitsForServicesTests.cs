using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Jobs;
using Harbora.Domain.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The real pipeline, holding a new container back for a database still being set up beside it. The queue
/// being first-in-first-out only ever promised that a provision starts first, and a queue running several
/// jobs at once means it may not have finished.
///
/// <para>
/// Half of what this protects is the other half of the sentence: nothing provisioning means no wait and no
/// line, so every existing deployment behaves as it did, and a wait never fails a deployment that would have
/// worked.
/// </para>
/// </summary>
public class PipelineWaitsForServicesTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static PipelineHarness Harness()
    {
        var h = new PipelineHarness();
        h.Options.ServiceWaitPollSeconds = 0.01;
        return h;
    }

    private static ManagedService GivenService(
        PipelineHarness h, ServiceStatus status, string name = "orders", Guid? environmentId = null, Guid? workspaceId = null)
    {
        var service = new ManagedService
        {
            WorkspaceId = workspaceId ?? h.App.WorkspaceId,
            EnvironmentId = environmentId ?? h.App.EnvironmentId,
            ServerId = h.App.ServerId,
            Name = name,
            Type = ManagedServiceType.PostgreSql,
            Version = "16",
            Status = status,
            ContainerName = $"harbora-svc-{name}",
            VolumeName = $"harbora-svc-{name}-data"
        };
        h.Db.ManagedServices.Add(service);
        h.Db.SaveChanges();
        return service;
    }

    /// <summary>Another job writing the row, through a context of its own, while the pipeline holds this one.</summary>
    private static void Settle(PipelineHarness h, ManagedService service, ServiceStatus status)
    {
        using var other = h.OpenAnotherContext();
        other.ManagedServices.IgnoreQueryFilters().Single(s => s.Id == service.Id).Status = status;
        other.SaveChanges();
    }

    private static bool ContainerStarted(PipelineHarness h) =>
        h.Docker.Calls.Any(c => c.Operation == nameof(FakeDockerEngine.RunContainerAsync));

    /// <summary>Runs the pipeline off this thread and comes back once it has said it is waiting.</summary>
    private static async Task<Task<Deployment>> StartAndSeeItWaitAsync(PipelineHarness h)
    {
        var deployment = h.QueueDeployment();
        var run = Task.Run(() => h.RunAsync(deployment));
        await UntilAsync(() => WaitingLine(h) is not null || run.IsCompleted);
        WaitingLine(h).Should().NotBeNull("it should have said what it is waiting for");
        return run;
    }

    private static string? WaitingLine(PipelineHarness h) =>
        h.Stream.Lines.FirstOrDefault(l => l.StartsWith("Waiting for", StringComparison.Ordinal));

    private static async Task UntilAsync(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < Patience) await Task.Delay(5);
    }

    // ---- nothing to wait for ----

    [Fact]
    public async Task Nothing_provisioning_means_no_wait_and_no_line()
    {
        using var h = Harness();
        h.Options.ServiceWaitPollSeconds = 600; // a wait would be noticed
        GivenService(h, ServiceStatus.Running);
        GivenService(h, ServiceStatus.Stopped, "stopped");
        GivenService(h, ServiceStatus.Failed, "failed");

        var deployment = await h.RunAsync(h.QueueDeployment()).WaitAsync(Patience);

        deployment.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Stream.Lines.Should().NotContain(l => l.Contains("being set up"));
    }

    [Fact]
    public async Task No_databases_at_all_means_no_wait_and_no_line()
    {
        using var h = Harness();
        h.Options.ServiceWaitPollSeconds = 600;

        var deployment = await h.RunAsync(h.QueueDeployment()).WaitAsync(Patience);

        deployment.Status.Should().Be(DeploymentStatus.Succeeded);
        WaitingLine(h).Should().BeNull();
    }

    [Fact]
    public async Task A_database_in_another_environment_or_workspace_is_not_waited_for()
    {
        using var h = Harness();
        h.Options.ServiceWaitPollSeconds = 600;
        GivenService(h, ServiceStatus.Provisioning, "staging-db", environmentId: h.AddEnvironment().Id);
        GivenService(h, ServiceStatus.Provisioning, "strangers-db", workspaceId: Guid.CreateVersion7());

        var deployment = await h.RunAsync(h.QueueDeployment()).WaitAsync(Patience);

        deployment.Status.Should().Be(DeploymentStatus.Succeeded);
        WaitingLine(h).Should().BeNull();
    }

    [Fact]
    public async Task A_scheduled_job_starts_nothing_so_it_waits_for_nothing()
    {
        using var h = Harness();
        h.Options.ServiceWaitPollSeconds = 600;
        h.App.Kind = ServiceKind.Cron;
        h.App.CronExpression = "0 3 * * *";
        await h.Db.SaveChangesAsync();
        GivenService(h, ServiceStatus.Provisioning);

        var deployment = await h.RunAsync(h.QueueDeployment()).WaitAsync(Patience);

        deployment.Status.Should().Be(DeploymentStatus.Succeeded);
        WaitingLine(h).Should().BeNull();
    }

    // ---- waiting ----

    [Fact]
    public async Task It_waits_naming_what_for_and_starts_once_the_database_runs()
    {
        using var h = Harness();
        var service = GivenService(h, ServiceStatus.Provisioning);

        var run = await StartAndSeeItWaitAsync(h);

        WaitingLine(h).Should().Contain("'orders'").And.Contain("10 minutes");
        await Task.Delay(100);
        run.IsCompleted.Should().BeFalse("nothing has changed yet");
        ContainerStarted(h).Should().BeFalse("the database it may need is not there yet");

        Settle(h, service, ServiceStatus.Running);
        var deployment = await run.WaitAsync(Patience);

        deployment.Status.Should().Be(DeploymentStatus.Succeeded);
        ContainerStarted(h).Should().BeTrue();

        var lines = h.Stream.Lines.ToList();
        var ready = lines.FindIndex(l => l == "'orders' ready.");
        ready.Should().BeGreaterThan(-1, "one line says it is ready");
        ready.Should().BeLessThan(lines.FindIndex(l => l.StartsWith("Starting container", StringComparison.Ordinal)));
        lines.Count(l => l.StartsWith("Waiting for", StringComparison.Ordinal)).Should().Be(1, "a line, not a line per poll");
    }

    [Fact]
    public async Task It_waits_for_every_one_of_them()
    {
        using var h = Harness();
        var orders = GivenService(h, ServiceStatus.Provisioning);
        var cache = GivenService(h, ServiceStatus.Provisioning, "cache");

        var run = await StartAndSeeItWaitAsync(h);
        WaitingLine(h).Should().Contain("'orders'").And.Contain("'cache'");

        Settle(h, orders, ServiceStatus.Running);
        await Task.Delay(100);
        ContainerStarted(h).Should().BeFalse("one of them is still being set up");

        Settle(h, cache, ServiceStatus.Running);
        (await run.WaitAsync(Patience)).Status.Should().Be(DeploymentStatus.Succeeded);
    }

    [Fact]
    public async Task It_waits_before_the_release_task_too_which_is_the_first_thing_that_needs_the_database()
    {
        using var h = Harness();
        h.App.ReleaseCommand = "dotnet ef database update";
        await h.Db.SaveChangesAsync();
        var service = GivenService(h, ServiceStatus.Provisioning);

        var run = await StartAndSeeItWaitAsync(h);
        await Task.Delay(100);

        h.Docker.Calls.Select(c => c.Operation).Should().NotContain(nameof(FakeDockerEngine.RunOneOffAsync));

        Settle(h, service, ServiceStatus.Running);
        await run.WaitAsync(Patience);

        h.Docker.Calls.Select(c => c.Operation).Should().Contain(nameof(FakeDockerEngine.RunOneOffAsync));
    }

    // ---- carrying on ----

    [Fact]
    public async Task A_database_that_ends_failed_is_a_line_and_the_deployment_carries_on()
    {
        using var h = Harness();
        var service = GivenService(h, ServiceStatus.Provisioning);

        var run = await StartAndSeeItWaitAsync(h);
        Settle(h, service, ServiceStatus.Failed);
        var deployment = await run.WaitAsync(Patience);

        deployment.Status.Should().Be(DeploymentStatus.Succeeded, "the app might not need it");
        ContainerStarted(h).Should().BeTrue();
        h.Stream.Lines.Should().ContainSingle(l => l.Contains("'orders' ended Failed") && l.Contains("starting this app anyway"));
    }

    [Fact]
    public async Task At_the_bound_it_says_so_and_carries_on_and_not_a_moment_before()
    {
        using var h = Harness();
        GivenService(h, ServiceStatus.Provisioning);
        var started = h.Clock.UtcNow;

        var run = await StartAndSeeItWaitAsync(h);

        h.Clock.UtcNow = started.AddMinutes(9).AddSeconds(59);
        await Task.Delay(100);
        run.IsCompleted.Should().BeFalse("the ten minutes are not up");
        ContainerStarted(h).Should().BeFalse();

        h.Clock.UtcNow = started.AddMinutes(10);
        var deployment = await run.WaitAsync(Patience);

        deployment.Status.Should().Be(DeploymentStatus.Succeeded);
        ContainerStarted(h).Should().BeTrue();
        h.Stream.Lines.Should().ContainSingle(l => l.Contains("'orders' still being set up after 10 minutes"));
    }

    [Fact]
    public async Task A_database_removed_while_waiting_ends_the_wait_with_a_line_saying_so()
    {
        using var h = Harness();
        var service = GivenService(h, ServiceStatus.Provisioning);

        var run = await StartAndSeeItWaitAsync(h);
        using (var other = h.OpenAnotherContext())
        {
            other.ManagedServices.Remove(other.ManagedServices.IgnoreQueryFilters().Single(s => s.Id == service.Id));
            other.SaveChanges();
        }

        (await run.WaitAsync(Patience)).Status.Should().Be(DeploymentStatus.Succeeded);
        h.Stream.Lines.Should().Contain("'orders' no longer there.");
    }

    // ---- stopping ----

    [Fact]
    public async Task A_cancelled_token_stops_the_wait_and_nothing_is_started()
    {
        using var h = Harness();
        GivenService(h, ServiceStatus.Provisioning);
        var deployment = h.QueueDeployment();
        using var cancel = new CancellationTokenSource();

        var run = Task.Run(() => h.BuildPipeline().ExecuteAsync(deployment.Id, cancel.Token));
        await UntilAsync(() => WaitingLine(h) is not null);
        WaitingLine(h).Should().NotBeNull();

        await cancel.CancelAsync();

        // However the pipeline chooses to record it, it has to stop, and well before the bound.
        await FluentActions.Awaiting(() => run.WaitAsync(Patience))
            .Should().NotThrowAsync<TimeoutException>();
        ContainerStarted(h).Should().BeFalse();
        deployment.Status.Should().NotBe(DeploymentStatus.Succeeded);
    }

    // ---- saying why it is not getting anywhere ----

    private static void GivenJob(PipelineHarness h, JobKind kind, Guid target, JobStatus status)
    {
        h.Db.Jobs.Add(new Job { Kind = kind, TargetId = target, Status = status, WorkspaceId = h.App.WorkspaceId });
        h.Db.SaveChanges();
    }

    [Fact]
    public async Task With_the_only_worker_held_by_this_deployment_the_line_says_the_setup_cannot_start()
    {
        using var h = Harness();
        h.JobQueue.MaxConcurrency = 1;
        var service = GivenService(h, ServiceStatus.Provisioning);
        var deployment = h.QueueDeployment();
        GivenJob(h, JobKind.Deployment, deployment.Id, JobStatus.Running);
        GivenJob(h, JobKind.ServiceProvision, service.Id, JobStatus.Pending);
        var started = h.Clock.UtcNow;

        var run = Task.Run(() => h.RunAsync(deployment));
        await UntilAsync(() => WaitingLine(h) is not null);

        WaitingLine(h).Should().Contain("no worker is free");

        h.Clock.UtcNow = started.AddMinutes(10);
        (await run.WaitAsync(Patience)).Status.Should().Be(DeploymentStatus.Succeeded);
        h.Stream.Lines.Should().ContainSingle(l => l.Contains("still being set up after 10 minutes") && l.Contains("no worker is free"));
    }

    [Fact]
    public async Task With_a_worker_free_the_line_does_not_blame_the_queue()
    {
        using var h = Harness();
        h.JobQueue.MaxConcurrency = 4;
        var service = GivenService(h, ServiceStatus.Provisioning);
        var deployment = h.QueueDeployment();
        GivenJob(h, JobKind.Deployment, deployment.Id, JobStatus.Running);
        GivenJob(h, JobKind.ServiceProvision, service.Id, JobStatus.Pending);

        var run = Task.Run(() => h.RunAsync(deployment));
        await UntilAsync(() => WaitingLine(h) is not null);

        WaitingLine(h).Should().NotContain("no worker");

        Settle(h, service, ServiceStatus.Running);
        await run.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_setup_job_already_running_is_not_said_to_be_queued()
    {
        using var h = Harness();
        h.JobQueue.MaxConcurrency = 1;
        var service = GivenService(h, ServiceStatus.Provisioning);
        var deployment = h.QueueDeployment();
        GivenJob(h, JobKind.ServiceProvision, service.Id, JobStatus.Running);

        var run = Task.Run(() => h.RunAsync(deployment));
        await UntilAsync(() => WaitingLine(h) is not null);

        WaitingLine(h).Should().NotContain("no worker");

        Settle(h, service, ServiceStatus.Running);
        await run.WaitAsync(Patience);
    }
}
