using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Asking the engine about one container.
///
/// The panel could list containers and read a State and a Status string, and that was all — so
/// "how long has this been up" and "which image is actually running" had no source at all. The node
/// agent has extracted both since it was written; this is the same question asked of the local engine.
/// </summary>
public class ContainerDetailTests
{
    [Fact]
    public async Task An_engine_answers_with_what_it_was_told_about_the_container()
    {
        var engine = new FakeDockerEngine();
        engine.SeedDetail("harbora-blog-2", new ContainerDetail(
            Id: "abc123", Name: "harbora-blog-2", Image: "harbora/blog:build-2",
            ImageDigest: "sha256:1111111111111111111111111111111111111111111111111111111111111111",
            State: "running", Status: "Up 3 hours",
            Healthy: true, RestartCount: 2,
            StartedAt: new DateTimeOffset(2026, 8, 15, 6, 0, 0, TimeSpan.Zero)));

        var detail = await engine.InspectAsync("harbora-blog-2", CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.RestartCount.Should().Be(2);
        detail.ImageDigest.Should().Be(
            "sha256:1111111111111111111111111111111111111111111111111111111111111111");
        detail.StartedAt.Should().Be(new DateTimeOffset(2026, 8, 15, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_container_the_engine_has_never_heard_of_is_null_not_an_empty_detail()
    {
        var engine = new FakeDockerEngine();

        (await engine.InspectAsync("harbora-nothing-1", CancellationToken.None))
            .Should().BeNull("an empty detail would reach the page as a real answer full of zeroes");
    }

    [Fact]
    public async Task A_container_with_no_health_check_reports_unknown_rather_than_unhealthy()
    {
        var engine = new FakeDockerEngine();
        engine.SeedDetail("harbora-worker-1", new ContainerDetail(
            Id: "def456", Name: "harbora-worker-1", Image: "harbora/worker:build-1",
            ImageDigest: null, State: "running", Status: "Up 10 minutes",
            Healthy: null, RestartCount: 0, StartedAt: null));

        var detail = await engine.InspectAsync("harbora-worker-1", CancellationToken.None);

        detail!.Healthy.Should().BeNull(
            "no health check configured is not 'unhealthy' — it is 'we were not told how to ask', " +
            "the distinction DockerContainerRuntime already makes explicitly");
    }
}
