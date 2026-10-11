using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>list_resources</c> — the whole tree this person can see, past the snapshot's cap, in pages.
///
/// <para>
/// Paged rather than capped, because a tool result is cut to <see cref="AgentJson.MaxToolResultLength"/>
/// characters on its way to the model and the cut keeps the head: a long list would arrive as its first
/// fifty entries and silently lose the rest — exactly the resources the snapshot left out and told the
/// model to come here for. Each page is packed to fit under the cut, with the paging facts first, so
/// "there is more, ask for page 2" can never be the part that is cut off.
/// </para>
/// </summary>
public sealed class ListResourcesTool(AgentResourceResolver resolver) : AgentReadTool
{
    /// <summary>The most one page may serialise to — under the cut, with room for redaction to lengthen it.</summary>
    public const int PageBudget = AgentJson.MaxToolResultLength - 400;

    public override string Name => "list_resources";

    public override string Description =>
        "Lists the projects, environments, apps and databases the user can see, with each app's status and kind and each " +
        "database's engine, version and status. The answer comes in pages: when nextPage is set, call again with that " +
        "page to see the rest. Use it when the workspace snapshot says resources were left out, or to narrow down to one project.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "project": { "type": "string", "description": "Only this project (its slug or name). Omit for everything." },
            "page": { "type": "integer", "description": "Which page, from 1. Omit for the first." }
          },
          "additionalProperties": false
        }
        """;

    /// <summary>One line of the listing: a resource, or an environment (or project) that has none.</summary>
    private sealed record Item(AgentProjectNode Project, AgentEnvironmentNode? Environment, string? Kind, string? Text);

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var project = AgentArgs.String(args, "project");
        var page = Math.Max(1, AgentArgs.Int(args, "page") ?? 1);
        var tree = await resolver.TreeAsync(context, int.MaxValue, project, ct);

        if (project is not null && tree.Projects.Count == 0)
            return NotFound($"No project named '{project}' that you can see.",
                $"پروژه‌ای به نام «{project}» که به آن دسترسی داشته باشید پیدا نشد.");

        var items = Flatten(tree);
        var totalApps = items.Count(i => i.Kind == "app");
        var totalDatabases = items.Count(i => i.Kind == "database");
        var pages = Paginate(items, totalApps, totalDatabases);

        if (page > pages.Count)
            return NotFound($"There are only {pages.Count} pages.", $"فقط {pages.Count} صفحه هست.");

        var shown = pages[page - 1];
        var payload = Page(shown, page, pages.Count, totalApps, totalDatabases);
        var apps = shown.Count(i => i.Kind == "app");
        var databases = shown.Count(i => i.Kind == "database");

        return new AgentToolResult(true, payload.ToJsonString(AgentJson.Options),
            $"Listed page {page} of {pages.Count}: {apps} apps and {databases} databases",
            $"صفحهٔ {page} از {pages.Count}: {apps} اپ و {databases} دیتابیس");
    }

    private static List<Item> Flatten(AgentResourceTree tree)
    {
        var items = new List<Item>();
        foreach (var project in tree.Projects)
        {
            if (project.Environments.Count == 0) items.Add(new Item(project, null, null, null));
            foreach (var environment in project.Environments)
            {
                if (environment.Apps.Count == 0 && environment.Databases.Count == 0)
                    items.Add(new Item(project, environment, null, null));
                foreach (var app in environment.Apps)
                    items.Add(new Item(project, environment, "app", $"{app.Slug} ({app.Status}, {app.Kind})"));
                foreach (var database in environment.Databases)
                    items.Add(new Item(project, environment, "database", $"{database.Name} ({database.Engine} {database.Version}, {database.Status})"));
            }
        }
        return items;
    }

    /// <summary>Greedy packing: each page takes items until one more would push it over the budget.
    /// Measured with the largest page numbers it could carry, so the real page is never bigger.</summary>
    private static List<List<Item>> Paginate(List<Item> items, int totalApps, int totalDatabases)
    {
        var pages = new List<List<Item>>();
        var current = new List<Item>();
        foreach (var item in items)
        {
            current.Add(item);
            if (current.Count > 1 && Page(current, 9999, 9999, totalApps, totalDatabases).ToJsonString(AgentJson.Options).Length > PageBudget)
            {
                current.RemoveAt(current.Count - 1);
                pages.Add(current);
                current = [item];
            }
        }
        if (current.Count > 0 || pages.Count == 0) pages.Add(current);
        return pages;
    }

    /// <summary>One page, the paging facts first.</summary>
    private static JsonObject Page(List<Item> items, int page, int pages, int totalApps, int totalDatabases)
    {
        var projects = new JsonArray();
        foreach (var byProject in items.GroupBy(i => i.Project))
        {
            var environments = new JsonArray();
            foreach (var byEnvironment in byProject.Where(i => i.Environment is not null).GroupBy(i => i.Environment!))
            {
                var environment = new JsonObject { ["environment"] = byEnvironment.Key.Slug };
                if (byEnvironment.Key.Protected) environment["protected"] = true;
                var apps = byEnvironment.Where(i => i.Kind == "app").Select(i => (JsonNode?)i.Text).ToArray();
                var databases = byEnvironment.Where(i => i.Kind == "database").Select(i => (JsonNode?)i.Text).ToArray();
                if (apps.Length > 0) environment["apps"] = new JsonArray(apps);
                if (databases.Length > 0) environment["databases"] = new JsonArray(databases);
                environments.Add(environment);
            }

            projects.Add(new JsonObject
            {
                ["project"] = byProject.Key.Slug,
                ["name"] = byProject.Key.Name,
                ["environments"] = environments
            });
        }

        return new JsonObject
        {
            ["page"] = page,
            ["pages"] = pages,
            ["nextPage"] = page < pages ? page + 1 : null,
            ["totalApps"] = totalApps,
            ["totalDatabases"] = totalDatabases,
            ["projects"] = projects
        };
    }
}
