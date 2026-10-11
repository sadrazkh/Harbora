using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Services;
using Harbora.Infrastructure.Templates;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>get_catalog</c> — what can be created: database engines with the versions an operator offers
/// (<see cref="ServiceCatalogReader"/>, the same list the create form draws), the templates this
/// workspace can see (<see cref="TemplateCatalog.IsVisibleTo"/>) and the instance sizes its plan allows.
/// </summary>
public sealed class GetCatalogTool(HarboraDbContext db, IManagedServiceEngine engine) : AgentReadTool
{
    public const int MaxTemplates = 60;

    public override string Name => "get_catalog";

    public override string Description =>
        "Lists what can be created: database engines and the versions offered for each, the app templates available to " +
        "this workspace, and the instance sizes (CPU, memory, disk). Use it before proposing a database version, a " +
        "template or a size, instead of guessing one.";

    public override string ParametersSchema =>
        """
        { "type": "object", "properties": {}, "additionalProperties": false }
        """;

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var engines = (await ServiceCatalogReader.EffectiveAsync(db, engine, ct))
            .Select(e => new { engine = AgentResourceResolver.Lower(e.Type), name = e.DisplayName, versions = e.Versions })
            .ToList();

        // Filtered in memory against the one visibility rule, as TemplatesController does — "who may see
        // this" decides whether one tenant's unreviewed template is offered to another.
        var visible = (await db.AppTemplates.AsNoTracking().OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct))
            .Where(t => TemplateCatalog.IsVisibleTo(t, context.WorkspaceId))
            .ToList();
        var templates = visible.Take(MaxTemplates)
            .Select(t => new { key = t.Key, name = t.Name, category = t.Category, description = Clip(t.Description, 140) })
            .ToList();

        // The sizes this workspace's plan allows — the same filter the panel's own size choosers apply
        // (TemplatesController.SizeChoicesAsync): a plan with no list allows every enabled size, and a
        // workspace with no plan is on the default one.
        var planId = await db.Workspaces.AsNoTracking()
            .Where(w => w.Id == context.WorkspaceId).Select(w => w.PlanId).FirstOrDefaultAsync(ct);
        var allowedKeys = await db.Plans.AsNoTracking()
            .Where(p => planId != null ? p.Id == planId : p.IsDefault)
            .Select(p => p.AllowedSizeKeys)
            .FirstOrDefaultAsync(ct);
        var allowed = string.IsNullOrWhiteSpace(allowedKeys)
            ? null
            : allowedKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sizes = (await db.InstanceSizes.AsNoTracking()
                .Where(s => s.IsEnabled)
                .OrderBy(s => s.SortOrder).ThenBy(s => s.CpuCores)
                .Select(s => new { s.Key, s.Name, s.CpuCores, s.MemoryBytes, s.DiskBytes })
                .ToListAsync(ct))
            .Where(s => allowed is null || allowed.Contains(s.Key))
            .ToList();

        var payload = new
        {
            engines,
            templates,
            templatesOmitted = Math.Max(0, visible.Count - MaxTemplates),
            instanceSizes = sizes.Select(s => new
            {
                key = s.Key,
                name = s.Name,
                cpuCores = s.CpuCores,
                memoryMb = s.MemoryBytes / (1024 * 1024),
                diskGb = s.DiskBytes == 0 ? (long?)null : s.DiskBytes / (1024L * 1024 * 1024)
            }).ToList()
        };

        return Found(payload, "Read the catalog", "کاتالوگ بررسی شد");
    }
}
