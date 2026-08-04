using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// One invocation of an engine binary.
///
/// <para>
/// <see cref="Arguments"/> is a LIST, and that is the whole point. Each element is handed to the
/// process as one argument, so a value containing <c>;</c>, <c>&amp;&amp;</c> or a quote is data.
/// There is no string form of this command anywhere, and therefore nothing for a shell to reinterpret.
/// </para>
/// <para>
/// <see cref="Environment"/> carries secrets — repository passwords, access keys. It never reaches a
/// log, and its values are registered with the redactor before the process starts.
/// </para>
/// </summary>
public sealed record EngineCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null);

/// <summary>
/// What an invocation produced. Output is already redacted and length-bounded — callers may log it.
/// </summary>
public sealed record EngineCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// <summary>Whichever stream carries the explanation, for an error message.</summary>
    public string Diagnostic =>
        !string.IsNullOrWhiteSpace(StandardError) ? StandardError.Trim() : StandardOutput.Trim();
}

/// <summary>
/// Starts engine processes. Behind an interface so engines can be driven in tests without a binary
/// installed, and so an HTTP-based implementation can replace process execution later without the
/// engine adapters changing.
/// </summary>
public interface IEngineProcessRunner
{
    Task<EngineCommandResult> RunAsync(EngineCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Runs an engine binary directly — never through a shell.
///
/// <para>
/// <c>UseShellExecute</c> is false and no interpreter is invoked, so there is no
/// <c>sh -c</c>/<c>cmd /c</c> anywhere on this path. That is the structural half of the
/// command-injection defence described in THREAT_MODEL T1; the allowlists in
/// <c>EngineArgumentGuard</c> are the other half.
/// </para>
/// </summary>
public sealed class EngineProcessRunner(
    EngineOutputRedactor redactor,
    ILogger<EngineProcessRunner> logger) : IEngineProcessRunner
{
    /// <summary>Matches <see cref="KopiaOptions.MaxCapturedOutputBytes"/>'s default.</summary>
    private const int DefaultOutputCap = 64 * 1024;

    public async Task<EngineCommandResult> RunAsync(EngineCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var info = new ProcessStartInfo
        {
            FileName = command.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,

            // No shell. With this false the arguments below are passed to the process verbatim and
            // nothing expands metacharacters on the way.
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // ArgumentList, never Arguments. Assigning a single joined string would put quoting back in
        // our hands and reintroduce exactly the class of bug this avoids.
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);

        if (command.WorkingDirectory is { } cwd) info.WorkingDirectory = cwd;

        if (command.Environment is { } env)
        {
            foreach (var (key, value) in env)
            {
                info.Environment[key] = value;
                // Registered BEFORE the process can emit anything, so a secret echoed back in an
                // error message is masked on its way out rather than after it has been logged.
                redactor.Register(value);
            }
        }

        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var cap = DefaultOutputCap;

        process.OutputDataReceived += (_, e) => Append(stdout, e.Data, cap);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data, cap);

        logger.LogDebug("Running {Executable} with {ArgumentCount} argument(s).",
            command.Executable, command.Arguments.Count);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // A missing binary is the common case and deserves a sentence an operator can act on,
            // rather than a Win32Exception surfacing through six layers.
            return new EngineCommandResult(
                -1, "", redactor.Redact($"'{command.Executable}' could not be started: {ex.Message}"), false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (command.Timeout is { } timeout) timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Killed rather than abandoned. An orphaned engine process holds a repository lock, and
            // the next attempt then fails for a reason that has nothing to do with the new request.
            TryKill(process);

            // A cancellation the CALLER asked for is a cancellation; only the timeout is a timeout.
            if (cancellationToken.IsCancellationRequested) throw;

            return new EngineCommandResult(
                -1, Finish(stdout), redactor.Redact($"The engine did not finish within {command.Timeout}."), true);
        }

        // WaitForExitAsync returns once the process ends, but the async output readers may still be
        // draining. Without this the last lines — usually the error message — are lost.
        process.WaitForExit();

        return new EngineCommandResult(process.ExitCode, Finish(stdout), Finish(stderr), false);

        string Finish(StringBuilder builder) => redactor.Redact(builder.ToString());
    }

    private static void Append(StringBuilder builder, string? line, int cap)
    {
        if (line is null) return;
        lock (builder)
        {
            if (builder.Length >= cap) return;
            builder.AppendLine(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or not ours to kill. Nothing useful left to do.
        }
    }
}
