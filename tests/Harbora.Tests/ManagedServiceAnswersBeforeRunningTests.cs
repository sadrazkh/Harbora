using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The launch proof's real Laravel project failed its migration with "SQLSTATE[HY000] [2002] Connection
/// refused": the database read Running the moment its container started, and MySQL was still initialising
/// behind it. A launch waits for Running and deploys on it, and a release task is the first thing to touch
/// the database. Running has to mean that it answers.
/// </summary>
public class ManagedServiceAnswersBeforeRunningTests
{
    private static readonly HarboraRuntimeOptions NoPause = new() { ServiceAnswerPollSeconds = 0, ServiceAnswerAttempts = 3 };

    /// <summary>MySQL, where the gap is widest, and which has no certificate one-off ahead of the probe.</summary>
    private static async Task<Harbora.Domain.Services.ManagedService> SeedMySqlAsync(ServiceFailureHarness h)
    {
        var svc = await h.SeedServiceAsync();
        svc.Type = ManagedServiceType.MySql;
        svc.Version = "8.4";
        svc.InternalPort = 3306;
        await h.Db.SaveChangesAsync();
        return svc;
    }

    [Fact]
    public async Task It_stays_provisioning_until_the_database_answers()
    {
        using var h = new ServiceFailureHarness();
        var svc = await SeedMySqlAsync(h);

        // Refused, refused, answered.
        h.Docker.OneOffExitCodes.Enqueue(1);
        h.Docker.OneOffExitCodes.Enqueue(1);
        h.Docker.OneOffExitCodes.Enqueue(0);

        var whileAsking = new List<ServiceStatus>();
        h.Docker.OnOneOff = _ => whileAsking.Add(h.Db.ManagedServices.IgnoreQueryFilters().AsNoTracking().Single(s => s.Id == svc.Id).Status);

        await h.Engine(NoPause).ProvisionAsync(svc.Id, default);

        whileAsking.Should().Equal(ServiceStatus.Provisioning, ServiceStatus.Provisioning, ServiceStatus.Provisioning);
        h.Docker.RunRequests.Should().ContainSingle("it is asked of the one container, not started again");

        var reloaded = await h.ReadServiceAsync(svc.Id);
        reloaded.Status.Should().Be(ServiceStatus.Running);
        reloaded.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task It_is_asked_with_its_own_client_on_its_own_network()
    {
        using var h = new ServiceFailureHarness();
        var svc = await SeedMySqlAsync(h);

        await h.Engine(NoPause).ProvisionAsync(svc.Id, default);

        var asked = h.Docker.OneOffRequests.Should().ContainSingle().Subject;
        string.Join(' ', asked.Command).Should().Contain("mysql").And.Contain(svc.ContainerName);
        asked.NetworkMode.Should().Be(h.Docker.RunRequests.Single().NetworkName);
    }

    [Fact]
    public async Task A_database_that_never_answers_has_not_come_up_and_says_so()
    {
        using var h = new ServiceFailureHarness();
        var svc = await SeedMySqlAsync(h);
        h.Docker.OneOffExitCode = 1;
        h.Docker.OneOffOutput.Add("ERROR 2002 (HY000): connection refused");

        await h.Engine(NoPause).ProvisionAsync(svc.Id, default);

        h.Docker.OneOffRequests.Should().HaveCount(NoPause.ServiceAnswerAttempts);

        var reloaded = await h.ReadServiceAsync(svc.Id);
        reloaded.Status.Should().Be(ServiceStatus.Failed, "a launch would otherwise deploy beside a database that is not there");
        reloaded.ErrorMessage.Should().Contain("did not answer").And.Contain("Nothing is listening");
    }

    [Fact]
    public async Task A_probe_that_cannot_run_here_is_no_claim_either_way()
    {
        // A v1 node has no one-offs, and it provisioned databases before there was anything to ask.
        using var h = new ServiceFailureHarness();
        var svc = await SeedMySqlAsync(h);
        h.Docker.OneOffThrows = new NotSupportedException("this node cannot run one-offs");

        await h.Engine(NoPause).ProvisionAsync(svc.Id, default);

        (await h.ReadServiceAsync(svc.Id)).Status.Should().Be(ServiceStatus.Running);
        h.Docker.OneOffRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task An_engine_with_no_shell_client_is_not_asked()
    {
        using var h = new ServiceFailureHarness();
        var svc = await SeedMySqlAsync(h);
        svc.Type = ManagedServiceType.RabbitMq;
        svc.Version = "3";
        await h.Db.SaveChangesAsync();

        await h.Engine(NoPause).ProvisionAsync(svc.Id, default);

        (await h.ReadServiceAsync(svc.Id)).Status.Should().Be(ServiceStatus.Running);
        h.Docker.OneOffRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_docker_error_while_asking_is_no_answer_yet()
    {
        using var h = new ServiceFailureHarness();
        var svc = await SeedMySqlAsync(h);
        var calls = 0;
        h.Docker.OnOneOff = _ =>
        {
            if (++calls == 1) throw new InvalidOperationException("the daemon hiccuped");
        };

        await h.Engine(NoPause).ProvisionAsync(svc.Id, default);

        (await h.ReadServiceAsync(svc.Id)).Status.Should().Be(ServiceStatus.Running);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task A_job_that_runs_out_of_time_while_asking_records_the_failure_as_any_other_does()
    {
        using var h = new ServiceFailureHarness();
        var svc = await SeedMySqlAsync(h);
        h.Docker.OneOffExitCode = 1;

        using var deadline = new CancellationTokenSource();
        h.Docker.OnOneOff = _ => deadline.Cancel();

        await h.Engine(new HarboraRuntimeOptions { ServiceAnswerPollSeconds = 0, ServiceAnswerAttempts = 50 })
            .ProvisionAsync(svc.Id, deadline.Token);

        (await h.ReadServiceAsync(svc.Id)).Status.Should().Be(ServiceStatus.Failed);
    }
}
