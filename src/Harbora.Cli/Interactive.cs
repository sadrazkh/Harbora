using System.Text.Json;
using Spectre.Console;

namespace Harbora.Cli;

/// <summary>An app as the server describes it, which is the only place the truth lives.</summary>
public sealed record RemoteApp(string Slug, string Name, string Status, string Source, bool CanServerPull)
{
    public override string ToString() => Slug;
}

/// <summary>
/// The parts of a deploy that can be answered by asking, instead of by making the user look things up.
///
/// `harbora deploy` used to fail with "App not found." when the folder had no config, which told the
/// user nothing about which apps exist or what to type. Everything needed to answer that is one
/// request away, so the CLI asks the server and offers a list.
///
/// Every prompt here is skipped when there is no terminal to prompt on: in CI these paths must fail
/// with an explanation rather than block forever waiting for input nobody can give.
/// </summary>
public static class Interactive
{
    /// <summary>Whether a person is there to answer. False under CI, pipes, and redirected input.</summary>
    public static bool IsAvailable =>
        !Console.IsInputRedirected && AnsiConsole.Profile.Capabilities.Interactive;

    public static async Task<IReadOnlyList<RemoteApp>> ListAppsAsync(ApiClient api)
    {
        var payload = await api.GetAsync("apps");
        var apps = new List<RemoteApp>();
        foreach (var a in payload.EnumerateArray())
        {
            apps.Add(new RemoteApp(
                a.GetProperty("slug").GetString() ?? "",
                a.GetProperty("name").GetString() ?? "",
                Text(a, "status"),
                Text(a, "source"),
                // Older servers do not send this. Assuming "cannot pull" makes the CLI upload the
                // folder, which works for every app type — the opposite assumption deploys nothing.
                a.TryGetProperty("canServerPull", out var pull) && pull.ValueKind == JsonValueKind.True));
        }
        return apps;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    /// <summary>Which account to act as, when more than one is signed in.</summary>
    public static Profile? ChooseAccount(HarboraConfig config, string? requested)
    {
        if (config.Profiles.Count == 0) return null;

        var named = config.Resolve(requested);
        if (!string.IsNullOrWhiteSpace(requested) || config.Profiles.Count == 1) return named;

        // Several accounts and nobody said which. Ask, or fall back to the current one rather than
        // hanging a pipeline on a question.
        if (!IsAvailable) return named;

        var chosen = AnsiConsole.Prompt(
            new SelectionPrompt<Profile>()
                .Title("Which account?")
                .UseConverter(p => $"{p.Name} [grey]({p.Server})[/]")
                .AddChoices(config.Profiles));

        config.Current = HarboraConfig.Key(chosen);
        config.Save();
        return chosen;
    }

    /// <summary>Which app to deploy, when the folder and the command line did not say.</summary>
    public static RemoteApp? ChooseApp(IReadOnlyList<RemoteApp> apps)
    {
        if (apps.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]![/] This account has no apps yet. Create one in the panel first.");
            return null;
        }
        if (apps.Count == 1) return apps[0];

        return AnsiConsole.Prompt(
            new SelectionPrompt<RemoteApp>()
                .Title("Which app do you want to deploy?")
                .PageSize(15)
                .UseConverter(a => $"{a.Slug} [grey]({a.Name} · {a.Status})[/]")
                .AddChoices(apps));
    }

    /// <summary>
    /// Writes the smallest useful <c>harbora.yml</c> so the next deploy in this folder needs no
    /// answers at all. Never overwrites: a project that already has a config has already decided.
    /// </summary>
    public static bool RememberApp(string dir, string slug, string? server)
    {
        if (ProjectConfig.Locate(dir) is not null) return false;

        var path = Path.Combine(dir, ProjectConfig.DefaultFileName);
        var body =
            $"""
            # Written by `harbora deploy`. Full schema: docs/cli-deploy.md
            app: {slug}
            {(string.IsNullOrWhiteSpace(server) ? "# server: https://panel.example.com" : $"server: {server}")}

            """;
        try
        {
            File.WriteAllText(path, body);
            return true;
        }
        catch (Exception ex)
        {
            // Not being able to save the answer is not a reason to fail a deploy that worked.
            AnsiConsole.MarkupLine($"[grey]Could not write {ProjectConfig.DefaultFileName}: {Markup.Escape(ex.Message)}[/]");
            return false;
        }
    }
}
