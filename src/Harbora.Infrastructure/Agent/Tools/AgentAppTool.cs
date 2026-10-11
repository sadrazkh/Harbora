using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// The app a step is about, as a write tool sees it: a row the person may touch with the tool's
/// capability, an app an earlier step of the same plan creates, or the reason it is neither — in both
/// languages.
/// </summary>
public sealed record AppTarget(App? App, AgentPlannedResource? Planned, string? Error, string? ErrorFa)
{
    public bool Refused => Error is not null;

    public static AppTarget Of(App app) => new(app, null, null, null);

    public static AppTarget WillExist(AgentPlannedResource planned) => new(null, planned, null, null);

    public static AppTarget No(string error, string errorFa) => new(null, null, error, errorFa);
}

/// <summary>
/// What every tool that acts on an app shares. The two rules this exists to keep identical across
/// thirteen tools: an app is resolved only through <see cref="AgentResourceResolver"/> (so "cannot see"
/// and "does not exist" are one sentence everywhere), and seeing it is not touching it — every tool asks
/// <see cref="ProjectAccessService"/> for the same capability the matching controller action's policy
/// carries, before validating and again before executing, because the engine and operations services
/// behind these tools check nothing themselves.
/// </summary>
public abstract class AgentAppTool(AgentResourceResolver resolver, ProjectAccessService access) : AgentWriteTool
{
    protected AgentResourceResolver Resolver => resolver;
    protected ProjectAccessService Access => access;

    /// <summary>
    /// The app named by <paramref name="name"/>, if this person may exercise <paramref name="capability"/>
    /// on it. With a <paramref name="scope"/> (validation only), a name no app has yet but an earlier
    /// step of this plan creates is accepted as will-exist; the capability is then asked of the place
    /// that step creates it in. Execution passes no scope: by then the app has to exist.
    /// </summary>
    protected async Task<AppTarget> TargetAsync(
        AgentToolContext context, string? name, string capability, AgentPlanScope? scope, CancellationToken ct)
    {
        var lookup = await resolver.AppAsync(context, name, ct);
        if (lookup.Found)
        {
            var app = lookup.Value!;
            return await access.CanTouchAppAsync(app.Id, capability, ct)
                ? AppTarget.Of(app)
                : AppTarget.No(
                    $"Your role does not allow this on app '{app.Slug}': it needs the '{capability}' permission.",
                    $"نقش شما اجازهٔ این کار را روی اپ «{app.Slug}» نمی‌دهد: مجوز «{capability}» لازم است.");
        }

        var planned = scope is null || string.IsNullOrWhiteSpace(name)
            ? null
            : scope.Planned.FirstOrDefault(p => p.Kind == AgentResourceKinds.App
                                                && string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (planned is null) return AppTarget.No(lookup.Error!, lookup.ErrorFa!);

        // Nobody holds a grant on an app that does not exist yet; the question is whether this person
        // may do this in the environment the earlier step creates it in — or, when that environment is
        // itself still to be created, anywhere outside every project, which only an unscoped member may.
        var environment = await resolver.EnvironmentAsync(context, planned.Project, planned.Environment, ct);
        var placement = environment.Found
            ? new ResourcePlacement(environment.Value!.ProjectId, environment.Value.Id)
            : new ResourcePlacement(null, null);
        return await access.AllowsAsync(placement, capability, ct)
            ? AppTarget.WillExist(planned)
            : AppTarget.No(
                $"Your role does not allow this in {PlaceOf(planned)}, where app '{planned.Name}' is to be created: it needs the '{capability}' permission.",
                $"نقش شما اجازهٔ این کار را در {PlaceOf(planned)} که اپ «{planned.Name}» قرار است آن‌جا ساخته شود نمی‌دهد: مجوز «{capability}» لازم است.");
    }

    /// <summary>"shop/production" for a planned app, as the snapshot names places.</summary>
    protected static string PlaceOf(AgentPlannedResource planned) =>
        $"{planned.Project ?? "?"}/{planned.Environment ?? "?"}";

    /// <summary>The sentence a will-exist step's detail carries, so the person knows what was not checked yet.</summary>
    protected static (string En, string Fa) PlannedNote(AgentPlannedResource planned) => (
        $"App '{planned.Name}' is created by an earlier step of this plan; the checks that need the app itself run when this step does.",
        $"اپ «{planned.Name}» در مرحلهٔ قبلی همین برنامه ساخته می‌شود؛ بررسی‌هایی که به خود اپ نیاز دارند هنگام اجرای این مرحله انجام می‌شوند.");

    /// <summary>The app services' actor. No IP: a tool is handed no request address, and the audit entry's
    /// person comes from the request's own signed-in user either way (see <see cref="AppActor"/>).</summary>
    protected static AppActor ActorOf(AgentToolContext context) => new(context.WorkspaceId);

    protected static AgentToolResult NotPossible(string error, string errorFa) => AgentToolResult.Failed(error, errorFa);

    // ---- links: the panel's own routes for each page (AppsController, DeploymentsController, ProjectsController) ----

    protected static AgentResourceLink AppLink(App app) =>
        new("app", app.Id, app.Slug, $"/apps/details/{app.Id}");

    protected static AgentResourceLink AppLink(Guid id, string slug) =>
        new("app", id, slug, $"/apps/details/{id}");

    protected static AgentResourceLink VolumesLink(App app) =>
        new("app", app.Id, $"{app.Slug} — volumes", $"/apps/{app.Id}/volumes");

    protected static AgentResourceLink DeploymentLink(Guid id, int number) =>
        new("deployment", id, $"#{number}", $"/deployments/details/{id}");

    protected static AgentResourceLink ProjectLink(Guid id, string slug) =>
        new("project", id, slug, $"/projects/{id}");

    // ---- deployments: what a queued request turned into, read back rather than assumed ----

    /// <summary>A deployment as it stands right after it was asked for.</summary>
    public sealed record QueuedDeployment(Guid Id, int Number, DeploymentStatus Status);

    protected static Task<QueuedDeployment?> ReadDeploymentAsync(HarboraDbContext db, Guid id, CancellationToken ct) =>
        db.Deployments.AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new QueuedDeployment(d.Id, d.Number, d.Status))
            .FirstOrDefaultAsync(ct);

