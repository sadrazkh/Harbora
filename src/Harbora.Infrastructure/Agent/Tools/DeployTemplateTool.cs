using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>deploy_template</c> — materialises a catalog template through
/// <see cref="TemplateDeploymentService.DeployAsync"/>, the path the template page's form takes. A
/// template always becomes a <em>new</em> project with a Production environment — the app, the databases
/// it requires and their reference variables — and the card says so.
///
/// <para>
/// The service has no validate method (it predates the phase-0 extraction) and says no by throwing,
/// sometimes after it has staged rows. So validation here asks, read-only, what the service would refuse
/// first — the template is visible and parses, a repository when it builds from Git, every variable it
/// needs a value for and no variable it would silently ignore, a version that runs on this server, the
/// plan's room — and execution detaches whatever a refusal left staged before it reports the failure.
/// Access is the template page's (<c>TemplatesController.CanDeployAsync</c>: AppsCreate, plus
/// DatabasesManage when databases come with it), asked for a project that does not exist yet — the same
/// question <c>ProjectsController.Create</c> asks, which only an unscoped member passes.
/// </para>
///
/// <para>
/// The first deployment is queued by this tool, not by the service: the service would record it as a
/// manual deploy, and a refusal at queue time (quota, capacity, disk) would escape as an error after the
/// project had already been committed. Here it is recorded as the assistant's, and such a refusal is
/// reported as what it is — the project exists, its first deploy did not start.
/// </para>
/// </summary>
public sealed class DeployTemplateTool(
    HarboraDbContext db,
    AgentResourceResolver resolver,
    ProjectAccessService access,
    TemplateDeploymentService templates,
    IDeploymentEngine deployments,
    IQuotaService quota,
    IAuditLogger audit,
    IOptions<BillingOptions> billing) : AgentAppTool(resolver, access)
{
    private const int ShownValueLength = 60;

    public override string Name => "deploy_template";

    public override string Description =>
        "Deploys a catalog template (by its key or name from get_catalog) as a NEW project with a Production " +
        "environment: the app plus any databases the template requires, wired together, and by default its first " +
        "deployment. It cannot add to an existing project. Pass every variable the template needs a value for in " +
        "'variables'; templates that build from Git need a public repository URL. Nothing is created until the user " +
        "approves.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "template": { "type": "string", "description": "The template's key or name, as get_catalog lists it." },
            "name": { "type": "string", "description": "Name for the app (or database) it creates. Default: the template's name." },
            "project": { "type": "string", "description": "Name for the new project. Default: the template's name." },
            "repository": { "type": "string", "description": "Public https Git clone URL, only for templates that build from Git." },
            "gitRef": { "type": "string", "description": "Branch or tag, for templates that build from Git. Default main." },
            "variables": { "type": "object", "additionalProperties": { "type": "string" }, "description": "Values for the variables the template needs, by key." },
            "size": { "type": "string", "description": "An instance size key from get_catalog, applied to everything it creates. Optional." },
            "deployNow": { "type": "boolean", "description": "Queue the app's first deployment. Default true." }
          },
          "required": ["template"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.SafeWrite;

    public override string? Capability => Capabilities.AppsCreate;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var call = await ReadAsync(args, context, ct);
        if (call.Error is { } error) return AgentValidation.Refused(error.En, error.Fa);

        var template = call.Template!;
        var manifest = call.Manifest!;
        var databases = call.Databases;
        var projectSlug = call.ProjectSlug;
        var resource = call.AppSlug ?? call.ResourceName;

        var whatEn = manifest.Service is not null
            ? $"database {call.ResourceName} ({databases[^1]})"
            : $"app {resource} from {(call.Repository is not null ? $"Git {call.Repository}" : $"image {manifest.Image}")}";
        var whatFa = manifest.Service is not null
            ? $"دیتابیس {call.ResourceName} ({databases[^1]})"
            : $"اپ {resource} از {(call.Repository is not null ? $"Git {call.Repository}" : $"ایمیج {manifest.Image}")}";

        var detailEn = new List<string> { $"New project '{call.ProjectName}' ({projectSlug}/{ProjectService.DefaultEnvironmentSlug}) with {whatEn}" };
        var detailFa = new List<string> { $"پروژهٔ تازهٔ «{call.ProjectName}» ({projectSlug}/{ProjectService.DefaultEnvironmentSlug}) با {whatFa}" };
        if (manifest.Service is null && databases.Count > 0)
        {
            detailEn.Add($"plus {databases.Count} database(s) it requires: {string.Join(", ", databases)}");
            detailFa.Add($"به‌همراه {databases.Count} دیتابیسی که لازم دارد: {string.Join("، ", databases)}");
        }
        if (call.Size is { } size) { detailEn.Add($"size {size} for everything it creates"); detailFa.Add($"اندازهٔ {size} برای همه"); }
        foreach (var (key, value) in call.Variables)
        {
            var secret = call.Secrets.Contains(key);
            detailEn.Add(secret ? $"{key} (secret — value hidden)" : $"{key} = {Shown(value)}");
            detailFa.Add(secret ? $"{key} (محرمانه — مقدار نمایش داده نمی‌شود)" : $"{key} = {Shown(value)}");
        }
        if (manifest.Service is null)
        {
            detailEn.Add(call.DeployNow ? "first deployment queued after the databases" : "not deployed yet");
            detailFa.Add(call.DeployNow ? "استقرار اول پس از دیتابیس‌ها در صف قرار می‌گیرد" : "فعلاً مستقر نمی‌شود");
        }

        var warningsEn = new List<string> { "A template always creates a new project; it cannot be added to an existing one." };
        var warningsFa = new List<string> { "قالب همیشه پروژهٔ تازه‌ای می‌سازد و نمی‌شود آن را به پروژهٔ موجود اضافه کرد." };
        if (await MayChargeAsync(db, billing, context.WorkspaceId, ct))
        {
            warningsEn.Add(FirstHourWarning.En);
            warningsFa.Add(FirstHourWarning.Fa);
        }

        return new AgentValidation(true,
            TitleEn: $"Deploy template {template.Name} as {call.ResourceName}",
            TitleFa: $"استقرار قالب {(string.IsNullOrWhiteSpace(template.NameFa) ? template.Name : template.NameFa)} با نام {call.ResourceName}",
            DetailEn: string.Join(" · ", detailEn),
            DetailFa: string.Join(" · ", detailFa),
            WarningsEn: warningsEn,
            WarningsFa: warningsFa,
            Creates: manifest.Service is null
                ? new AgentPlannedResource(AgentResourceKinds.App, resource, projectSlug, ProjectService.DefaultEnvironmentSlug)
                : new AgentPlannedResource(AgentResourceKinds.Database, call.ResourceName, projectSlug, ProjectService.DefaultEnvironmentSlug));
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var call = await ReadAsync(args, context, ct);
        if (call.Error is { } error) return NotPossible(error.En, error.Fa);
        var template = call.Template!;

        // The service stages the project, its environment and the databases before some of its refusals;
        // whatever it left behind when it throws is this step's to drop, not the agent's next save's to write.
        var before = AgentTracker.ForeignChanges(db);
        TemplateDeployResult deployed;
        try
        {
            deployed = await templates.DeployAsync(new TemplateDeployRequest(
                context.WorkspaceId, context.UserId, template.Id, call.ProjectName, call.ResourceName,
                call.Repository, call.GitRef, call.Variables, DeployNow: false, InstanceSizeKey: call.Size), ct);
        }
        catch (InvalidOperationException ex)
        {
            DropStaged(db, before);
            var fa = ex switch
            {
                QuotaRefusedException quotaRefused => quotaRefused.ReasonFa ?? ex.Message,
                CreationPaymentRequiredException payment => payment.ReasonFa,
                _ => $"قالب مستقر نشد: {ex.Message}"
            };
            return NotPossible(ex.Message, fa);
        }
        catch
        {
            // Not a refusal (a save that lost a race on a unique name, say): the plan engine reports it, but
            // the graph it staged goes first, in an order that cannot sever a required relationship.
            DropStaged(db, before);
            throw;
        }

        await audit.LogAsync("template.deploy", "template", template.Id.ToString(),
            metadataJson: JsonSerializer.Serialize(new { projectId = deployed.ProjectId, dependencies = deployed.DependencyCount }),
            workspaceId: context.WorkspaceId, ct: ct);

        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == deployed.ProjectId, ct);
        var databases = await db.ManagedServices.AsNoTracking()
            .Where(s => s.Environment!.ProjectId == project.Id).OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name }).ToListAsync(ct);
        var app = deployed.AppId is { } appId
            ? await db.Apps.AsNoTracking().Where(a => a.Id == appId).Select(a => new { a.Id, a.Slug, a.GitRef }).FirstAsync(ct)
            : null;

        var links = new List<AgentResourceLink> { ProjectLink(project.Id, project.Slug) };
        if (app is not null) links.Add(AppLink(app.Id, app.Slug));
        links.AddRange(databases.Select(d => new AgentResourceLink("database", d.Id, d.Name, $"/databases/{d.Id}")));

        QueuedDeployment? deployment = null;
        (string En, string Fa)? deployRefused = null;
        if (app is not null && call.DeployNow)
        {
            try
            {
                var deploymentId = await deployments.QueueDeploymentAsync(
                    new DeploymentRequest(app.Id, DeploymentTrigger.Assistant, context.UserId, app.GitRef), ct);
                deployment = await ReadDeploymentAsync(db, deploymentId, ct);
                if (deployment is not null) links.Add(DeploymentLink(deployment.Id, deployment.Number));
            }
            catch (Exception ex) when (DeployRefusal(ex) is { } refusal)
            {
                deployRefused = refusal;
            }
        }
        var state = deployment is null ? ((string En, string Fa, string Code)?)null : Describe(deployment);

        var summaryEn = $"Created project '{project.Slug}' from template {template.Name}";
        var summaryFa = $"پروژهٔ «{project.Slug}» از قالب {(string.IsNullOrWhiteSpace(template.NameFa) ? template.Name : template.NameFa)}";
        if (app is not null) { summaryEn += $", with app '{app.Slug}'"; summaryFa += $" با اپ «{app.Slug}»"; }
        summaryFa += " ساخته شد";
        if (databases.Count > 0)
        {
            // Queued, like everything long a tool starts: provisioning runs in the job queue.
            summaryEn += $"; database(s) {string.Join(", ", databases.Select(d => d.Name))} are queued to be provisioned";
            summaryFa += $"؛ دیتابیس‌های {string.Join("، ", databases.Select(d => d.Name))} در صف آماده‌سازی‌اند";
        }
        summaryEn += ".";
        summaryFa += ".";
        if (state is { } s) { summaryEn += $" Its first {s.En}."; summaryFa += $" اولین {s.Fa}."; }
        else if (deployRefused is { } refused) { summaryEn += $" Its first deployment did not start: {refused.En}"; summaryFa += $" استقرار اول شروع نشد: {refused.Fa}"; }
        else if (app is not null) { summaryEn += " The app is not deployed yet."; summaryFa += " اپ هنوز مستقر نشده است."; }

        return new AgentToolResult(true,
            AgentJson.Serialize(new
            {
                created = "project",
                project = project.Slug,
                template = template.Key,
                app = app?.Slug,
                databases = databases.Select(d => d.Name),
                deployment = deployment is null ? null : new { number = deployment.Number, status = state!.Value.Code },
                deployRefused = deployRefused?.En
            }),
            summaryEn, summaryFa, Links: links);
    }

    // ---- reading the call ----

    private sealed record Call(
        AppTemplate? Template, TemplateManifest? Manifest, string ProjectName, string ProjectSlug, string ResourceName, string? AppSlug,
        string? Repository, string? GitRef, IReadOnlyDictionary<string, string> Variables, IReadOnlySet<string> Secrets,
        IReadOnlyList<string> Databases, string? Size, bool DeployNow, (string En, string Fa)? Error);

    private static Call No(string en, string fa) =>
        new(null, null, "", "", "", null, null, null, new Dictionary<string, string>(), new HashSet<string>(), [], null, false, (en, fa));

    private async Task<Call> ReadAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var wanted = AgentArgs.String(args, "template");
        if (wanted is null) return No("Say which template (its key from get_catalog).", "بگویید کدام قالب (کلید آن از get_catalog).");

        // The one visibility rule the catalogue, the template page and get_catalog all apply.
        var visible = (await db.AppTemplates.AsNoTracking().ToListAsync(ct))
            .Where(t => TemplateCatalog.IsVisibleTo(t, context.WorkspaceId)).ToList();
        var template = visible.FirstOrDefault(t => string.Equals(t.Key, wanted, StringComparison.OrdinalIgnoreCase))
                       ?? visible.SingleOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (template is null)
            return No($"There is no template '{wanted}' available to this workspace. Use get_catalog to see the templates.",
                $"قالبی به نام «{wanted}» برای این فضای کاری نیست. فهرست قالب‌ها را با get_catalog ببینید.");

        if (!TemplateManifest.TryParse(template.ManifestJson, out var manifest, out _))
            return No($"The template '{template.Name}' cannot be used: its manifest does not parse.",
                $"قالب «{template.Name}» قابل استفاده نیست: مانیفست آن خوانده نمی‌شود.");

        var createsDatabases = manifest!.Service is not null || manifest.Requires.Count > 0;
        var outside = new ResourcePlacement(null, null);
        if (manifest.Service is null && !await Access.AllowsAsync(outside, Capabilities.AppsCreate, ct))
            return No($"Your role does not allow creating a new project: it needs the '{Capabilities.AppsCreate}' permission across the workspace.",
                $"نقش شما اجازهٔ ساختن پروژهٔ تازه را نمی‌دهد: مجوز «{Capabilities.AppsCreate}» در کل فضای کاری لازم است.");
        if (createsDatabases && !await Access.AllowsAsync(outside, Capabilities.DatabasesManage, ct))
            return No($"This template creates databases, and your role does not allow that: it needs the '{Capabilities.DatabasesManage}' permission across the workspace.",
                $"این قالب دیتابیس می‌سازد و نقش شما اجازهٔ آن را نمی‌دهد: مجوز «{Capabilities.DatabasesManage}» در کل فضای کاری لازم است.");

        // In the service's own order: what it requires, then the database a database template is.
        var databases = new List<string>();
        var services = manifest.Requires.ToList();
        if (manifest.Service is { Length: > 0 } single) services.Add(single);
        foreach (var service in services)
        {
            try { databases.Add(AgentResourceResolver.Lower(TemplateDeploymentService.ParseServiceType(service))); }
            catch (InvalidOperationException ex)
            {
                return No(ex.Message, $"این قالب سرویسی می‌خواهد که پشتیبانی نمی‌شود: {service}");
            }
        }

        var repository = AgentArgs.String(args, "repository");
        var fromGit = manifest.Source?.Equals("git", StringComparison.OrdinalIgnoreCase) == true;
        if (fromGit && repository is null)
            return No($"Template '{template.Name}' builds from Git: give a public repository URL.",
                $"قالب «{template.Name}» از Git ساخته می‌شود: آدرس یک مخزن عمومی بدهید.");
        if (!fromGit && repository is not null)
            return No($"Template '{template.Name}' does not build from a repository; leave repository out.",
                $"قالب «{template.Name}» از مخزن ساخته نمی‌شود؛ repository را ندهید.");
        if (repository is not null && CheckRepositoryUrl(repository) is { } badUrl) return No(badUrl.En, badUrl.Fa);

        // The service reads a value only for a variable that needs one; anything else given here would be
        // silently dropped, and the model would believe it had been set.
        var plan = TemplateSetup.Prepare(manifest, () => "");
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.TryGetProperty("variables", out var variables) && variables.ValueKind == JsonValueKind.Object)
            foreach (var property in variables.EnumerateObject())
                given[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.GetRawText();
        var needed = plan.Variables.Where(v => v.NeedsAValue).ToList();
        foreach (var key in given.Keys)
        {
            var declared = plan.Variables.FirstOrDefault(v => v.Key == key);
            if (declared is null)
                return No($"Template '{template.Name}' has no variable {key}. Its variables: {Keys(plan.Variables)}.",
                    $"قالب «{template.Name}» متغیری به نام {key} ندارد. متغیرهایش: {Keys(plan.Variables)}.");
            if (!declared.NeedsAValue)
                return No($"{key} is set by the template itself (a default or a generated secret); change it after deploying with set_env_vars.",
                    $"{key} را خود قالب تنظیم می‌کند (مقدار پیش‌فرض یا رمز ساخته‌شده)؛ پس از استقرار با set_env_vars آن را تغییر دهید.");
        }
        var missing = needed.Where(v => !given.TryGetValue(v.Key, out var value) || string.IsNullOrWhiteSpace(value)).Select(v => v.Key).ToList();
        if (missing.Count > 0)
            return No($"Template '{template.Name}' needs values for: {string.Join(", ", missing)}. Ask the user for them.",
                $"قالب «{template.Name}» برای این متغیرها مقدار لازم دارد: {string.Join("، ", missing)}. آن‌ها را از کاربر بپرسید.");

        // A value may refer to the databases the template brings (${{mysql.host}}); one naming anything
        // else is refused by the service after it has staged the project, so it is refused here first.
        var aliases = manifest.Service is not null && services.Count == 1 ? new[] { manifest.Service } : manifest.Requires.ToArray();
        var known = aliases
            .SelectMany(alias => TemplateReferences.For(alias, new Services.ServiceCreds("host", 1, "user", "password", "database")))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in given)
        {
            TemplateReferences.Resolve(value, known, out var unknown);
            if (unknown.Count > 0)
                return No($"{key} refers to an unknown service value: {string.Join(", ", unknown)}.",
                    $"{key} به مقداری از سرویسی اشاره می‌کند که در این قالب نیست: {string.Join("، ", unknown)}.");
        }

        var server = await db.Servers.AsNoTracking().Where(s => s.IsLocal).Select(s => new { s.Architecture }).FirstOrDefaultAsync(ct);
        if (server is null) return No("No local server is configured.", "هیچ سرور محلی‌ای پیکربندی نشده است.");
        var versions = await db.AppTemplateVersions.AsNoTracking().Where(v => v.AppTemplateId == template.Id).ToListAsync(ct);
        if (versions.Count > 0 && VersionSelection.Default(versions, server.Architecture) is null)
            return No("No version of this template can be deployed on this server yet.",
                "هنوز هیچ نسخه‌ای از این قالب روی این سرور قابل استقرار نیست.");

        var size = AgentArgs.String(args, "size");
        if (size is not null)
        {
            var allowed = await AllowedSizeKeysAsync(db, context.WorkspaceId, ct);
            size = allowed.FirstOrDefault(k => string.Equals(k, size, StringComparison.OrdinalIgnoreCase));
            if (size is null)
                return No($"That size is not one this workspace can use. Sizes: {(allowed.Count == 0 ? "none" : string.Join(", ", allowed))}.",
                    $"این اندازه برای این فضای کاری نیست. اندازه‌ها: {(allowed.Count == 0 ? "هیچ" : string.Join("، ", allowed))}.");
        }

        // The plan's room, asked the way the service and ProjectService ask it — read-only here.
        var room = await quota.CanAddGovernedResourcesAsync(context.WorkspaceId, new GovernanceQuotaDelta(Projects: 1, Environments: 1), ct);
        if (room.Allowed && manifest.Service is null) room = await quota.CanAddAppAsync(context.WorkspaceId, null, null, ct);
        if (room.Allowed && databases.Count > 0) room = await quota.CanAddServiceAsync(context.WorkspaceId, size, ct);
        if (!room.Allowed)
            return No(room.Reason ?? "Plan quota exceeded.", room.ReasonFa ?? room.Reason ?? "سقف پلن پر شده است.");

        var resourceName = AgentArgs.String(args, "name") ?? template.Name;
        var projectName = AgentArgs.String(args, "project") ?? template.Name;

        // The slugs the service will derive — ProjectService's rule for both — refused when taken rather
        // than left to become "ghost-2": the card, and any later step of the plan that names them, were
        // promised these. (The service would quietly suffix; the agent says so instead.)
        var projectSlug = ProjectService.Slugify(projectName);
        if (projectSlug.Length == 0)
            return No($"'{projectName}' gives no usable project slug. Pass a project name in Latin letters.",
                $"از «{projectName}» نامک قابل استفاده‌ای برای پروژه درنمی‌آید. نام پروژه را با حروف لاتین بدهید.");
        if (await db.Projects.AnyAsync(p => p.WorkspaceId == context.WorkspaceId && p.Slug == projectSlug, ct))
            return No($"A project '{projectSlug}' already exists. A template always creates a new project: pass another project name.",
                $"پروژهٔ «{projectSlug}» از قبل هست. قالب همیشه پروژهٔ تازه می‌سازد: نام دیگری برای پروژه بدهید.");

        var appSlug = manifest.Service is null ? ProjectService.Slugify(resourceName) : null;
        if (appSlug is { Length: 0 })
            return No($"'{resourceName}' gives no usable app slug. Pass a name in Latin letters.",
                $"از «{resourceName}» نامک قابل استفاده‌ای برای اپ درنمی‌آید. نامی با حروف لاتین بدهید.");
        if (appSlug is not null && await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Slug == appSlug, ct))
            return No($"'{appSlug}' is already taken on this platform — app names are shared across every workspace. Pass another name.",
                $"«{appSlug}» قبلاً در پلتفرم استفاده شده است — نام اپ در کل پلتفرم مشترک است. نام دیگری بدهید.");

        return new Call(template, manifest, projectName, projectSlug, resourceName, appSlug,
            repository, AgentArgs.String(args, "gitRef"), given,
            plan.Variables.Where(v => v.Secret).Select(v => v.Key).ToHashSet(StringComparer.Ordinal),
            databases, size, AgentArgs.Bool(args, "deployNow") ?? true, null);
    }

    private static string Keys(IEnumerable<PreparedVariable> variables)
    {
        var keys = variables.Select(v => v.Key).ToList();
        return keys.Count == 0 ? "none" : string.Join(", ", keys);
    }

    private static string Shown(string value) =>
        value.Length > ShownValueLength ? value[..ShownValueLength] + "…" : value;
}
