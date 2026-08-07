using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Cli;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// `harbora cancel`, and the Ctrl+C that `harbora deploy --follow` used to ignore.
///
/// <para>
/// The follow loop polled with a bare <c>Task.Delay(1500)</c>, so a deployment could be followed but
/// not interrupted: Ctrl+C was noticed only when the process was killed hard enough. With a way to
/// stop the deployment itself now in the CLI, "stop watching" and "stop deploying" are two different
/// requests and both have to work.
/// </para>
/// </summary>
public class CliCancelTests
{
    /// <summary>A stand-in for the panel: records what was asked of it and answers as told.</summary>
    private sealed class Panel : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Path)> Calls = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Answer =
            _ => Json(HttpStatusCode.OK, "{}");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(Answer(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static ApiClient Client(Panel panel) =>
        new("https://panel.example.com", "tok", panel);

    /// <summary>
    /// Runs a command with the console redirected, so what a person would have seen can be asserted.
    /// Spectre wraps to a terminal width, hence the flattening — the sentence matters, its line
    /// breaks do not.
    /// </summary>
    private static async Task<(int Exit, string Output)> RunAsync(Func<Task<int>> command)
    {
        var previous = AnsiConsole.Console;
        var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer)
        });

        try { return (await command(), Regex.Replace(writer.ToString(), @"\s+", " ").Trim()); }
        finally { AnsiConsole.Console = previous; }
    }

    // ---- cancel ----

    [Fact]
    public async Task Cancel_posts_to_the_deployments_cancel_endpoint()
    {
        var panel = new Panel();
        var id = "0199aa11-2233-4455-6677-889900aabbcc";

        var (exit, _) = await RunAsync(() => CancelCommand.RunAsync(Client(panel), id, default));

        exit.Should().Be(0);
        panel.Calls.Should().ContainSingle()
            .Which.Should().Be((HttpMethod.Post, $"/api/v1/deployments/{id}/cancel"));
    }

    [Fact]
    public async Task A_refused_cancel_exits_one_and_repeats_what_the_server_said()
    {
        // The server's sentence, not the CLI's guess at it. "Cancel failed" alone would send someone
        // to the panel to find out what everybody already knew.
        var panel = new Panel
        {
            Answer = _ => Json(HttpStatusCode.Conflict,
                """{"error":"Deployment #42 had already ended (Succeeded), so there was nothing to cancel."}""")
        };

        var (exit, output) = await RunAsync(() => CancelCommand.RunAsync(Client(panel), "abc", default));

        exit.Should().Be(1);
        output.Should().Contain("Deployment #42 had already ended (Succeeded)");
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_also_exits_one()
    {
        var panel = new Panel { Answer = _ => throw new HttpRequestException("Connection refused") };

        var (exit, output) = await RunAsync(() => CancelCommand.RunAsync(Client(panel), "abc", default));

        exit.Should().Be(1);
        output.Should().Contain("Connection refused");
    }

    [Fact]
    public void Cancel_takes_a_deployment_id_and_honours_the_account_option()
    {
        var settings = typeof(CancelCommand.Settings);

        var argument = settings.GetProperty(nameof(CancelCommand.Settings.DeploymentId))!
            .GetCustomAttribute<CommandArgumentAttribute>();
        argument.Should().NotBeNull();
        // Angle brackets are Spectre's spelling of "required", which is what makes this
        // non-interactive-safe: it refuses rather than asking a question a pipeline cannot answer.
        argument!.ValueName.Should().Be("deploymentId");
        argument.IsRequired.Should().BeTrue();

        settings.GetProperty(nameof(CancelCommand.Settings.Account))!
            .GetCustomAttribute<CommandOptionAttribute>()!.LongNames
            .Should().Contain("account");
    }

    [Fact]
    public void The_cli_registers_the_command()
    {
        // A command nobody can invoke is the same gap this task exists to close, one layer up.
        CliSource("Program.cs").Should().Contain("AddCommand<CancelCommand>(\"cancel\")");
    }

    // ---- following logs ----

    [Fact]
    public async Task Following_logs_stops_when_the_command_has_already_been_cancelled()
    {
        // Before, StreamLogs took no token at all, so this could not even be asked.
        var panel = new Panel();
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();

        var exit = await DeployCommand.StreamLogs(Client(panel), "abc", stopped.Token);

        exit.Should().NotBe(0);
        panel.Calls.Should().BeEmpty("nothing should be asked of the server after Ctrl+C");
    }

    [Fact]
    public async Task Ctrl_C_during_the_wait_between_polls_is_observed_at_once()
    {
        // The defect exactly: the poll slept for 1.5 s with no token, so a cancellation arriving
        // during the wait was not seen until the wait was over. The bound is deliberately loose —
        // anything under the poll interval proves the delay is watching the token, and a machine
        // slow enough to fail this would have to lose more than a second on two in-memory calls.
        var panel = new Panel();
        using var stopped = new CancellationTokenSource();
        panel.Answer = request =>
        {
            // Cancelled while the status is being served, so the loop meets an already-cancelled
            // token the moment it reaches its wait.
            if (!request.RequestUri!.AbsolutePath.EndsWith("/logs")) stopped.Cancel();
            return request.RequestUri!.AbsolutePath.EndsWith("/logs")
                ? Json(HttpStatusCode.OK, "[]")
                : Json(HttpStatusCode.OK, """{"status":"Building"}""");
        };

        var clock = Stopwatch.StartNew();
        await DeployCommand.StreamLogs(Client(panel), "abc", stopped.Token);
        clock.Stop();

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1400));
    }

    [Fact]
    public async Task A_deployment_that_ends_still_reports_its_own_outcome()
    {
        // The token must not have changed what following logs is for.
        var panel = new Panel
        {
            Answer = request => request.RequestUri!.AbsolutePath.EndsWith("/logs")
                ? Json(HttpStatusCode.OK, """[{"seq":0,"stream":"System","message":"done"}]""")
                : Json(HttpStatusCode.OK, """{"status":"Succeeded"}""")
        };

        var (exit, _) = await RunAsync(() => DeployCommand.StreamLogs(Client(panel), "abc", default));

        exit.Should().Be(0);
    }

    private static string CliSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Harbora.Cli", file));
    }
}
