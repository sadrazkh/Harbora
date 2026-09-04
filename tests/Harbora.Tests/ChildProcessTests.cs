using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="ChildProcess"/> and <see cref="CommandLine"/> — the part of <c>harbora run</c> its own
/// task brief calls out directly: "a wrapper that swallows a non-zero exit is the defining defect
/// class of this codebase in miniature." These run a REAL child process rather than a fake, because a
/// fake exit code cannot prove anything about the thing that actually decides it — <see cref="System.Diagnostics.Process"/>
/// and, on Windows, <c>cmd.exe</c>'s own exit-code propagation through the <see cref="CommandLine.Resolve"/>
/// wrapper.
/// </summary>
public class ChildProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "harbora-run-" + Guid.NewGuid().ToString("N"));

    public ChildProcessTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    private static (string FileName, string[] Args) ExitWith(int code) => OperatingSystem.IsWindows()
        ? ("cmd.exe", ["/d", "/c", "exit", code.ToString()])
        : ("/bin/sh", ["-c", $"exit {code}"]);

    // ---- ChildProcess.RunAsync: the exit code, directly -----------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task The_childs_exit_code_passes_through_unchanged(int code)
    {
        var (fileName, args) = ExitWith(code);

        var exit = await ChildProcess.RunAsync(_dir, fileName, args, new Dictionary<string, string>());

        exit.Should().Be(code, "a wrapper that turns any exit code into 0 (or any other fixed number) " +
                                "is exactly the defect this command exists to not be");
    }

    [Fact]
    public async Task Extra_environment_variables_reach_the_child_process()
    {
        // A one-liner with redirection (`>`) is a whole little shell script, not a plain argv token —
        // exactly the shape RunRawAsync exists for on Windows (see its own doc: ArgumentList would
        // re-escape this and corrupt the redirection/quoting). RunAsync (ArgumentList) is exercised on
        // POSIX below, where the shell reads its script from -c as one ordinary argument.
        var marker = Path.Combine(_dir, "marker.txt");
        int exit;
        if (OperatingSystem.IsWindows())
        {
            exit = await ChildProcess.RunRawAsync(_dir, "cmd.exe", $"/d /c echo %HARBORA_TEST_VAR%>\"{marker}\"",
                new Dictionary<string, string> { ["HARBORA_TEST_VAR"] = "harbora-run-injected-value" });
        }
        else
        {
            exit = await ChildProcess.RunAsync(_dir, "/bin/sh", ["-c", $"echo \"$HARBORA_TEST_VAR\" > \"{marker}\""],
                new Dictionary<string, string> { ["HARBORA_TEST_VAR"] = "harbora-run-injected-value" });
        }

        exit.Should().Be(0);
        File.Exists(marker).Should().BeTrue();
        (await File.ReadAllTextAsync(marker)).Trim().Should().Be("harbora-run-injected-value");
    }

    [Fact]
    public async Task The_childs_own_environment_is_still_there_alongside_the_injected_variables()
    {
        // ProcessStartInfo.Environment starts as a copy of this process's own environment — proving
        // PATH survives is what proves an ordinary program (found via PATH) can still run at all.
        var marker = Path.Combine(_dir, "path-marker.txt");
        if (OperatingSystem.IsWindows())
        {
            await ChildProcess.RunRawAsync(_dir, "cmd.exe",
                $"/d /c if defined PATH (echo yes>\"{marker}\") else (echo no>\"{marker}\")",
                new Dictionary<string, string> { ["HARBORA_TEST_VAR"] = "x" });
        }
        else
        {
            await ChildProcess.RunAsync(_dir, "/bin/sh",
                ["-c", $"[ -n \"$PATH\" ] && echo yes > \"{marker}\" || echo no > \"{marker}\""],
                new Dictionary<string, string> { ["HARBORA_TEST_VAR"] = "x" });
        }

        (await File.ReadAllTextAsync(marker)).Trim().Should().Be("yes");
    }

    // ---- CommandLine.Resolve: how `run -- <argv>` becomes what ChildProcess starts ----------------

    [Fact]
    public void On_a_posix_shell_the_argv_is_used_directly_with_no_shell_involved()
    {
        if (OperatingSystem.IsWindows()) return; // this branch is exercised on Linux/macOS CI

        var resolved = CommandLine.Resolve(["npm", "start", "--", "--flag"]);

        resolved.FileName.Should().Be("npm");
        resolved.Arguments.Should().Equal("start", "--", "--flag");
        resolved.RawArguments.Should().BeNull();
    }

    [Fact]
    public void On_Windows_the_argv_is_wrapped_through_cmd_for_pathext_resolution()
    {
        if (!OperatingSystem.IsWindows()) return; // this branch is exercised on Windows CI

        var resolved = CommandLine.Resolve(["npm", "start"]);

        resolved.FileName.Should().EndWithEquivalentOf("cmd.exe", "COMSPEC's casing varies by machine");
        resolved.Arguments.Should().BeNull("Windows carries its command line as a raw string, never ArgumentList");
        // Neither token needs quoting, so neither gets any — reaching cmd.exe exactly as somebody
        // would have typed it, which is what carries the .cmd resolution CreateProcess alone cannot
        // do, and (see CommandLine's own doc) what avoids cmd.exe's /c quote-stripping entirely.
        resolved.RawArguments.Should().Be("/d /c npm start");
    }

    [Fact]
    public void On_Windows_an_embedded_quote_in_an_argument_is_doubled_not_escaped_with_a_backslash()
    {
        if (!OperatingSystem.IsWindows()) return;

        var resolved = CommandLine.Resolve(["node", "-e", "say \"hi\""]);

        // "node" and "-e" need no quoting; the third argument has a space and a quote, so only it is
        // quoted, with its own embedded quotes doubled.
        resolved.RawArguments.Should().Be("/d /c node -e \"say \"\"hi\"\"\"");
    }

    // ---- End to end: the exact path `harbora run` takes -------------------------------------------

    [Fact]
    public async Task Resolve_then_the_right_ChildProcess_method_still_pass_the_exit_code_through()
    {
        // The production path RunCommand actually calls: CommandLine.Resolve builds the OS-specific
        // command, then RunCommand picks RunAsync or RunRawAsync based on which field is set — exactly
        // what this reproduces, rather than calling either ChildProcess method directly and assuming
        // RunCommand wires them the same way.
        var (innerFile, innerArgs) = ExitWith(5);
        var argv = new List<string> { innerFile };
        argv.AddRange(innerArgs);

        var resolved = CommandLine.Resolve(argv);
        var exit = resolved.RawArguments is not null
            ? await ChildProcess.RunRawAsync(_dir, resolved.FileName, resolved.RawArguments, new Dictionary<string, string>())
            : await ChildProcess.RunAsync(_dir, resolved.FileName, resolved.Arguments!, new Dictionary<string, string>());

        exit.Should().Be(5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public async Task Every_step_of_the_real_run_command_path_passes_a_realistic_argv_exit_code_through(int code)
    {
        // The argv shape a user actually types — `harbora run -- cmd.exe /c exit N` on Windows, a
        // plain shell invocation on POSIX — resolved and run exactly the way RunCommand does, with no
        // pre-built "cmd.exe /d /c" scaffolding of the test's own to give the wrapper an easier time.
        var argv = ExitWithRealisticArgv(code);

        var resolved = CommandLine.Resolve(argv);
        var exit = resolved.RawArguments is not null
            ? await ChildProcess.RunRawAsync(_dir, resolved.FileName, resolved.RawArguments, new Dictionary<string, string>())
            : await ChildProcess.RunAsync(_dir, resolved.FileName, resolved.Arguments!, new Dictionary<string, string>());

        exit.Should().Be(code);
    }

    private static IReadOnlyList<string> ExitWithRealisticArgv(int code) => OperatingSystem.IsWindows()
        ? ["cmd.exe", "/c", "exit", code.ToString()]
        : ["/bin/sh", "-c", $"exit {code}"];
}
