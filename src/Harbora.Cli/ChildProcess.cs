using System.Diagnostics;

namespace Harbora.Cli;

/// <summary>
/// Runs a child process with extra environment variables layered on top of the current one, and
/// returns its exit code unmodified — the one behaviour <c>harbora run</c> exists to guarantee. Its
/// own task brief names the failure mode directly: "a wrapper that swallows a non-zero exit is the
/// defining defect class of this codebase in miniature."
///
/// <para>
/// Deliberately does not redirect stdout/stderr/stdin. With <c>UseShellExecute = false</c> and no
/// redirection, .NET inherits this process's own console handles for the child, so a colored progress
/// bar, an interactive prompt, or a raw stderr line reach the terminal exactly as a bare invocation of
/// the same command would — with no buffering or copy loop of this CLI's own sitting between them and
/// a chance to mangle or drop one.
/// </para>
///
/// <para>
/// Two entry points, not one, because Windows and POSIX need incompatible ways of telling
/// <see cref="ProcessStartInfo"/> what the arguments are — see <see cref="RunRawAsync"/>'s own doc for
/// why mixing them silently corrupts the command line.
/// </para>
/// </summary>
public static class ChildProcess
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> via
    /// <see cref="ProcessStartInfo.ArgumentList"/> — each token passed through untouched, ordinary
    /// argv semantics. What <see cref="CommandLine.Resolve"/> uses on a POSIX shell, where the tokens
    /// Spectre already split on whitespace already ARE argv.
    /// </summary>
    public static Task<int> RunAsync(
        string workingDirectory, string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> extraEnv, CancellationToken ct = default)
    {
        var psi = NewStartInfo(workingDirectory, fileName, extraEnv);
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        return StartAndWaitAsync(psi, ct);
    }

    /// <summary>
    /// Runs <paramref name="fileName"/> with a pre-built, already-escaped raw argument STRING — what
    /// <see cref="CommandLine.Resolve"/> uses on Windows, where the arguments are cmd.exe's own quoted
    /// command line rather than a plain argv list.
    ///
    /// <para>
    /// Deliberately NOT <see cref="ProcessStartInfo.ArgumentList"/>: .NET re-escapes every
    /// <c>ArgumentList</c> entry using the Win32/CommandLineToArgvW convention (backslash-escaped
    /// quotes) before handing it to <c>CreateProcess</c>. A string already escaped for cmd.exe's own,
    /// different and incompatible convention (doubled quotes) would be escaped a SECOND time — this
    /// was a real bug here: every <c>harbora run</c> on Windows silently exited <c>1</c> regardless of
    /// the child's actual exit code, because the doubly-escaped command line cmd.exe received no
    /// longer parsed as the command it was supposed to be. <see cref="ProcessStartInfo.Arguments"/>
    /// (the raw string property) is used as-is by .NET, with no re-escaping, which is what this needs.
    /// </para>
    /// </summary>
    public static Task<int> RunRawAsync(
        string workingDirectory, string fileName, string rawArguments,
        IReadOnlyDictionary<string, string> extraEnv, CancellationToken ct = default)
    {
        var psi = NewStartInfo(workingDirectory, fileName, extraEnv);
        psi.Arguments = rawArguments;
        return StartAndWaitAsync(psi, ct);
    }

    private static ProcessStartInfo NewStartInfo(
        string workingDirectory, string fileName, IReadOnlyDictionary<string, string> extraEnv)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        // Layered on top of this process's own environment (which ProcessStartInfo.Environment
        // already starts as a copy of) so PATH, HOME and everything else the child needs to be an
        // ordinary program still reaches it — only the effective env's own keys are ever overridden.
        foreach (var (key, value) in extraEnv) psi.Environment[key] = value;
        return psi;
    }

    private static async Task<int> StartAndWaitAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start '{psi.FileName}'.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}

