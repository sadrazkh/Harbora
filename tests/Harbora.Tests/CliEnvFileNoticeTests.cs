using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Cli;
using Spectre.Console;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What a push says about the env files it kept on the developer's machine. A Vite, Next.js or Create
/// React App build reads <c>.env.production</c>, so one that used to go and now does not changes what
/// is built: the person has to be told which, where the values go instead, and how to send one.
/// </summary>
[Collection(CliConsoleCollection.Name)]
public sealed class CliEnvFileNoticeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harbora-envnotice-" + Guid.NewGuid().ToString("N"));

    public CliEnvFileNoticeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private void Write(string relative, string content = "x") =>
        File.WriteAllText(Path.Combine(_root, relative), content);

    /// <summary>Mirrors the capture in <c>CliCancelTests</c>: only the words are compared, not where Spectre folds them.</summary>
    private static string Captured(Action print)
    {
        var previous = AnsiConsole.Console;
        var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer)
        });

        try { print(); return Regex.Replace(writer.ToString(), @"\s+", " ").Trim(); }
        finally { AnsiConsole.Console = previous; }
    }

    private async Task<string> PushedAsync()
    {
        var packed = await SourcePacker.PackAsync(_root);
        try { return Captured(() => EnvFileNotice.Print(packed.Excluded)); }
        finally { File.Delete(packed.ArchivePath); }
    }

    [Fact]
    public async Task A_push_names_each_env_file_it_kept_where_the_values_go_and_how_to_send_one()
    {
        Write("package.json", "{}");
        Write(".env", "SECRET=hunter2");
        Write(".env.production", "VITE_API_URL=https://api.example.com");

        var output = await PushedAsync();

        output.Should().Contain("Kept on this machine:").And.Contain(".env.production").And.Contain(".env,")
            .And.Contain("variables in the panel")
            .And.Contain("To upload .env.production anyway, add \"!.env.production\" to .dockerignore");
        output.Should().NotContain("hunter2").And.NotContain("api.example.com", "a value is never printed");
    }

    [Fact]
    public async Task Env_alone_says_where_the_values_go_and_offers_no_way_to_send_it()
    {
        Write("package.json", "{}");
        Write(".env", "SECRET=hunter2");

        var output = await PushedAsync();

        output.Should().Contain("Kept on this machine: .env —").And.NotContain("To upload");
    }

    [Fact]
    public async Task Nothing_kept_is_nothing_said()
    {
        Write("package.json", "{}");
        Write(".env.example", "SECRET=");
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        Write(Path.Combine("node_modules", ".env.production"));

        (await PushedAsync()).Should().BeEmpty();
    }

    [Fact]
    public void A_monorepo_full_of_them_is_counted_past_five()
    {
        EnvFileNotice.List(["a/.env", "b/.env", "c/.env", "d/.env", "e/.env", "f/.env", "g/.env"])
            .Should().Be("a/.env, b/.env, c/.env, d/.env, e/.env and 2 more");
    }

    [Fact]
    public void The_push_report_is_where_it_is_said_for_deploy_and_launch_alike()
    {
        // ReportExcluded is what both `harbora deploy` and `harbora launch` call after packing.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Harbora.Cli", "Commands.cs"));

        var report = source.IndexOf("internal static void ReportExcluded", StringComparison.Ordinal);
        report.Should().BeGreaterThan(-1);
        source.IndexOf("EnvFileNotice.Print(excluded);", report, StringComparison.Ordinal).Should().BeGreaterThan(report);
    }
}
