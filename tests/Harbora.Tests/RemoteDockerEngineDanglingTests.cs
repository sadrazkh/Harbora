using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Docker;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="RemoteDockerEngine"/>'s two dangling-image calls, against a scripted agent.
///
/// <para>
/// The behaviour that matters here is the one the agent's age decides. An inbound agent keeps the code
/// it was installed with until somebody updates it, so an agent deployed before these endpoints existed
/// answers 404 to them. That is "this agent is too old to be swept" — reported as not examined, with a
/// reason, and never as zero (which reads as clean) or as a failure (which raises an alarm about a
/// machine that is working exactly as installed). Any other non-success is a real failure and must not
/// be laundered into that reading.
/// </para>
///
/// <para>
/// What is NOT proven: a real <c>Harbora.Agent</c> serving these routes over a real daemon. The agent's
/// two routes are one-line pass-throughs to <see cref="IDockerEngine"/> in
/// <c>src/Harbora.Agent/Program.cs</c>; the JSON shapes used below are the ones its default
/// serializer produces for <see cref="DanglingImages"/> and <see cref="DanglingImagesPruned"/>.
/// </para>
/// </summary>
public class RemoteDockerEngineDanglingTests
{
    private static RemoteDockerEngine EngineOver(StubAgentHandler agent) =>
        new(new StubAgentHandler.Factory(agent), "http://node.example.com", "s3cret");

    private static StubAgentHandler Agent(HttpStatusCode status, string body = "") =>
        new(_ => StubAgentHandler.Json(body, status));

    // ---- the agent has the endpoints ----

    [Fact]
    public async Task The_read_asks_the_agents_dangling_endpoint_with_the_bearer_token_and_reads_its_answer()
    {
        var agent = Agent(HttpStatusCode.OK, """{"count":165,"sizeBytes":75330000000}""");

        var found = await EngineOver(agent).GetDanglingImagesAsync(default);

        found.Should().Be(new DanglingImages(165, 75_330_000_000));
        agent.Requests.Should().Equal(new[] { (HttpMethod.Get, "/agent/images/dangling") });
        agent.LastAuthorization.Should().Be("Bearer s3cret");
    }

    [Fact]
    public async Task The_prune_posts_only_to_the_dangling_prune_endpoint_and_reads_the_agents_figures()
    {
        var agent = Agent(HttpStatusCode.OK, """{"imagesDeleted":165,"bytesReclaimed":74920000000}""");

        var pruned = await EngineOver(agent).PruneDanglingImagesAsync(default);

        pruned.Should().Be(new DanglingImagesPruned(165, 74_920_000_000));
        agent.Requests.Should().Equal(
            new[] { (HttpMethod.Post, "/agent/images/dangling/prune") },
            "one call to the daemon's own prune on that machine — never the image-remove endpoint, " +
            "which is the only route from here to a named image");
    }

    [Fact]
    public void The_agent_serves_exactly_the_two_routes_the_engine_calls_and_hands_them_to_the_engine()
    {
        // The tests above prove which URLs the engine calls; nothing here can boot Harbora.Agent (it is
        // its own host with no test project). This ties the two ends together at the source: if the
        // agent's routes and the engine's URLs ever drift, every agent in the field reads as "too old"
        // — a 404 — and nothing is swept, silently. The agent must also never answer 404 itself.
        var program = File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot, "src", "Harbora.Agent", "Program.cs"));

        program.Should().Contain("app.MapGet(\"/agent/images/dangling\", (IDockerEngine e, CancellationToken ct) => e.GetDanglingImagesAsync(ct));");
        program.Should().Contain("app.MapPost(\"/agent/images/dangling/prune\", (IDockerEngine e, CancellationToken ct) => e.PruneDanglingImagesAsync(ct));");
    }

    // ---- the agent predates them ----

    [Fact]
    public async Task A_404_is_an_agent_too_old_to_be_swept_and_says_so_for_the_read()
    {
        var thrown = await FluentActions.Awaiting(() => EngineOver(Agent(HttpStatusCode.NotFound)).GetDanglingImagesAsync(default))
            .Should().ThrowAsync<ImageSweepUnavailableException>();

        thrown.Which.Reason.Should().Be(RemoteDockerEngine.AgentTooOldReason);
        thrown.Which.Reason.Should().Contain("too old").And.Contain("update");
    }

    [Fact]
    public async Task A_404_is_an_agent_too_old_to_be_swept_and_says_so_for_the_prune()
    {
        var thrown = await FluentActions.Awaiting(() => EngineOver(Agent(HttpStatusCode.NotFound)).PruneDanglingImagesAsync(default))
            .Should().ThrowAsync<ImageSweepUnavailableException>();

        thrown.Which.Reason.Should().Be(RemoteDockerEngine.AgentTooOldReason);
    }

    // ---- anything else is a real failure, not "too old" ----

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Any_other_non_success_is_thrown_as_a_failure_and_never_read_as_an_old_agent(HttpStatusCode status)
    {
        // A wrong token (401) or a daemon that errored (500) means the endpoint EXISTS. Calling that "too
        // old" would send an operator to update an agent that is fine.
        var read = await FluentActions.Awaiting(() => EngineOver(Agent(status)).GetDanglingImagesAsync(default))
            .Should().ThrowAsync<HttpRequestException>();
        var prune = await FluentActions.Awaiting(() => EngineOver(Agent(status)).PruneDanglingImagesAsync(default))
            .Should().ThrowAsync<HttpRequestException>();

        read.Which.Should().NotBeAssignableTo<ImageSweepUnavailableException>();
        prune.Which.Should().NotBeAssignableTo<ImageSweepUnavailableException>();
    }

    [Fact]
    public async Task An_empty_body_is_a_failure_not_a_reading_of_zero()
    {
        var agent = Agent(HttpStatusCode.OK, "null");

        await FluentActions.Awaiting(() => EngineOver(agent).GetDanglingImagesAsync(default))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => EngineOver(agent).PruneDanglingImagesAsync(default))
            .Should().ThrowAsync<InvalidOperationException>();
    }
}