/// <summary>
/// Turns <c>harbora run</c>'s <c>-- &lt;command&gt; [args...]</c> into what <see cref="ChildProcess"/>
/// actually starts.
///
/// <para>
/// On a POSIX shell the tokens Spectre already split on whitespace are exactly argv — no shell is
/// needed or wanted, and running one would just be a second, redundant round of quoting rules to get
/// wrong. Windows is different: `npm`, `yarn`, `tsc` and most Node tooling are <c>.cmd</c>/<c>.bat</c>
/// files, which <c>CreateProcess</c> (what .NET calls with <c>UseShellExecute = false</c>) cannot
/// launch directly — only a real <c>.exe</c> — so <c>cmd.exe /c</c> is the one place a shell is
/// actually required, purely for that file-type resolution, not for parsing.
/// </para>
///
/// <para>
/// Only a token that actually needs quoting gets any — deliberately, not just for readability.
/// <c>cmd.exe /c</c> has its own, separate quote-stripping pass over whatever follows it (undocumented
/// well enough to be its own small folklore: exactly two quote characters wrapping an executable name
/// is treated one way, anything else has its first and last quote characters silently stripped before
/// the rest is parsed at all). Quoting <em>every</em> token — including a plain <c>npm</c> that never
/// needed it — makes the remainder start with a quote character and walks straight into that
/// stripping, corrupting the command line before cmd.exe ever reaches the real argument parser: this
/// was a real, shipped bug here, where every <c>harbora run</c> on Windows silently exited <c>1</c>
/// regardless of the child's actual exit code. Quoting only what needs it means an ordinary command
/// (<c>npm start</c>, <c>node app.js</c>) reaches cmd.exe exactly as a person would have typed it,
/// never touching that stripping pass at all.
/// </para>
///
/// <para>
/// Known, accepted limitation: characters cmd.exe treats as special even inside a quoted string —
/// chiefly <c>%</c> for environment-variable expansion — are not escaped by <see cref="Resolve"/>,
/// because cmd.exe has no general-purpose escape for them. A command argument that happens to contain
/// a literal <c>%</c> can be misread by cmd.exe itself.
/// </para>
/// </summary>
public static class CommandLine
{
    /// <param name="FileName">What to start.</param>
    /// <param name="Arguments">Set when <see cref="ChildProcess.RunAsync"/> should be used (POSIX) —
    /// null on Windows.</param>
    /// <param name="RawArguments">Set when <see cref="ChildProcess.RunRawAsync"/> should be used
    /// (Windows) — null on POSIX. Never both: <see cref="ChildProcess"/>'s own doc explains why mixing
    /// the two escaping conventions corrupts the command line.</param>
    public readonly record struct Resolved(string FileName, IReadOnlyList<string>? Arguments, string? RawArguments);

    public static Resolved Resolve(IReadOnlyList<string> argv)
    {
        if (!OperatingSystem.IsWindows())
            return new Resolved(argv[0], argv.Skip(1).ToList(), null);

        var comspec = Environment.GetEnvironmentVariable("COMSPEC");
        var shell = string.IsNullOrWhiteSpace(comspec) ? "cmd.exe" : comspec;
        return new Resolved(shell, null, "/d /c " + Windows(argv));
    }

    /// <summary>
    /// Joins argv into one cmd.exe command line, quoting a token only when it actually needs it (empty,
    /// or containing whitespace or a character cmd.exe would otherwise read as shell syntax) — see the
    /// class doc for why quoting an ordinary token that needs none of that would corrupt the line via
    /// cmd.exe's own <c>/c</c> quote-stripping. A quoted token doubles its own embedded quotes, the
    /// escaping cmd.exe expects once inside one.
    /// </summary>
    private static string Windows(IReadOnlyList<string> argv) =>
        string.Join(' ', argv.Select(Quote));

    private static readonly char[] NeedsQuoting = [' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')'];

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(NeedsQuoting) < 0) return arg;
        return "\"" + arg.Replace("\"", "\"\"") + "\"";
    }
}
