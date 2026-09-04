using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Harbora.Cli;

/// <summary>
/// <c>harbora env pull</c> — writes an app's effective environment to <c>.env.local</c>, so "works on
/// my machine" and "works on Harbora" can finally be the same question (4.1, 2026-09-04
/// local-dev-parity plan).
///
/// <para>
/// "Effective" is deliberately not this command's decision: it asks the server for exactly what
/// <c>ConfigGroupMerge</c> computes — the app's own variables, its config groups, and everything its
/// attached services inject — over <see cref="ApiClient"/>, the same authenticated CLI session every
/// other command already uses. See <see cref="EffectiveEnv"/>'s own doc for why recomputing any part
/// of that merge here would be a second implementation the CLI would have to keep in step by hand.
/// </para>
/// </summary>
public sealed class EnvPullCommand : AsyncCommand<EnvPullCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[app]"), Description("App slug (defaults to ./harbora.yml)")]
        public string? App { get; init; }

        [CommandOption("--path <PATH>"), Description("Project folder to write .env.local into (default: current directory)")]
        public string? Path { get; init; }

        [CommandOption("--server <URL>"), Description("Panel URL — for CI, instead of `harbora login`")]
        public string? Server { get; init; }

        [CommandOption("--token <TOKEN>"), Description("API token — for CI, instead of `harbora login`")]
        public string? Token { get; init; }

        [CommandOption("--account <EMAIL>"), Description("Which signed-in account to use, when several are")]
        public string? Account { get; init; }

        [CommandOption("-f|--force"), Description("Replace an existing .env.local instead of just showing what would change")]
        public bool Force { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var dir = System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
        var config = ProjectConfig.Load(dir);
        var slug = settings.App ?? config.App ?? HarboraConfig.ReadProjectSlug();
        if (string.IsNullOrWhiteSpace(slug))
        {
            AnsiConsole.MarkupLine("[red]No app specified.[/] Pass one, or add [yellow]app:[/] to harbora.yml.");
            return 1;
        }

        var api = Session.Resolve(settings.Server, settings.Token, settings.Account, config.Server);
        if (api is null) return 1;

        return await RunAsync(api, slug, dir, settings.Force, ct);
    }

    /// <summary>
    /// The command over a client and a resolved app/folder the caller supplies — public for the same
    /// reason <see cref="CancelCommand.RunAsync"/> is: this is the part with behaviour in it, testable
    /// against a stand-in for the panel and a real temp directory without a terminal or a network.
    /// </summary>
    public static async Task<int> RunAsync(ApiClient api, string slug, string dir, bool force, CancellationToken ct)
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

        var path = System.IO.Path.Combine(dir, DotEnvFile.FileName);
        var rendered = DotEnvFile.Render(slug, api.Server, entries);

        if (File.Exists(path) && !force)
        {
            var existing = File.ReadAllText(path);
            var diff = DotEnvFile.Diff(existing, rendered);

            if (diff.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    $"[green]✓[/] {DotEnvFile.FileName} already matches {Markup.Escape(slug)}'s environment.");
                return 0;
            }

            // Never overwritten silently: the whole point of pulling into a file instead of printing
            // to the terminal is that a developer can trust what is already there — a deploy tool that
            // clobbers a local edit on every run teaches people to stop trusting it.
            AnsiConsole.MarkupLine(
                $"[yellow]![/] {DotEnvFile.FileName} already exists and would change:");
            foreach (var line in diff)
                AnsiConsole.MarkupLine("  " + Markup.Escape(line));
            AnsiConsole.MarkupLine("[grey]Run with[/] --force [grey]to replace it.[/]");
            return 1;
        }

        File.WriteAllText(path, rendered);
        var secretCount = entries.Count(e => e.IsSecret);
        AnsiConsole.MarkupLine(
            $"[green]✓[/] Wrote {DotEnvFile.FileName} — {entries.Count} variable(s), " +
            $"{secretCount} marked [bold]SECRET[/].");
        AnsiConsole.MarkupLine("[grey]Run[/] harbora doctor [grey]to check it is excluded from git.[/]");
        return 0;
    }
}
