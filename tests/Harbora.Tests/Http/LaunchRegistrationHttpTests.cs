using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Launch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests.Http;

/// <summary>
/// That the real host can build a launch. Every other launch test constructs the service by hand, so a
/// dependency the container cannot supply would pass all of them and fail on the first request.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public sealed class LaunchRegistrationHttpTests(HarboraHttpFixture fixture)
{
    [Fact]
    public void The_host_can_build_the_service_and_the_job_that_reads_the_source()
    {
        using var scope = fixture.Panel.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ILaunchService>().Should().BeOfType<LaunchService>();
        scope.ServiceProvider.GetServices<IJobHandler>().Should().ContainSingle(h => h.Kind == JobKind.LaunchInspect);
    }

    [Fact]
    public void The_tick_is_registered_because_the_test_host_takes_every_background_worker_out()
    {
        var root = Path.GetFullPath(Path.Combine(TestPaths.WebRoot, "..", ".."));

        File.ReadAllText(Path.Combine(root, "src", "Harbora.Infrastructure", "DependencyInjection.cs"))
            .Should().Contain("AddHostedService<Launch.LaunchTicker>",
                "with no tick, a confirmed launch sits on WaitingForServices for ever and nothing says so");
    }
}
