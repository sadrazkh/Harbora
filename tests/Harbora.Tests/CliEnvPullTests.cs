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
/// <c>harbora env pull</c> — the CLI half of 4.1 (2026-09-04 local-dev-parity plan). The merge itself
/// is proven identical to a deploy's at the shared assembly point
/// (<c>EffectiveEnvironmentBuilderParityTests</c>) and over HTTP (<c>ApiV1EnvHttpTests</c>); this
/// covers what is specific to the CLI command: it never overwrites an existing <c>.env.local</c>
/// without saying so, secrets are marked in the written file, and a server error is reported rather
/// than swallowed.
/// </summary>
public class CliEnvPullTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "harbora-env-pull-" + Guid.NewGuid().ToString("N"));

    public CliEnvPullTests() => Directory.CreateDirectory(_dir);
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

    private static ApiClient ClientWithEnv(string body) =>
        Client(_ => Json(HttpStatusCode.OK, body));

    private const string TwoVarsBody = """
        [
          { "key": "API_BASE", "value": "https://api.example.com", "isSecret": false, "source": "App" },
          { "key": "DB_PASSWORD", "value": "s3cret-value", "isSecret": true, "source": "Database: orders" }
        ]
        """;

    /// <summary>Mirrors CliCancelTests' own helper: the console redirected so what a person would have
    /// seen can be asserted, flattened because Spectre wraps to a terminal width.</summary>
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

    // ---- writing a fresh file ------------------------------------------------------------------

    [Fact]
    public async Task With_no_existing_file_it_writes_env_local_and_marks_the_secret()
    {
        var api = ClientWithEnv(TwoVarsBody);

        var (exit, output) = await RunAsync(() => EnvPullCommand.RunAsync(api, "blog", _dir, force: false, default));

        exit.Should().Be(0);
        output.Should().Contain("Wrote .env.local").And.Contain("2 variable").And.Contain("1 marked");

        var written = File.ReadAllText(Path.Combine(_dir, ".env.local"));
        written.Should().Contain("API_BASE=https://api.example.com");
        written.Should().Contain("# SECRET (from Database: orders)\nDB_PASSWORD=s3cret-value");
    }

    [Fact]
    public async Task The_written_file_never_contains_an_unmarked_secret()
    {
        var api = ClientWithEnv(TwoVarsBody);
        await EnvPullCommand.RunAsync(api, "blog", _dir, force: false, default);

        var lines = File.ReadAllLines(Path.Combine(_dir, ".env.local"));
        var secretLineIndex = Array.FindIndex(lines, l => l.StartsWith("DB_PASSWORD=", StringComparison.Ordinal));

        secretLineIndex.Should().BeGreaterThan(0);
        lines[secretLineIndex - 1].Should().Contain("SECRET",
            "a secret value written with nothing marking it as one defeats the entire point of this command");
    }

    // ---- refusing to overwrite silently --------------------------------------------------------

    [Fact]
    public async Task An_existing_file_that_would_change_is_not_overwritten_without_force()
    {
        var path = Path.Combine(_dir, ".env.local");
        File.WriteAllText(path, "API_BASE=https://old.example.com\n");
        var api = ClientWithEnv(TwoVarsBody);

        var (exit, output) = await RunAsync(() => EnvPullCommand.RunAsync(api, "blog", _dir, force: false, default));

        exit.Should().Be(1);
        File.ReadAllText(path).Should().Be("API_BASE=https://old.example.com\n",
            "the existing file must survive untouched until --force is given");
        output.Should().Contain("would change").And.Contain("--force");
    }

    [Fact]
    public async Task The_shown_diff_never_prints_the_secrets_actual_value()
    {
        var path = Path.Combine(_dir, ".env.local");
        File.WriteAllText(path, "DB_PASSWORD=totally-different-old-secret\n");
        var api = ClientWithEnv(TwoVarsBody);

        var (_, output) = await RunAsync(() => EnvPullCommand.RunAsync(api, "blog", _dir, force: false, default));

        output.Should().NotContain("totally-different-old-secret").And.NotContain("s3cret-value");
        output.Should().Contain("DB_PASSWORD");
    }

    [Fact]
    public async Task Force_replaces_an_existing_file()
    {
        var path = Path.Combine(_dir, ".env.local");
        File.WriteAllText(path, "API_BASE=https://old.example.com\n");
        var api = ClientWithEnv(TwoVarsBody);

        var (exit, _) = await RunAsync(() => EnvPullCommand.RunAsync(api, "blog", _dir, force: true, default));

        exit.Should().Be(0);
        File.ReadAllText(path).Should().Contain("API_BASE=https://api.example.com");
    }

    [Fact]
    public async Task An_existing_file_that_already_matches_is_reported_as_such_and_left_alone()
    {
        var api = ClientWithEnv("""[ { "key": "API_BASE", "value": "https://api.example.com", "isSecret": false, "source": "App" } ]""");
        var path = Path.Combine(_dir, ".env.local");
        // First pull, to get the exact bytes RunAsync itself would write.
        await EnvPullCommand.RunAsync(api, "blog", _dir, force: false, default);
        var before = File.GetLastWriteTimeUtc(path);

        var (exit, output) = await RunAsync(() => EnvPullCommand.RunAsync(api, "blog", _dir, force: false, default));

        exit.Should().Be(0);
        output.Should().Contain("already matches");
        File.GetLastWriteTimeUtc(path).Should().Be(before, "an unchanged pull must not touch the file at all");
    }

    // ---- server errors --------------------------------------------------------------------------

    [Fact]
    public async Task A_server_that_refuses_the_request_reports_its_own_message_and_writes_nothing()
    {
        var api = Client(_ => Json(HttpStatusCode.Forbidden, """{"error":"Your role cannot view this app's environment."}"""));

        var (exit, output) = await RunAsync(() => EnvPullCommand.RunAsync(api, "blog", _dir, force: false, default));

        exit.Should().Be(1);
        output.Should().Contain("Your role cannot view this app's environment.");
        File.Exists(Path.Combine(_dir, ".env.local")).Should().BeFalse();
    }

    [Fact]
    public async Task An_unreachable_server_also_exits_one()
    {
        var api = Client(_ => throw new HttpRequestException("Connection refused"));

        var (exit, output) = await RunAsync(() => EnvPullCommand.RunAsync(api, "blog", _dir, force: false, default));

        exit.Should().Be(1);
        output.Should().Contain("Connection refused");
    }

    // ---- wiring -----------------------------------------------------------------------------------

    [Fact]
    public void The_cli_registers_env_pull_as_a_branch()
    {
        CliSource("Program.cs").Should().Contain("AddBranch(\"env\"").And.Contain("AddCommand<EnvPullCommand>(\"pull\")");
    }

    [Fact]
    public void Env_pull_takes_a_force_flag()
    {
        typeof(EnvPullCommand.Settings).GetProperty(nameof(EnvPullCommand.Settings.Force))!
            .GetCustomAttribute<CommandOptionAttribute>()!.LongNames.Should().Contain("force");
    }

    private static string CliSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Harbora.Cli", file));
    }
}
