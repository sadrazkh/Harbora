using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Harbora.Infrastructure.Functions;
using Harbora.Infrastructure.Networking;
using Harbora.Web.Infrastructure;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Code written in the panel.
///
/// <para>
/// A function app is an ordinary <see cref="App"/> with <see cref="AppSourceType.InlineCode"/>, so
/// everything below is about the code and the triggers — deploying, health-checking, rolling back,
/// metering and quota are the platform's existing machinery, reached through the same
/// <c>IDeploymentEngine</c> every other app uses.
/// </para>
/// </summary>
[Authorize]
[RequireFeature(PlatformFeatures.Functions)]
[Route("functions")]
public sealed class FunctionsController(
    HarboraDbContext db,
    FunctionAppService functions,
    IFunctionInvoker invoker,
    IQuotaService quota,
    ISchedulerService scheduler,
    ICurrentUser currentUser,
    IAuditLogger audit,
    AppAddressAssigner addresses,
    Harbora.Infrastructure.Projects.ProjectService projects,
    Harbora.Infrastructure.Billing.ResourceCreationBilling creationBilling) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    /// <summary>
    /// The apps whose source is rows in this database. Everything else in the workspace is an
    /// ordinary application and belongs on the Apps page.
    /// </summary>
    private IQueryable<App> FunctionApps =>
        db.Apps.Where(a => a.WorkspaceId == WorkspaceId && a.SourceType == AppSourceType.InlineCode);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = IsFa ? "فانکشن‌ها" : "Functions";

        var apps = await FunctionApps.AsNoTracking()
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

        var counts = await db.FunctionDefinitions
            .Where(f => f.WorkspaceId == WorkspaceId)
            .GroupBy(f => f.AppId)
            .Select(g => new { AppId = g.Key, Count = g.Count(), Dirty = g.Count(f => f.HasUnpublishedChanges) })
            .ToDictionaryAsync(x => x.AppId, ct);

        var rootDomain = await addresses.RootDomainAsync(ct);

        return View(new FunctionAppListViewModel(apps.Select(a => new FunctionAppRow(
            a.Id, a.Name, a.Slug,
            a.FunctionRuntime ?? FunctionRuntime.CSharp,
            a.Status,
            counts.TryGetValue(a.Id, out var c) ? c.Count : 0,
            counts.TryGetValue(a.Id, out var d) && d.Dirty > 0,
            a.ActiveDeploymentId is not null)).ToList(), rootDomain));
    }

    [HttpGet("new")]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        ViewData["Title"] = IsFa ? "فانکشن‌اپ تازه" : "New function app";
        return View(await NewFormAsync(new FunctionAppFormModel(), ct));
    }

    [HttpPost("new")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> Create(FunctionAppFormModel model, CancellationToken ct)
    {
        var slug = FunctionSlug.Normalise(model.Name);
        if (slug.Length == 0)
            ModelState.AddModelError(nameof(model.Name), IsFa
                ? "نامی بگذارید که از حروف و رقم ساخته شده باشد."
                : "Choose a name made of letters and digits.");

        // Platform-wide, matching the rule the Apps page enforces: a slug is a container name and a
        // network alias before it is a label.
        if (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Slug == slug, ct))
            ModelState.AddModelError(nameof(model.Name), IsFa
                ? "اپی با این نام از قبل وجود دارد."
                : "An app with that name already exists.");

        var size = string.IsNullOrWhiteSpace(model.InstanceSizeKey)
            ? null
            : await db.InstanceSizes.FirstOrDefaultAsync(s => s.Key == model.InstanceSizeKey, ct);

        await using var reservation = await quota.AcquireCreationLockAsync(WorkspaceId, ct);

        var check = await quota.CanAddAppAsync(WorkspaceId, model.InstanceSizeKey, excludeAppId: null, ct);
        if (!check.Allowed)
            ModelState.AddModelError(string.Empty, (IsFa ? check.ReasonFa : null) ?? check.Reason ?? "Plan quota exceeded.");

        var placement = await scheduler.PlaceAsync(size?.MemoryBytes ?? 0, size?.CpuCores ?? 0, null, ct);
        if (!placement.Ok)
            ModelState.AddModelError(string.Empty, placement.Reason ?? "No server has capacity.");

        if (!ModelState.IsValid) return View(await NewFormAsync(model, ct));

        var environment = await projects.ResolveEnvironmentAsync(WorkspaceId, null, ct);

        var app = new App
        {
            WorkspaceId = WorkspaceId,
            EnvironmentId = environment.Id,
            ServerId = placement.ServerId!.Value,
            Name = model.Name.Trim(),
            Slug = slug,
            SourceType = AppSourceType.InlineCode,
            Kind = ServiceKind.Web,
            FunctionRuntime = model.Runtime,
            // The generator always writes this file, so stack detection is never consulted — and the
            // build log never claims to have auto-detected a stack nobody chose.
            DockerfilePath = "Dockerfile.harbora",
            ContainerPort = FunctionProject.DefaultPort,
            HealthCheckPath = FunctionProject.HealthPath,
            InstanceSizeKey = size?.Key,
            MemoryLimitBytes = size?.MemoryBytes ?? 0,
            DiskLimitBytes = size?.DiskBytes ?? 0,
            CpuLimit = size?.CpuCores ?? 0
        };
        functions.EnsureSecret(app);

        var addressed = await addresses.AssignAsync(app, null, AppAddressRequestOrigin.Derived, suffix: null, ct);
        if (addressed.Outcome is AppAddressOutcome.Reserved or AppAddressOutcome.Taken)
        {
            ModelState.AddModelError(nameof(model.Name), IsFa
                ? "این نام روی دامنه‌ی برنامه‌ها گرفته شده است؛ نام دیگری بگذارید."
                : "That name is taken on the apps domain — choose another.");
            return View(await NewFormAsync(model, ct));
        }

        // The first function comes with the app. An empty function app cannot be published — the
        // pipeline refuses it — so creating one and leaving the person on a page whose only button
        // fails would be the platform setting a trap it then springs.
        var starterSlug = "hello";
        db.FunctionDefinitions.Add(new FunctionDefinition
        {
            AppId = app.Id,
            WorkspaceId = WorkspaceId,
            Name = "Hello",
            Slug = starterSlug,
            Trigger = FunctionTrigger.Http,
            Code = FunctionStarters.For(model.Runtime, FunctionTrigger.Http),
            HasUnpublishedChanges = true
        });

        db.Apps.Add(app);
        try
        {
            // The server is named, not defaulted: a price belongs to a (server, tier) pair, so a
            // function app that prepaid the global rate would be charged its host's rate every hour
            // afterwards — a bill that does not reconcile against its own first line.
            await creationBilling.SaveAsync(WorkspaceId,
                [new(Harbora.Domain.Billing.BilledResourceType.App, app.Id, app.Name, app.InstanceSizeKey,
                    app.ServerId)], ct);
        }
        catch (Harbora.Infrastructure.Billing.CreationPaymentRequiredException ex)
        {
            db.ChangeTracker.Clear();
            ModelState.AddModelError(string.Empty, IsFa ? ex.ReasonFa : ex.Message);
            return View(await NewFormAsync(model, ct));
        }

        await reservation.CommitAsync(ct);
        await audit.LogAsync("functions.app.create", "App", app.Id.ToString(), ct: ct);

        return RedirectToAction(nameof(Details), new { id = app.Id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var app = await FunctionApps.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();

        ViewData["Title"] = app.Name;

        var defined = await functions.ListAsync(app.Id, ct);
        var host = await db.Domains.Where(d => d.AppId == app.Id).Select(d => d.Host).FirstOrDefaultAsync(ct);

        return View(new FunctionAppDetailsViewModel(
            app.Id, app.Name, app.Slug, app.FunctionRuntime ?? FunctionRuntime.CSharp, app.Status,
            app.ActiveDeploymentId is not null, host,
            defined.Select(f => new FunctionRow(
                f.Id, f.Name, f.Slug, f.Trigger, FunctionProject.RouteFor(f),
                f.CronExpression, f.EventKey, f.IsEnabled, f.HasUnpublishedChanges, f.NextRunAt)).ToList()));
    }

    [HttpPost("{id:guid}/publish")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct)
    {
        var app = await FunctionApps.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();

        // An app created before the secret existed, or one whose secret never saved, would deploy a
        // host that refuses every scheduled call with a 401. Issue it here, where it still can be.
        if (functions.EnsureSecret(app)) await db.SaveChangesAsync(ct);

        try
        {
            var deploymentId = await functions.PublishAsync(app.Id, currentUser.UserId ?? Guid.Empty, ct);
            await audit.LogAsync("functions.publish", "App", app.Id.ToString(), ct: ct);
            return RedirectToAction("Details", "Deployments", new { id = deploymentId });
        }
        catch (Exception ex) when (ex is InvalidOperationException or QuotaRefusedException)
        {
            TempData["Error"] = (ex as QuotaRefusedException) is { } refused && IsFa
                ? refused.ReasonFa
                : ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
    }

    // ------------------------------------------------------------- functions

    [HttpGet("{id:guid}/new")]
    public async Task<IActionResult> NewFunction(Guid id, FunctionTrigger trigger, CancellationToken ct)
    {
        var app = await FunctionApps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();

        var runtime = app.FunctionRuntime ?? FunctionRuntime.CSharp;
        ViewData["Title"] = IsFa ? "فانکشن تازه" : "New function";

        return View("EditFunction", new FunctionEditViewModel(
            app.Id, app.Name, runtime, null,
            new FunctionFormModel
            {
                Trigger = trigger,
                Code = FunctionStarters.For(runtime, trigger),
                IsEnabled = true
            },
            FunctionEvents.All));
    }

    [HttpGet("{id:guid}/{functionId:guid}")]
    public async Task<IActionResult> EditFunction(Guid id, Guid functionId, CancellationToken ct)
    {
        var app = await FunctionApps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();

        var fn = await db.FunctionDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == functionId && f.AppId == id, ct);
        if (fn is null) return NotFound();

        ViewData["Title"] = fn.Name;
        var recent = await functions.RecentInvocationsAsync(fn.Id, 20, ct);

        return View(new FunctionEditViewModel(
            app.Id, app.Name, app.FunctionRuntime ?? FunctionRuntime.CSharp, fn.Id,
            new FunctionFormModel
            {
                Name = fn.Name,
                Trigger = fn.Trigger,
                Route = fn.Route,
                CronExpression = fn.CronExpression,
                EventKey = fn.EventKey,
                Code = fn.Code,
                IsEnabled = fn.IsEnabled
            },
            FunctionEvents.All,
            recent.Select(i => new FunctionRunRow(
                i.StartedAt, i.Trigger, i.StatusCode, i.Succeeded, i.DurationMs, i.Error, i.CompletedAt is null)).ToList(),
            IsPublished: app.ActiveDeploymentId is not null,
            HasUnpublishedChanges: fn.HasUnpublishedChanges));
    }

    [HttpPost("{id:guid}/save")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> SaveFunction(Guid id, Guid? functionId, FunctionFormModel model, CancellationToken ct)
    {
        var app = await FunctionApps.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();

        var runtime = app.FunctionRuntime ?? FunctionRuntime.CSharp;
        var existing = await functions.ListAsync(app.Id, ct);

        var (candidate, failure) = TryBuildCandidate(app, functionId, model, existing, runtime);
        if (failure is not null) return failure;

        if (functionId is null) db.FunctionDefinitions.Add(candidate!);

        // A schedule that changed must be recomputed, or the function keeps firing on the old one
        // until the next tick after its stale NextRunAt — which for a monthly job is a month.
        candidate!.NextRunAt = null;
        candidate.UpdatedAt = DateTimeOffset.UtcNow;

        // Whole-app, because the image is whole-app: publishing rebuilds every function, so saying
        // only this one is unpublished would describe something the platform cannot do.
        await functions.MarkDirtyAsync(app.Id, ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("functions.save", "App", app.Id.ToString(), ct: ct);

        TempData["Message"] = IsFa
            ? "ذخیره شد. برای اجرا شدن، «انتشار» را بزنید."
            : "Saved. Press Publish to make it live.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Validates the posted form against a candidate row (new, or the one <paramref name="functionId"/>
    /// names) and applies it. Shared by <see cref="SaveFunction"/> and <see cref="RunNow"/> so a
    /// rejected edit looks — and behaves — identically whichever button posted it.
    /// </summary>
    /// <returns>
    /// The tracked, validated candidate and a null failure on success; on refusal, a null candidate and
    /// the exact view a caller should return unchanged, with the model state carrying the reason and the
    /// typed code preserved.
    /// </returns>
    private (FunctionDefinition? Candidate, IActionResult? Failure) TryBuildCandidate(
        App app, Guid? functionId, FunctionFormModel model, List<FunctionDefinition> existing, FunctionRuntime runtime)
    {
        var candidate = functionId is { } editing
            ? existing.FirstOrDefault(f => f.Id == editing)
            : new FunctionDefinition { AppId = app.Id, WorkspaceId = WorkspaceId };
        if (candidate is null) return (null, NotFound());

        candidate.Name = model.Name?.Trim() ?? "";
        candidate.Slug = FunctionSlug.Normalise(candidate.Name);
        candidate.Trigger = model.Trigger;
        candidate.Route = model.Trigger == FunctionTrigger.Http && !string.IsNullOrWhiteSpace(model.Route)
            ? model.Route.Trim().Trim('/') : null;
        candidate.CronExpression = model.Trigger == FunctionTrigger.Cron ? model.CronExpression?.Trim() : null;
        candidate.EventKey = model.Trigger == FunctionTrigger.Event ? model.EventKey : null;
        candidate.Code = model.Code ?? "";
        candidate.IsEnabled = model.IsEnabled;

        var validation = FunctionAppService.Validate(candidate, existing, functionId);
        if (!validation.Ok)
        {
            // Detach so a rejected edit does not leave the tracked entity carrying the values that
            // were refused — the next save on this context would write them without being asked.
            db.ChangeTracker.Clear();
            ModelState.AddModelError(validation.Field ?? string.Empty,
                (IsFa ? validation.MessageFa : validation.Message) ?? "Invalid.");

            return (null, View("EditFunction", new FunctionEditViewModel(
                app.Id, app.Name, runtime, functionId, model, FunctionEvents.All)));
        }

        return (candidate, null);
    }

    [HttpPost("{id:guid}/{functionId:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> DeleteFunction(Guid id, Guid functionId, CancellationToken ct)
    {
        var fn = await db.FunctionDefinitions.FirstOrDefaultAsync(f => f.Id == functionId && f.AppId == id, ct);
        if (fn is null) return NotFound();

        db.FunctionDefinitions.Remove(fn);
        await functions.MarkDirtyAsync(id, ct);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("functions.delete", "App", id.ToString(), ct: ct);

        TempData["Message"] = IsFa
            ? "حذف شد. تا زمانی که منتشر نکنید، هنوز روی سرور اجرا می‌شود."
            : "Deleted. It keeps running on the server until you publish.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Runs one function now, through exactly the door a schedule uses.
    ///
    /// <para>
    /// Deliberately the same path rather than a direct HTTP call from here: a person testing a
    /// function by hand must be testing what will happen at 03:00, including the secret, the
    /// envelope and the invocation row.
    /// </para>
    ///
    /// <para>
    /// It runs the <em>published</em> code, and the editor says so beside the button whenever the
    /// saved row differs from what is deployed. Making it run the buffer instead was tried and
    /// reverted: nothing here can execute code that was never built into an image, so "run the
    /// buffer" could only mean save-and-publish — which turned Run now into a second name for
    /// Publish, cost the one way to test a cron function without waiting for 03:00, and made the
    /// panel <em>less</em> clear rather than more. Two buttons, two genuinely different acts, each
    /// labelled with what it does.
    /// </para>
    ///
    /// <para>
    /// Stays on the lighter <see cref="Capabilities.AppsOperate"/> for that reason: an Operator may
    /// run a function without being able to edit or deploy one, and running published code is the
    /// operating act, not the editing one.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/{functionId:guid}/run")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsOperate)]
    public async Task<IActionResult> RunNow(Guid id, Guid functionId, CancellationToken ct)
    {
        var fn = await db.FunctionDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == functionId && f.AppId == id, ct);
        if (fn is null) return NotFound();

        var queued = await invoker.QueueAsync(fn.Id, fn.Trigger == FunctionTrigger.Http
            ? FunctionTrigger.Http : fn.Trigger, evt: null, ct);

        await audit.LogAsync("functions.run", "App", id.ToString(), ct: ct);

        TempData[queued is null ? "Error" : "Message"] = queued is null
            ? (IsFa
                ? "اجرا نشد: یا فانکشن خاموش است یا این اپ هنوز منتشر نشده."
                : "Nothing ran: the function is off, or this app has never been published.")
            : (IsFa
                ? "نسخه‌ی منتشرشده اجرا شد — نتیجه در تاریخچه‌ی همین فانکشن است."
                : "Ran the published version — the result is in this function's history.");

        return RedirectToAction(nameof(EditFunction), new { id, functionId });
    }

    private async Task<FunctionAppFormViewModel> NewFormAsync(FunctionAppFormModel model, CancellationToken ct)
    {
        var sizes = await db.InstanceSizes.AsNoTracking()
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.MemoryBytes)
            .Select(s => new FunctionSizeOption(s.Key, s.Name, s.MemoryBytes, s.CpuCores))
            .ToListAsync(ct);

        return new FunctionAppFormViewModel(model, sizes);
    }
}
