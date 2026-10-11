using System.Text.Json;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary><c>list_resources</c> — the whole tree this person can see, past the snapshot's cap.</summary>
public sealed class ListResourcesTool(AgentResourceResolver resolver) : AgentReadTool
{
    /// <summary>Generous, because this is the call the model makes precisely when the snapshot was cut short.</summary>
    public const int MaxResources = 200;

    public override string Name => "list_resources";

    public override string Description =>
        "Lists the projects, environments, apps and databases the user can see, with each app's status and kind and each " +
        "database's engine, version and status. Use it when the workspace snapshot says resources were left out, or to " +
        "narrow down to one project.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "project": { "type": "string", "description": "Only this project (its slug or name). Omit for everything." }
          },
          "additionalProperties": false
        }
        """;

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var project = AgentArgs.String(args, "project");
        var tree = await resolver.TreeAsync(context, MaxResources, project, ct);

        if (project is not null && tree.Projects.Count == 0)
            return NotFound($"No project named '{project}' that you can see.",
                $"پروژه‌ای به نام «{project}» که به آن دسترسی داشته باشید پیدا نشد.");

        var apps = tree.Projects.Sum(p => p.Environments.Sum(e => e.Apps.Count));
        var databases = tree.Projects.Sum(p => p.Environments.Sum(e => e.Databases.Count));

        return Found(new { projects = tree.Projects, omitted = tree.Omitted },
            $"Listed {apps} apps and {databases} databases",
            $"فهرست {apps} اپ و {databases} دیتابیس");
    }
}