    /// <summary>The app's deployment that has not settled yet, if any — a new request joins it instead of
    /// starting another (<c>DeploymentEngine</c> coalesces onto it).</summary>
    protected static Task<QueuedDeployment?> UnsettledDeploymentAsync(HarboraDbContext db, Guid appId, CancellationToken ct)
    {
        var unsettled = Domain.Deployments.DeploymentStateMachine.Unsettled.ToArray();
        return db.Deployments.AsNoTracking()
            .Where(d => d.AppId == appId && unsettled.Contains(d.Status))
            .OrderByDescending(d => d.Number)
            .Select(d => new QueuedDeployment(d.Id, d.Number, d.Status))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>"queued" or "waiting for approval" — never "running" or "done": a tool only ever queues.</summary>
    protected static (string En, string Fa, string Code) Describe(QueuedDeployment deployment) => deployment.Status switch
    {
        DeploymentStatus.PendingApproval => (
            $"deployment #{deployment.Number} is waiting for approval (protected environment)",
            $"استقرار شمارهٔ {deployment.Number} منتظر تأیید است (محیط محافظت‌شده)",
            "pending_approval"),
        DeploymentStatus.Queued => (
            $"deployment #{deployment.Number} is queued",
            $"استقرار شمارهٔ {deployment.Number} در صف است",
            "queued"),
        var status => (
            $"deployment #{deployment.Number} is {StatusEn(status)}",
            $"استقرار شمارهٔ {deployment.Number} {StatusFa(status)} است",
            AgentResourceResolver.Lower(status))
    };

    /// <summary>A deployment's state as an English phrase that fits "deployment #3 is …".</summary>
    protected static string StatusEn(DeploymentStatus status) => status switch
    {
        DeploymentStatus.HealthChecking => "health-checking",
        DeploymentStatus.PendingApproval => "waiting for approval",
        DeploymentStatus.RolledBack => "rolled back",
        _ => AgentResourceResolver.Lower(status)
    };

    /// <summary>The same state in Persian, for "استقرار شمارهٔ ۳ … است".</summary>
    protected static string StatusFa(DeploymentStatus status) => status switch
    {
        DeploymentStatus.Queued => "در صف",
        DeploymentStatus.Building => "در حال ساخت",
        DeploymentStatus.Pushing => "در حال ارسال ایمیج",
        DeploymentStatus.Deploying => "در حال استقرار",
        DeploymentStatus.HealthChecking => "در حال بررسی سلامت",
        DeploymentStatus.PendingApproval => "منتظر تأیید",
        DeploymentStatus.Succeeded => "موفق",
        DeploymentStatus.Failed => "ناموفق",
        DeploymentStatus.Cancelled => "لغوشده",
        DeploymentStatus.RolledBack => "بازگردانده‌شده",
        _ => AgentResourceResolver.Lower(status)
    };

    /// <summary>
    /// A deploy request the engine refused, in both languages. The three preflight refusals carry their own
    /// Persian. An <see cref="InvalidOperationException"/> counts only when the engine itself threw it (a
    /// rollback that will not be merged into a deploy in flight, an app that is gone) — it is English only,
    /// so the Persian says what happened and carries the English detail, the shape the app services use for
    /// such texts. Anything else — an EF error is an <see cref="InvalidOperationException"/> too — is not a
    /// refusal: null, so it reaches the plan engine, which logs it and reports a failed step in neutral words.
    /// </summary>
    protected static (string En, string Fa)? DeployRefusal(Exception ex) => ex switch
    {
        QuotaRefusedException quota => (quota.Message, quota.ReasonFa ?? quota.Message),
        CapacityRefusedException capacity => (capacity.Message, capacity.ReasonFa ?? capacity.Message),
        LowDiskRefusedException disk => (disk.Message, disk.ReasonFa ?? disk.Message),
        InvalidOperationException own when ThrownBy(own, typeof(IDeploymentEngine)) =>
            (own.Message, $"استقرار در صف قرار نگرفت: {own.Message}"),
        _ => null
    };

    /// <summary>
    /// Whether <paramref name="ex"/> was thrown by code of <paramref name="owner"/> (or a type implementing
    /// it) itself, rather than by something it called. An async method throws from its state machine,
    /// which is nested in the type that declares the method, so the declaring types are walked outwards.
    /// </summary>
    internal static bool ThrownBy(Exception ex, Type owner)
    {
        for (var type = ex.TargetSite?.DeclaringType; type is not null; type = type.DeclaringType)
            if (owner.IsAssignableFrom(type)) return true;
        return false;
    }

    /// <summary>
    /// The same line the deploy card carries for a protected environment: whether a deploy will wait for
    /// a second person is decided by the engine when it queues (nobody else eligible means it runs at
    /// once), so the card says what usually happens and names the exception.
    /// </summary>
    protected static (string En, string Fa) ProtectedEnvironmentWarning(string environment) => (
        $"Environment '{environment}' is protected: the deployment waits for approval by another member who may deploy, unless there is nobody else who could approve it.",
        $"محیط «{environment}» محافظت‌شده است: استقرار منتظر تأیید عضو دیگری با اجازهٔ استقرار می‌ماند، مگر این‌که کسی دیگری برای تأیید نباشد.");

    // ---- secrets entered with the lock button (AgentSecretContracts) ----

    /// <summary>How a handle looks, for the sentences that tell the model what to pass on.</summary>
    protected const string HandleShape = "{{secret:…}}";

    /// <summary>A plain value given for a secret: refused, with the way a secret is meant to arrive.</summary>
    protected static (string En, string Fa) PlainSecret(string key, bool offerGenerate) => (
        $"{key} is a secret, and a secret's value must not travel through the chat. Ask the user to enter it with the lock button beside the message box — it reaches you as {HandleShape}; pass that on unchanged as the value" +
        (offerGenerate ? " — or use generate: true for a random value nobody needs to type." : "."),
        $"{key} محرمانه است و مقدار آن نباید از گفتگو بگذرد. از کاربر بخواهید آن را با دکمهٔ قفل کنار کادر پیام وارد کند — به شکل {HandleShape} به شما می‌رسد و همان را بی‌تغییر به‌عنوان مقدار بفرستید" +
        (offerGenerate ? " — یا برای مقداری تصادفی که کسی لازم نیست تایپ کند generate: true را بدهید." : "."));

    /// <summary>A handle the vault does not know for this person: never made, someone else's, or expired.</summary>
    protected static (string En, string Fa) UnknownHandle(string key) => (
        $"The secret given for {key} is unknown or has expired (one entered with the lock button lasts {AgentSecretHandles.Lifetime.TotalMinutes:0} minutes). Ask the user to enter it again with the lock button.",
        $"رمزی که برای {key} داده شده شناخته‌شده نیست یا منقضی شده است (رمزی که با دکمهٔ قفل وارد می‌شود {AgentSecretHandles.Lifetime.TotalMinutes:0} دقیقه می‌ماند). از کاربر بخواهید آن را دوباره با دکمهٔ قفل وارد کند.");

    /// <summary>A handle that expired between the card and the run: nothing is applied with anything else.</summary>
    protected static (string En, string Fa) Expired(string key) => (
        $"The secret for {key} expired before this step ran, so nothing was changed. Ask the user to enter it again with the lock button.",
        $"رمز {key} پیش از اجرای این مرحله منقضی شد، پس چیزی تغییر نکرد. از کاربر بخواهید آن را دوباره با دکمهٔ قفل وارد کند.");

    // ---- staged rows a refused service left behind ----

    /// <summary>
    /// Detaches every unsaved change that appeared since <paramref name="before"/> was taken — what a
    /// service staged before it said no — dependents before the rows they depend on.
    ///
    /// <para>
    /// The order is the point. <c>AgentTracker.DetachForeignChanges</c> detaches in tracker order, and
    /// detaching an added environment while an added database still points at it makes EF sever a required
    /// relationship and throw ("the association … has been severed") — found with the template service,
    /// which stages a project, its environment and its databases before its later refusals. Leaves first,
    /// so nothing is ever left pointing at a row that is already gone.
    /// </para>
    /// </summary>
    internal static void DropStaged(Microsoft.EntityFrameworkCore.DbContext db, IReadOnlySet<object> before)
    {
        var remaining = AgentTracker.ForeignChanges(db)
            .Where(entity => !before.Contains(entity))
            .Select(entity => db.Entry(entity))
            .ToList();

        while (remaining.Count > 0)
        {
            var leaves = remaining.Where(entry => !remaining.Any(other => !ReferenceEquals(other, entry) && DependsOn(other, entry))).ToList();
            if (leaves.Count == 0) leaves = remaining.ToList(); // a cycle: nothing order can save
            foreach (var leaf in leaves)
            {
                leaf.State = EntityState.Detached;
                remaining.Remove(leaf);
            }
        }
    }

    private static bool DependsOn(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry dependent,
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry principal) =>
        dependent.Metadata.GetForeignKeys().Any(fk =>
            fk.PrincipalEntityType.IsAssignableFrom(principal.Metadata)
            && fk.Properties.Select((property, i) => Equals(
                    dependent.Property(property.Name).CurrentValue,
                    principal.Property(fk.PrincipalKey.Properties[i].Name).CurrentValue))
                .All(same => same));

    // ---- shared lookups ----

    /// <summary>
    /// The instance sizes this workspace may pick — enabled, and on the plan's list when the plan has one;
    /// the same filter the panel's size choosers and <c>get_catalog</c> apply.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> AllowedSizeKeysAsync(HarboraDbContext db, Guid workspaceId, CancellationToken ct)
    {
        var planId = await db.Workspaces.AsNoTracking()
            .Where(w => w.Id == workspaceId).Select(w => w.PlanId).FirstOrDefaultAsync(ct);
        var allowedKeys = await db.Plans.AsNoTracking()
            .Where(p => planId != null ? p.Id == planId : p.IsDefault)
            .Select(p => p.AllowedSizeKeys)
            .FirstOrDefaultAsync(ct);
        var allowed = string.IsNullOrWhiteSpace(allowedKeys)
            ? null
            : allowedKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (await db.InstanceSizes.AsNoTracking()
                .Where(s => s.IsEnabled)
                .OrderBy(s => s.SortOrder).ThenBy(s => s.CpuCores)
                .Select(s => s.Key)
                .ToListAsync(ct))
            .Where(k => allowed is null || allowed.Contains(k))
            .ToList();
    }

    /// <summary>
    /// Whether creating something here may charge its first hour: billing is on and this is not the
    /// provider's own workspace, which <c>ResourceCreationBilling</c> never charges. Validation cannot ask
    /// the wallet — only the create itself does — so the card says it rather than promising either way.
    /// </summary>
    internal static async Task<bool> MayChargeAsync(
        HarboraDbContext db, IOptions<BillingOptions> billing, Guid workspaceId, CancellationToken ct) =>
        billing.Value.Enabled
        && !await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => w.IsDefault).FirstOrDefaultAsync(ct);

