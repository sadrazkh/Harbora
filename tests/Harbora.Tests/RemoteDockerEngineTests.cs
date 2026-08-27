using System.Net;
using System.Text;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="RemoteDockerEngine"/> talks to a real node agent over HTTP, which this machine cannot
/// run — see docs/node-agent for why. What these tests cover instead is the panel-side contract
/// against a stubbed agent response, for two of its endpoints:
///
/// <list type="bullet">
/// <item><c>RunOneOffAsync</c> against <c>/agent/oneoff</c>: a returned "output" field is replayed
/// into the caller's log, and an agent old enough to have never learned about that field (only
/// "exitCode") does not throw and simply produces no output, exactly as it does today.</item>
/// <item><c>ListVolumesAsync</c> against <c>/agent/volumes</c> (HARBORA-0033's disk-side half): the
/// agent's JSON deserialises into the same <see cref="VolumeInfo"/> shape the local engine reports.</item>
/// </list>
///
/// A real agent round trip — the agent actually talking to its own Docker daemon — is not exercised
/// here in either case.
/// </summary>
public class RemoteDockerEngineTests
{
    private static DockerOneOffRequest Request() =>
        new("alpine:3.20", ["sh", "-c", "true"], []);

    [Fact]
    public async Task The_panel_replays_a_remote_oneoffs_output_into_the_callers_log()
    {
        var handler = new StubHandler("""{"exitCode":0,"output":"line one\nline two\n"}""");
        var engine = new RemoteDockerEngine(new Factory(handler), "http://node.example.com", "token");
        var lines = new List<string>();

        var exitCode = await engine.RunOneOffAsync(Request(), new InlineLog(lines.Add), default);

        exitCode.Should().Be(0);
        lines.Should().Equal("line one", "line two");
    }

    [Fact]
    public async Task An_agent_that_predates_the_output_field_returns_only_exit_code_and_that_does_not_throw()
    {
        var handler = new StubHandler("""{"exitCode":1}""");
        var engine = new RemoteDockerEngine(new Factory(handler), "http://node.example.com", "token");
        var lines = new List<string>();

        var exitCode = await engine.RunOneOffAsync(Request(), new InlineLog(lines.Add), default);

        exitCode.Should().Be(1, "the exit code is still read correctly from an older agent's response");
        lines.Should().BeEmpty("an older agent never collected output, so there is nothing to replay — not an error");
    }

    [Fact]
    public async Task An_agent_response_with_an_empty_output_field_yields_no_lines()
    {
        var handler = new StubHandler("""{"exitCode":0,"output":""}""");
        var engine = new RemoteDockerEngine(new Factory(handler), "http://node.example.com", "token");
        var lines = new List<string>();

        await engine.RunOneOffAsync(Request(), new InlineLog(lines.Add), default);

        lines.Should().BeEmpty();
    }

    [Fact]
    public async Task A_null_log_means_the_output_field_is_never_even_read()
    {
        // No log to replay into — RunOneOffAsync must not require one, the same as before this fix.
        var handler = new StubHandler("""{"exitCode":0,"output":"some line\n"}""");
        var engine = new RemoteDockerEngine(new Factory(handler), "http://node.example.com", "token");

        var exitCode = await engine.RunOneOffAsync(Request(), log: null, default);

        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task A_truncation_marker_the_agent_appended_is_replayed_like_any_other_line()
    {
        const string marker = "... [output truncated: exceeded 1048576 characters]";
        var handler = new StubHandler($$"""{"exitCode":0,"output":"kept line\n{{marker}}\n"}""");
        var engine = new RemoteDockerEngine(new Factory(handler), "http://node.example.com", "token");
        var lines = new List<string>();

        await engine.RunOneOffAsync(Request(), new InlineLog(lines.Add), default);

        lines.Should().Equal("kept line", marker);
    }

    // ---- ListVolumesAsync (HARBORA-0033's disk-side half) ----

    [Fact]
    public async Task Volumes_the_agent_reports_are_deserialised_with_their_name_and_creation_time()
    {
        var handler = new StubHandler(
            """[{"name":"harbora-vol-blog-data","createdAt":"2026-01-15T10:00:00+00:00"},{"name":"harbora-vol-gone-app-data","createdAt":null}]""");
        var engine = new RemoteDockerEngine(new Factory(handler), "http://node.example.com", "token");

        var volumes = await engine.ListVolumesAsync(default);

        volumes.Should().HaveCount(2);
        volumes.Should().ContainSingle(v => v.Name == "harbora-vol-blog-data" &&
            v.CreatedAt == DateTimeOffset.Parse("2026-01-15T10:00:00+00:00"));
        volumes.Should().ContainSingle(v => v.Name == "harbora-vol-gone-app-data" && v.CreatedAt == null);
    }

    [Fact]
    public async Task An_agent_reporting_no_volumes_at_all_answers_an_empty_list_rather_than_null()
    {
        var handler = new StubHandler("[]");
        var engine = new RemoteDockerEngine(new Factory(handler), "http://node.example.com", "token");

        var volumes = await engine.ListVolumesAsync(default);

        volumes.Should().BeEmpty();
    }

    private sealed class InlineLog(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(string jsonBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            });
    }
}
