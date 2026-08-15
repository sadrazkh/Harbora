using Docker.DotNet.Models;
using FluentAssertions;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The real mapping <see cref="DockerEngine.InspectAsync"/> uses to turn a Docker inspect response
/// into a <see cref="Harbora.Application.Abstractions.ContainerDetail"/>.
///
/// <see cref="ContainerDetailTests"/> only ever asked <see cref="Fakes.FakeDockerEngine"/> to echo
/// back what a test seeded into it — no test in the repository constructed a
/// <see cref="DockerEngine"/> or fed it a Docker.DotNet response, so this exact mapping (the
/// no-health-check case in particular) shipped inverted from what it claimed to do. Constructing a
/// real <see cref="DockerEngine"/> needs a Docker daemon behind <c>IDockerClient</c>, which this
/// suite does not have; <see cref="ContainerInspectResponse"/> and its nested records are plain
/// settable POCOs, though, so the mapping itself — extracted as <c>DockerEngine.MapDetail</c> — is
/// reachable and tested directly here, with no daemon involved.
/// </summary>
public class DockerEngineInspectMappingTests
{
    private static ContainerInspectResponse Response(ContainerState state) => new()
    {
        ID = "abc123",
        Name = "/harbora-blog-2",
        Image = "sha256:imagehash",
        Config = new Config { Image = "harbora/blog:build-2" },
        RestartCount = 2,
        State = state
    };

    [Fact]
    public void A_container_with_no_health_check_configured_reports_healthy_as_null_not_running()
    {
        // The exact regression this task exists to close: Running is a non-nullable bool, so a
        // mapping that fell back to it for an unset Health reported every running container as
        // healthy, whether or not anything ever checked.
        var response = Response(new ContainerState { Status = "running", Running = true, Health = null });

        var detail = DockerEngine.MapDetail(response, "harbora-blog-2");

        detail.Healthy.Should().BeNull(
            "no health check configured is 'we were not told how to ask', not an affirmative verdict");
    }

    [Fact]
    public void A_passing_health_check_reports_healthy_true()
    {
        var response = Response(new ContainerState
        { Status = "running", Running = true, Health = new Health { Status = "healthy" } });

        var detail = DockerEngine.MapDetail(response, "harbora-blog-2");

        detail.Healthy.Should().BeTrue();
    }

    [Fact]
    public void A_failing_health_check_reports_healthy_false_even_while_running()
    {
        var response = Response(new ContainerState
        { Status = "running", Running = true, Health = new Health { Status = "unhealthy" } });

        var detail = DockerEngine.MapDetail(response, "harbora-blog-2");

        detail.Healthy.Should().BeFalse();
    }

    [Fact]
    public void A_container_created_but_never_started_reports_no_start_time()
    {
        // Docker's Go zero time for a container that has never run. DateTimeOffset.TryParse accepts
        // it as a real (year 1) instant, so without a guard the view computed an uptime of hundreds
        // of thousands of days from it instead of saying the start time is unknown.
        var response = Response(new ContainerState
        { Status = "created", Running = false, StartedAt = "0001-01-01T00:00:00Z" });

        var detail = DockerEngine.MapDetail(response, "harbora-blog-2");

        detail.StartedAt.Should().BeNull();
    }

    [Fact]
    public void A_container_that_has_actually_started_reports_its_real_start_time()
    {
        var response = Response(new ContainerState
        { Status = "running", Running = true, StartedAt = "2026-08-15T06:00:00Z" });

        var detail = DockerEngine.MapDetail(response, "harbora-blog-2");

        detail.StartedAt.Should().Be(new DateTimeOffset(2026, 8, 15, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void The_mapping_carries_the_restart_count_and_image_digest_straight_from_the_response()
    {
        var response = Response(new ContainerState { Status = "running", Running = true });

        var detail = DockerEngine.MapDetail(response, "harbora-blog-2");

        detail.RestartCount.Should().Be(2, "the response's own RestartCount, not a default");
        detail.Id.Should().Be("abc123");
        detail.Image.Should().Be("harbora/blog:build-2", "the config image, which is what actually runs");
        detail.ImageDigest.Should().Be("sha256:imagehash", "the resolved image reference Docker reports");
    }

    [Fact]
    public void A_container_with_no_reported_name_falls_back_to_what_it_was_asked_about()
    {
        var response = new ContainerInspectResponse
        {
            ID = "abc123",
            Name = null,
            Image = "sha256:imagehash",
            RestartCount = 0,
            State = new ContainerState { Status = "running", Running = true }
        };

        var detail = DockerEngine.MapDetail(response, "the-name-asked-for");

        detail.Name.Should().Be("the-name-asked-for");
    }
}