    internal static readonly (string En, string Fa) FirstHourWarning = (
        "If this workspace pays for resources, the first hour is charged when it is created; the wallet is only checked then, so the step can still be refused for payment.",
        "اگر این فضای کاری هزینهٔ منابع را می‌پردازد، ساعت اول هنگام ساخت کسر می‌شود؛ موجودی کیف پول فقط همان موقع بررسی می‌شود، پس این مرحله ممکن است به‌خاطر پرداخت رد شود.");

    /// <summary>A Git URL the assistant may use: a public http(s) clone URL with no credentials in it.</summary>
    internal static (string En, string Fa)? CheckRepositoryUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return ("Give the repository's public https clone URL, for example https://github.com/owner/repo.git.",
                "آدرس عمومی https مخزن را بدهید، مثلاً https://github.com/owner/repo.git.");

        // A token pasted into the URL is a token handed to the model, stored in the plan and shown on the
        // card. Private repositories are out of the assistant's reach in v1; the panel's form takes them.
        // Both places a Git host accepts one: before the host, and as a query parameter.
        if (!string.IsNullOrEmpty(uri.UserInfo) || CarriesTokenInQuery(uri.Query))
            return ("A repository URL with a user name or token in it is not accepted: the assistant works only with public repositories. Create an app from a private repository in the panel's app form.",
                "آدرس مخزنی که نام کاربری یا توکن در آن باشد پذیرفته نمی‌شود: دستیار فقط با مخزن‌های عمومی کار می‌کند. اپِ مخزن خصوصی را از فرم ساخت اپ در پنل بسازید.");

        return null;
    }

    private static readonly string[] TokenQueryNames = ["access_token", "token", "private_token", "oauth_token", "password", "auth"];

    private static bool CarriesTokenInQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => Uri.UnescapeDataString(pair.Split('=', 2)[0]))
            .Any(name => TokenQueryNames.Contains(name, StringComparer.OrdinalIgnoreCase));
}
