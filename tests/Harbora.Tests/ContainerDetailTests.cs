using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Asking <see cref="FakeDockerEngine"/> about one container — that it faithfully echoes back
/// whatever a test seeded into it, and answers null for anything it was never told about.
///
/// This is deliberately narrow: it exercises only <see cref="FakeDockerEngine.InspectAsync"/>, a
/// dictionary lookup, so it says nothing about whether a real engine's own inspect-response mapping
/// is correct. That mapping — including the no-health-check case this file used to claim coverage
/// of without ever reaching the code that decides it — is tested directly against
/// <see cref="Harbora.Infrastructure.Docker.DockerEngine.MapDetail"/> in
/// <see cref="DockerEngineInspectMappingTests"/>.
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
    public async Task A_fake_engine_seeded_with_no_health_check_echoes_healthy_as_null()
    {
        var engine = new FakeDockerEngine();
        engine.SeedDetail("harbora-worker-1", new ContainerDetail(
            Id: "def456", Name: "harbora-worker-1", Image: "harbora/worker:build-1",
            ImageDigest: null, State: "running", Status: "Up 10 minutes",
            Healthy: null, RestartCount: 0, StartedAt: null));

        var detail = await engine.InspectAsync("harbora-worker-1", CancellationToken.None);

        detail!.Healthy.Should().BeNull(
            "the fake returns exactly what was seeded — a null Healthy stays null through the " +
            "TryGetValue this test actually exercises");
    }
}
