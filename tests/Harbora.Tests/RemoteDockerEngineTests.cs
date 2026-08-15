using System.Net;
using System.Text;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="RemoteDockerEngine.RunOneOffAsync"/> talks to a real node agent over HTTP, which
/// this machine cannot run — see docs/node-agent for why. What these tests cover instead is the
/// panel-side contract against a stubbed <c>/agent/oneoff</c> response: that a returned "output"
/// field is replayed into the caller's log, and that an agent old enough to have never learned
/// about that field (only "exitCode") does not throw and simply produces no output, exactly as it
/// does today. A real agent round trip — the agent actually collecting a container's output and
/// putting it on the wire — is not exercised here.
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
