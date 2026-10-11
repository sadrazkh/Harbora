using System.Text.Json;
using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProjectEnvironment = Harbora.Domain.Projects.Environment;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>create_app</c> — an app from a prebuilt image or a public Git repository, through
/// <see cref="IAppProvisioningService"/>: the very path the app form takes, so quota, placement access,
/// capacity, the address and the first-hour charge are decided by the same code.
///
/// <para>
/// Three things differ from the form, all on purpose. The slug is always sent explicitly (derived from
/// the name when the model gave none), so a taken name is refused rather than quietly becoming "web-2" —
/// the card promised "web", and a later step of the same plan names it as "web". The environment is
/// resolved here by name and passed as an id the person can see, because the service falls back to the
/// default environment for an id it does not recognise. And no Git token is accepted in any shape:
/// private repositories are the form's job in v1.
/// </para>
/// </summary>
public sealed partial class CreateAppTool(
    HarboraDbContext db,
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IAppProvisioningService provisioning,
    IOptions<BillingOptions> billing) : AgentAppTool(resolver, access)
{
    /// <summary>Argument names a model might put a credential under. Any of them present refuses the call
    /// outright, before anything else is read.</summary>
    private static readonly string[] CredentialArguments = ["gitToken", "token", "accessToken", "password", "credentials", "credential"];

    public override string Name => "create_app";

    public override string Description =>
        "Creates an app in a project's environment from either a prebuilt container image (for example nginx:alpine) or " +
        "a PUBLIC Git repository that Harbora builds itself, and by default queues its first deployment. Private " +
        "repositories are not supported by the assistant: never ask for, accept or pass a Git token, password or a URL " +
        "with credentials in it — tell the user to create such an app from the panel's app form. The slug is derived " +
        "from the name unless given (lowercase Latin letters, digits, hyphens) and must be free platform-wide. Use " +
        "get_catalog for instance sizes. Nothing is created until the user approves the plan.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The app's display name." },
            "slug": { "type": "string", "description": "Optional. Lowercase Latin letters, digits and hyphens; derived from the name when omitted." },
            "image": { "type": "string", "description": "A prebuilt image reference, e.g. nginx:alpine. Give this OR repository." },
            "repository": { "type": "string", "description": "A public https Git clone URL. Give this OR image." },
            "gitRef": { "type": "string", "description": "Branch or tag to build (Git only). Default main." },
            "rootDirectory": { "type": "string", "description": "Sub-folder of the repository to build from (Git only). Default: the repository root." },
            "dockerfilePath": { "type": "string", "description": "Path of the Dockerfile inside the build folder (Git only). Default Dockerfile." },
            "port": { "type": "integer", "description": "The port the app listens on inside its container. Default 80." },
            "project": { "type": "string", "description": "The project's slug. Required when the user can see more than one project." },
            "environment": { "type": "string", "description": "The environment's slug. Default: the project's default environment." },
            "size": { "type": "string", "description": "An instance size key from get_catalog. Optional." },
            "deployNow": { "type": "boolean", "description": "Queue the first deployment right after creating it. Default true." }
          },
          "required": ["name"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.SafeWrite;

    public override string? Capability => Capabilities.AppsCreate;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var form = await ReadAsync(args, context, ct);
        if (form.Error is { } error) return AgentValidation.Refused(error.En, error.Fa);
        var request = form.Request!;

        if (scope.Plans(AgentResourceKinds.App, request.Slug!))
            return AgentValidation.Refused(
                $"An earlier step of this plan already creates app '{request.Slug}'.",
                $"مرحلهٔ قبلی همین برنامه اپ «{request.Slug}» را می‌سازد.");

        var place = await PlaceAsync(args, context, scope, ct);
        if (place.Error is { } placeError) return AgentValidation.Refused(placeError.En, placeError.Fa);

        if (!await MayCreateAsync(place, ct)) return NotAllowed(place);

        var refusals = await provisioning.ValidateCreateAsync(request with { EnvironmentId = place.Environment?.Id }, ct);

        // An environment still to be created by an earlier step cannot be passed as an id, so the service
        // judged placement against the workspace's default one. That answer is about the wrong place;
        // placement was asked above, of the right one, and everything else the service checked — the
        // slug, the source, quota, capacity, the address — does not depend on which environment it is.
        if (place.WillExist)
            refusals = refusals.Where(r => r.Field != nameof(AppCreateRequest.EnvironmentId)).ToList();
        if (refusals.Count > 0) return Refused(refusals);

        var detailEn = new List<string> { form.SourceEn, $"port {request.ContainerPort}", $"in {place.Name}" };
        var detailFa = new List<string> { form.SourceFa, $"پورت {request.ContainerPort}", $"در {place.Name}" };
        if (!string.Equals(request.Name, request.Slug, StringComparison.Ordinal))
        {
            detailEn.Insert(0, $"name '{request.Name}'");
            detailFa.Insert(0, $"نام «{request.Name}»");
        }
        if (request.InstanceSizeKey is { } size) { detailEn.Add($"size {size}"); detailFa.Add($"اندازهٔ {size}"); }
        detailEn.Add(request.DeployNow ? "first deployment queued right after" : "not deployed yet");
        detailFa.Add(request.DeployNow ? "استقرار اول بلافاصله پس از ساخت در صف قرار می‌گیرد" : "فعلاً مستقر نمی‌شود");

        var warningsEn = new List<string>();
        var warningsFa = new List<string>();
        if (place.WillExist)
        {
            warningsEn.Add($"{place.Name} is created by an earlier step of this plan; the app is placed there when this step runs.");
            warningsFa.Add($"{place.Name} در مرحلهٔ قبلی همین برنامه ساخته می‌شود؛ اپ هنگام اجرای این مرحله آن‌جا قرار می‌گیرد.");
        }
        if (await MayChargeAsync(db, billing, context.WorkspaceId, ct))
        {
            warningsEn.Add(FirstHourWarning.En);
            warningsFa.Add(FirstHourWarning.Fa);
        }
        if (request.DeployNow && place.Environment is { IsProtected: true } guarded)
        {
            var (en, fa) = ProtectedEnvironmentWarning(guarded.Slug);
            warningsEn.Add(en);
            warningsFa.Add(fa);
        }

        return new AgentValidation(true,
            TitleEn: $"Create app {request.Slug}",
            TitleFa: $"ساخت اپ {request.Slug}",
            DetailEn: string.Join(" · ", detailEn),
            DetailFa: string.Join(" · ", detailFa),
            WarningsEn: warningsEn,
            WarningsFa: warningsFa,
            Creates: new AgentPlannedResource(AgentResourceKinds.App, request.Slug!, place.ProjectSlug, place.EnvironmentSlug));
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var form = await ReadAsync(args, context, ct);
        if (form.Error is { } error) return NotPossible(error.En, error.Fa);

        // No scope: whatever an earlier step was going to create exists by now, or this step cannot run.
        var place = await PlaceAsync(args, context, scope: null, ct);
        if (place.Error is { } placeError) return NotPossible(placeError.En, placeError.Fa);
        if (!await MayCreateAsync(place, ct))
        {
            var refused = NotAllowed(place);
            return NotPossible(refused.Error!, refused.ErrorFa!);
        }

        var request = form.Request! with { EnvironmentId = place.Environment?.Id };
        var before = AgentTracker.ForeignChanges(db);
        ServiceOutcome<AppCreateResult> outcome;
        try
        {
            outcome = await provisioning.CreateAsync(request, ct);
        }
        catch
        {
            // A refusal leaves nothing behind (the service attaches the app only after its checks), but an
            // exception — the unique slug or host lost to another workspace in the same instant, which the
            // service documents as surfacing this way — leaves the app's graph added. It goes first, leaves
            // before the rows they hang off, so the plan engine's own clean-up has nothing left to trip on.
            DropStaged(db, before);
            throw;
        }
        if (!outcome.Ok) return Failed(outcome.Refusals);
        var created = outcome.Value!;

        var app = await db.Apps.AsNoTracking()
            .Include(a => a.Environment).ThenInclude(e => e!.Project)
            .FirstAsync(a => a.Id == created.AppId, ct);
        var landed = AgentResourceResolver.Place(app);
        var links = new List<AgentResourceLink> { AppLink(app) };

        QueuedDeployment? deployment = created.DeploymentId is { } deploymentId ? await ReadDeploymentAsync(db, deploymentId, ct) : null;
        if (deployment is not null) links.Add(DeploymentLink(deployment.Id, deployment.Number));
        var deploymentState = deployment is null ? ((string En, string Fa, string Code)?)null : Describe(deployment);

        var result = AgentJson.Serialize(new
        {
            created = "app",
            app = app.Slug,
            name = app.Name,
            project = app.Environment?.Project?.Slug,
            environment = app.Environment?.Slug,
            source = form.SourceCode,
            host = created.Host,
            deployment = deployment is null ? null : new { number = deployment.Number, status = deploymentState!.Value.Code },
            deployRefused = created.DeployRefusal?.Message,
            notice = created.AddressNotice?.Message
        });

        // The service falls back to the workspace's default environment for an id it does not own. The id
        // was resolved a moment ago, so this is only reachable if the environment went away in between —
        // and then the app exists somewhere the person did not approve. Said as a failure, with the link.
        if (place.Environment is { } asked && app.EnvironmentId != asked.Id)
            return new AgentToolResult(false, result, null, null,
                $"App '{app.Slug}' was created, but in {landed} instead of {place.Name}, which no longer exists. Move or delete it from the panel.",
                $"اپ «{app.Slug}» ساخته شد، اما به‌جای {place.Name} که دیگر وجود ندارد در {landed} قرار گرفت. آن را از پنل جابه‌جا یا حذف کنید.",
                links);

        var summaryEn = $"Created app '{app.Slug}' in {landed}.";
        var summaryFa = $"اپ «{app.Slug}» در {landed} ساخته شد.";
        if (deploymentState is { } state)
        {
            summaryEn += $" Its first {state.En}.";
            summaryFa += $" اولین {state.Fa}.";
        }
        else if (created.DeployRefusal is { } refusal)
        {
            summaryEn += $" Its first deployment did not start: {refusal.Message}";
            summaryFa += $" استقرار اول آن شروع نشد: {refusal.MessageFa}";
        }
        else if (created.DeploymentId is not null)
        {
            summaryEn += " Its first deployment was queued.";
            summaryFa += " اولین استقرار آن در صف قرار گرفت.";
        }
        else
        {
            summaryEn += " It is not deployed yet.";
            summaryFa += " هنوز مستقر نشده است.";
        }

        if (created.Host is { } host)
        {
            summaryEn += $" Address: {host}.";
            summaryFa += $" آدرس: {host}.";
        }
        if (created.AddressNotice is { } notice)
        {
            summaryEn += " " + notice.Message;
            summaryFa += " " + notice.MessageFa;
        }

        return new AgentToolResult(true, result, summaryEn, summaryFa, Links: links);
    }

    // ---- reading the call ----

    private sealed record Form(
        AppCreateRequest? Request, string SourceCode, string SourceEn, string SourceFa, (string En, string Fa)? Error);

    private static Form Refuse(string en, string fa) => new(null, "", "", "", (en, fa));

    /// <summary>Everything the call says about the app itself, checked the way only the agent needs it
    /// checked (no credentials, one source, a Latin slug, a size the plan offers); the rest is the
    /// service's to judge.</summary>
    private async Task<Form> ReadAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        if (args.ValueKind == JsonValueKind.Object
            && CredentialArguments.Any(name => args.TryGetProperty(name, out _)))
            return Refuse(
                "The assistant does not take Git tokens or passwords: it works only with public repositories. Create an app from a private repository in the panel's app form.",
                "دستیار توکن یا گذرواژهٔ Git نمی‌گیرد و فقط با مخزن‌های عمومی کار می‌کند. اپِ مخزن خصوصی را از فرم ساخت اپ در پنل بسازید.");

        var name = AgentArgs.String(args, "name");
        if (name is null) return Refuse("Give the app a name.", "برای اپ یک نام بدهید.");

        var image = AgentArgs.String(args, "image");
        var repository = AgentArgs.String(args, "repository");
        if (image is not null && repository is not null)
            return Refuse("Give either an image or a repository, not both.", "یا ایمیج بدهید یا مخزن، نه هر دو.");
        if (image is null && repository is null)
            return Refuse("Give an image (a prebuilt container, e.g. nginx:alpine) or a public Git repository URL.",
                "یک ایمیج (مثلاً nginx:alpine) یا آدرس یک مخزن عمومی Git بدهید.");
        if (repository is not null && CheckRepositoryUrl(repository) is { } badUrl)
            return Refuse(badUrl.En, badUrl.Fa);

        var slug = Slugify(AgentArgs.String(args, "slug") ?? name);
        if (slug.Length == 0)
            return Refuse(
                $"'{name}' gives no usable slug. Pass a slug in lowercase Latin letters, digits and hyphens (a Persian name transliterated, for example).",
                $"از «{name}» نامک قابل استفاده‌ای درنمی‌آید. نامکی با حروف کوچک لاتین، رقم و خط تیره بدهید (مثلاً نام فارسی را با حروف لاتین بنویسید).");

        var port = AgentArgs.Int(args, "port") ?? 80;
        if (port is < 1 or > 65535)
            return Refuse("The port must be between 1 and 65535.", "پورت باید بین ۱ و ۶۵۵۳۵ باشد.");

        var size = AgentArgs.String(args, "size");
        if (size is not null)
        {
            var allowed = await AllowedSizeKeysAsync(db, context.WorkspaceId, ct);
            var match = allowed.FirstOrDefault(k => string.Equals(k, size, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return Refuse(
                    $"There is no size '{size}' this workspace can use. Sizes: {(allowed.Count == 0 ? "none" : string.Join(", ", allowed))}.",
                    $"اندازه‌ای به نام «{size}» برای این فضای کاری نیست. اندازه‌ها: {(allowed.Count == 0 ? "هیچ" : string.Join("، ", allowed))}.");
            size = match;
        }

        var gitRef = AgentArgs.String(args, "gitRef") ?? "main";
        var request = new AppCreateRequest
        {
            WorkspaceId = context.WorkspaceId,
            UserId = context.UserId,
            Name = name,
            Slug = slug,
            SourceType = image is not null ? AppSourceType.PrebuiltImage : AppSourceType.GitRepository,
            PrebuiltImage = image,
            CloneUrl = repository,
            GitRef = gitRef,
            RootDirectory = repository is not null ? AgentArgs.String(args, "rootDirectory") : null,
            DockerfilePath = AgentArgs.String(args, "dockerfilePath") ?? "Dockerfile",
            ContainerPort = port,
            InstanceSizeKey = size,
            DeployNow = AgentArgs.Bool(args, "deployNow") ?? true,
            Trigger = DeploymentTrigger.Assistant
        };

        return image is not null
            ? new Form(request, "image", $"image {image}", $"ایمیج {image}", null)
            : new Form(request, "git",
                $"Git {repository} (ref {gitRef}{(request.RootDirectory is { } dir ? $", folder {dir}" : "")})",
                $"Git {repository} (شاخهٔ {gitRef}{(request.RootDirectory is { } dirFa ? $"، پوشهٔ {dirFa}" : "")})",
                null);
    }

    /// <summary>The slug rule DNS needs, the same one projects use: lowercase Latin letters, digits and
    /// single hyphens. The app service's own rule would let Persian letters through into a container name.</summary>
    internal static string Slugify(string value)
    {
        var slug = NonSlug().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length > 50 ? slug[..50].Trim('-') : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    // ---- where it goes ----

    /// <param name="Environment">The environment, when it exists.</param>
    /// <param name="WillExist">An earlier step of this plan creates the project or the environment.</param>
    /// <param name="ProjectId">The project, when it exists (it may while its environment is planned).</param>
    private sealed record Place(
        ProjectEnvironment? Environment, string ProjectSlug, string EnvironmentSlug, bool WillExist, Guid? ProjectId,
        (string En, string Fa)? Error)
    {
        public string Name => $"{ProjectSlug}/{EnvironmentSlug}";

        public static Place No(string en, string fa) => new(null, "", "", false, null, (en, fa));
    }

    private async Task<Place> PlaceAsync(JsonElement args, AgentToolContext context, AgentPlanScope? scope, CancellationToken ct)
    {
        var project = AgentArgs.String(args, "project");
        var environment = AgentArgs.String(args, "environment");

        if (project is null)
        {
            if (environment is not null)
                return Place.No($"Say which project environment '{environment}' belongs to.",
                    $"بگویید محیط «{environment}» مال کدام پروژه است.");

            var visible = await Access.VisibleProjectIdsAsync(ct);
            var projects = await db.Projects.AsNoTracking()
                .Where(p => p.WorkspaceId == context.WorkspaceId)
                .Select(p => new { p.Id, p.Slug })
                .ToListAsync(ct);
            projects = projects.Where(p => visible is null || visible.Contains(p.Id)).ToList();

            if (projects.Count > 1)
                return Place.No(
                    $"Say which project: {string.Join(", ", projects.Select(p => p.Slug).Order(StringComparer.Ordinal))}.",
                    $"بگویید کدام پروژه: {string.Join("، ", projects.Select(p => p.Slug).Order(StringComparer.Ordinal))}.");

            if (projects.Count == 0)
            {
                // A workspace with nothing in it yet: the create makes its default project and
                // environment, exactly as the form does. A person who simply sees no project is not that.
                if (await db.Environments.AnyAsync(e => e.WorkspaceId == context.WorkspaceId, ct))
                    return Place.No("There is no project you can create an app in. Say which project.",
                        "پروژه‌ای نیست که بتوانید در آن اپ بسازید. بگویید کدام پروژه.");
                return new Place(null, ProjectService.DefaultProjectSlug, ProjectService.DefaultEnvironmentSlug,
                    WillExist: false, ProjectId: null, Error: null);
            }

            project = projects[0].Slug;
        }

        var lookup = await Resolver.EnvironmentAsync(context, project, environment, ct);
        if (lookup.Found)
        {
            var found = lookup.Value!;
            var owner = await db.Projects.AsNoTracking().Where(p => p.Id == found.ProjectId).Select(p => p.Slug).FirstAsync(ct);
            return new Place(found, owner, found.Slug, WillExist: false, found.ProjectId, Error: null);
        }

        // Not there yet: fine only when an earlier step of this plan makes it. create_project plans a
        // project with its default environment's slug in Environment; add_environment plans an
        // environment with its project in Project.
        var wantedProject = project.Trim().ToLowerInvariant();
        var wantedEnvironment = environment?.Trim().ToLowerInvariant();
        var plannedProject = scope?.Planned.FirstOrDefault(p =>
            p.Kind == AgentResourceKinds.Project && string.Equals(p.Name, wantedProject, StringComparison.OrdinalIgnoreCase));
        var plannedEnvironment = wantedEnvironment is not null && scope?.Planned.Any(p =>
            p.Kind == AgentResourceKinds.Environment
            && string.Equals(p.Name, wantedEnvironment, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Project, wantedProject, StringComparison.OrdinalIgnoreCase)) == true;

        if (plannedProject is not null)
        {
            var firstEnvironment = plannedProject.Environment ?? ProjectService.DefaultEnvironmentSlug;
            if (wantedEnvironment is not null && !plannedEnvironment
                && !string.Equals(wantedEnvironment, firstEnvironment, StringComparison.OrdinalIgnoreCase))
                return Place.No(
                    $"Project '{wantedProject}' is created by an earlier step with only its '{firstEnvironment}' environment; there is no '{wantedEnvironment}' in it.",
                    $"پروژهٔ «{wantedProject}» در مرحلهٔ قبلی فقط با محیط «{firstEnvironment}» ساخته می‌شود و محیط «{wantedEnvironment}» ندارد.");
            return new Place(null, wantedProject, wantedEnvironment ?? firstEnvironment, WillExist: true, ProjectId: null, Error: null);
        }

        if (!plannedEnvironment) return Place.No(lookup.Error!, lookup.ErrorFa!);
        var existing = await Resolver.ProjectAsync(context, project, ct);
        if (!existing.Found) return Place.No(existing.Error!, existing.ErrorFa!);
        return new Place(null, existing.Value!.Slug, wantedEnvironment!, WillExist: true, existing.Value.Id, Error: null);
    }

    /// <summary>Placement access with AppsCreate, asked of the right place: the environment when it
    /// exists, its project when only the environment is still to come, and outside every project when the
    /// project is too (only an unscoped member may) — the same question <c>ProjectsController.Create</c> asks.</summary>
    private Task<bool> MayCreateAsync(Place place, CancellationToken ct) =>
        Access.AllowsAsync(place.Environment is { } environment
            ? new ResourcePlacement(environment.ProjectId, environment.Id)
            : new ResourcePlacement(place.ProjectId, null), Capabilities.AppsCreate, ct);

    private static AgentValidation NotAllowed(Place place) => AgentValidation.Refused(
        $"Your role does not allow creating an app in {place.Name}: it needs the '{Capabilities.AppsCreate}' permission.",
        $"نقش شما اجازهٔ ساختن اپ در {place.Name} را نمی‌دهد: مجوز «{Capabilities.AppsCreate}» لازم است.");
}
