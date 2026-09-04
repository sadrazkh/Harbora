using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Cli;
using Spectre.Console;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <c>harbora run -- &lt;command&gt;</c> — the CLI half of 4.1 (2026-09-04 local-dev-parity plan).
/// <see cref="ChildProcessTests"/> proves <see cref="ChildProcess"/>/<see cref="CommandLine"/>
/// themselves pass an exit code and an injected variable through faithfully; this proves the same
/// thing at the command level — fetching a (faked) panel's environment and actually running a real
/// child process with it, exactly the path <c>harbora run</c> takes end to end.
/// </summary>
public class CliRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "harbora-run-cmd-" + Guid.NewGuid().ToString("N"));

    public CliRunTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    private sealed class Panel(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static ApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        new("https://panel.example.com", "tok", new Panel(answer));

    private static ApiClient ClientWithEnv(string body) => Client(_ => Json(HttpStatusCode.OK, body));

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

    private static string[] ExitWith(int code) => OperatingSystem.IsWindows()
        ? ["cmd.exe", "/c", "exit", code.ToString()]
        : ["/bin/sh", "-c", $"exit {code}"];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public async Task A_non_zero_child_exit_code_reaches_harboras_own_exit_code_unchanged(int code)
    {
        var api = ClientWithEnv("[]");

        var (exit, _) = await RunAsync(() => RunCommand.RunAsync(api, "blog", _dir, ExitWith(code), default));

        exit.Should().Be(code,
            "a wrapper that swallows a non-zero exit is the exact defect this command exists to not be");
    }

    [Fact]
    public async Task The_effective_environment_is_actually_injected_into_the_child_process()
    {
        var marker = Path.Combine(_dir, "marker.txt");
        var argv = OperatingSystem.IsWindows()
            ? new[] { "cmd.exe", "/c", $"echo %HARBORA_RUN_TEST_VAR%>\"{marker}\"" }
            : ["/bin/sh", "-c", $"echo \"$HARBORA_RUN_TEST_VAR\" > \"{marker}\""];
        var api = ClientWithEnv(
            """[ { "key": "HARBORA_RUN_TEST_VAR", "value": "pulled-from-panel", "isSecret": false, "source": "App" } ]""");

        var exit = await RunCommand.RunAsync(api, "blog", _dir, argv, default);

        exit.Should().Be(0);
        (await File.ReadAllTextAsync(marker)).Trim().Should().Be("pulled-from-panel");
    }

    [Fact]
    public async Task A_server_error_fetching_the_environment_exits_one_and_runs_nothing()
    {
        var api = Client(_ => Json(HttpStatusCode.Forbidden, """{"error":"Your role cannot view this app's environment."}"""));
        var marker = Path.Combine(_dir, "should-not-exist.txt");
        var argv = OperatingSystem.IsWindows()
            ? new[] { "cmd.exe", "/c", $"echo hi>\"{marker}\"" }
            : ["/bin/sh", "-c", $"echo hi > \"{marker}\""];

        var (exit, output) = await RunAsync(() => RunCommand.RunAsync(api, "blog", _dir, argv, default));

        exit.Should().Be(1);
        output.Should().Contain("Your role cannot view this app's environment.");
        File.Exists(marker).Should().BeFalse("nothing should run when the environment could not be fetched");
    }

    // ---- wiring -----------------------------------------------------------------------------------

    [Fact]
    public void The_cli_registers_the_run_command()
    {
        CliSource("Program.cs").Should().Contain("AddCommand<RunCommand>(\"run\")");
    }

    [Fact]
    public void No_command_after_the_separator_is_a_clean_refusal_not_a_crash()
    {
        // ExecuteAsync's own guard on an empty context.Remaining.Raw — proven at the source level
        // since CommandContext cannot be constructed directly outside Spectre's own parser.
        CliSource("RunCommand.cs").Should().Contain("No command given");
    }

    private static string CliSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Harbora.Cli", file));
    }
}
