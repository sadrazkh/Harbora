using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Harbora.Cli;

/// <summary>
/// <c>harbora run -- &lt;command&gt;</c> — runs a local process with an app's effective environment
/// injected, so a developer can run their app locally with the same environment the platform gives it
/// (4.1, 2026-09-04 local-dev-parity plan). The other half of local-dev parity, alongside
/// <see cref="EnvPullCommand"/>: this one never touches disk at all, for a one-off command that
/// should not leave a stale <c>.env.local</c> behind.
///
/// <para>
/// Faithfulness is the entire point. <see cref="ChildProcess"/> never redirects the child's
/// stdout/stderr, so output reaches the terminal exactly as a bare invocation would, and this command
/// returns the child's own exit code unchanged — the task brief that describes this feature names "a
/// wrapper that swallows a non-zero exit" as this codebase's defining defect class, and a `run` that
/// always exited 0 would be exactly that.
/// </para>
/// </summary>
public sealed class RunCommand : AsyncCommand<RunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-a|--app <SLUG>"), Description("App slug (defaults to ./harbora.yml)")]
        public string? App { get; init; }

        [CommandOption("--path <PATH>"), Description("Project/working folder (default: current directory)")]
        public string? Path { get; init; }

        [CommandOption("--server <URL>"), Description("Panel URL — for CI, instead of `harbora login`")]
        public string? Server { get; init; }

        [CommandOption("--token <TOKEN>"), Description("API token — for CI, instead of `harbora login`")]
        public string? Token { get; init; }

        [CommandOption("--account <EMAIL>"), Description("Which signed-in account to use, when several are")]
        public string? Account { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        // Everything after `--` is the command to run, untouched by Spectre's own option parsing — a
        // child command's own `-y` or `--force` must reach IT, never be read as harbora's.
        var argv = context.Remaining.Raw.ToList();
        if (argv.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[red]No command given.[/] Usage: [yellow]harbora run -- <command> [args...][/]");
            return 1;
        }

        var dir = System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
        var config = ProjectConfig.Load(dir);
        var slug = settings.App ?? config.App ?? HarboraConfig.ReadProjectSlug();
        if (string.IsNullOrWhiteSpace(slug))
        {
            AnsiConsole.MarkupLine("[red]No app specified.[/] Pass [yellow]--app[/], or add [yellow]app:[/] to harbora.yml.");
            return 1;
        }

        var api = Session.Resolve(settings.Server, settings.Token, settings.Account, config.Server);
        if (api is null) return 1;

        return await RunAsync(api, slug, dir, argv, ct);
    }

    /// <summary>
    /// The command over a client, a resolved app/folder, and the child argv the caller supplies —
    /// public for the same reason <see cref="CancelCommand.RunAsync"/> is: this is the part with
    /// behaviour in it, testable against a stand-in for the panel and a real child process without a
    /// terminal or a network.
    /// </summary>
    public static async Task<int> RunAsync(
        ApiClient api, string slug, string dir, IReadOnlyList<string> argv, CancellationToken ct)
    {
        IReadOnlyList<EffectiveEnvEntry> entries;
        try
        {
            entries = await EffectiveEnv.FetchAsync(api, slug, ct);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[red]Could not fetch the environment for {Markup.Escape(slug)}:[/] " +
                $"{Markup.Escape(ServerError.Message(ex.Message))}");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[grey]Running with {entries.Count} variable(s) from[/] {Markup.Escape(slug)} " +
            $"[grey]on {Markup.Escape(api.Server)} —[/] {Markup.Escape(string.Join(' ', argv))}");

        var env = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var resolved = CommandLine.Resolve(argv);

        try
        {
            return resolved.RawArguments is not null
                ? await ChildProcess.RunRawAsync(dir, resolved.FileName, resolved.RawArguments, env, ct)
                : await ChildProcess.RunAsync(dir, resolved.FileName, resolved.Arguments!, env, ct);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Named by the command that failed to start, not a stack trace — `npm: command not found`
            // is the sentence someone can act on; a Win32Exception's own message is not.
            AnsiConsole.MarkupLine($"[red]Could not run '{Markup.Escape(argv[0])}':[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
