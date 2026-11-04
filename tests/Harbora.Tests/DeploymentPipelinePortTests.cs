using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The tutorial and <c>harbora doctor</c> both say the platform sets <c>PORT</c>. Only the Node and
/// Python templates did, so an app with its own Dockerfile reading <c>$PORT</c> got nothing and listened
/// wherever its fallback said. Asserted on the environment the container actually received.
/// </summary>
public class DeploymentPipelinePortTests
{
    [Fact]
    public async Task A_web_app_is_told_the_port_traffic_is_sent_to()
    {
        using var h = new PipelineHarness().WithContainerPort(4000);

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.Docker.RunRequests.Should().ContainSingle().Which.Env["PORT"].Should().Be("4000");
    }

    [Fact]
    public async Task An_apps_own_port_variable_wins()
    {
        using var h = new PipelineHarness().WithContainerPort(4000);
        h.Db.EnvironmentVariables.Add(new EnvironmentVariable { AppId = h.App.Id, Key = "PORT", Value = "9000" });
        h.Db.SaveChanges();

        await h.RunAsync(h.QueueDeployment(number: 1));

        h.Docker.RunRequests.Should().ContainSingle().Which.Env["PORT"].Should().Be("9000");
    }

    [Fact]
    public async Task A_worker_takes_no_traffic_so_it_is_told_no_port()
    {
        using var h = new PipelineHarness();
        h.App.Kind = ServiceKind.Worker;
        h.Db.SaveChanges();

        await h.RunAsync(h.QueueDeployment(number: 1));

        h.Docker.RunRequests.Should().ContainSingle().Which.Env.Should().NotContainKey("PORT");
    }
}
