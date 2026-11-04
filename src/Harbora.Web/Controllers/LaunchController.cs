using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Launch;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Security;
using Harbora.Shared.Inspection;
using Harbora.Web.Infrastructure;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers;

/// <summary>
/// "Here is my code, make it work": one question, one screen, one button.
///
/// <para>
/// Only the HTTP shape lives here. Reading the source, working out what it needs, creating it and
/// walking it to a running app are all <see cref="ILaunchService"/>, which judges the signed-in user
/// through the same services the forms use. This turns a form into its choices and a refusal back into
/// a form.
/// </para>
///
/// <para>
/// One rule the routes below share: <see cref="ILaunchService.GetAsync"/> is the workspace's read, so
/// every route that names a launch asks it first and answers 404 when it is not this workspace's,
/// whatever the route goes on to do.
/// </para>
/// </summary>
[Authorize]
[Route("launch")]
public sealed class LaunchController(
    ILaunchService launches,
    HarboraDbContext db,
    ICurrentUser currentUser,
    ProjectAccessService access,
    AppAddressAssigner addresses,
    SizePickerService sizes,
    IOptions<HarboraRuntimeOptions> runtime) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    private string Say(ServiceRefusal refusal) => IsFa ? refusal.MessageFa : refusal.Message;
    private string Say(IEnumerable<ServiceRefusal> refusals) => string.Join(" ", refusals.Select(Say));

    [HttpGet("")]
    public IActionResult Index() => View(new LaunchQuestionModel());

    /// <summary>
    /// Takes the source and starts reading it. Nothing is created and nothing is charged here, so all
    /// this can be refused for is the source itself or who is asking.
    /// </summary>
    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsCreate)]
    [RequestSizeLimit(UploadedArchive.MaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadedArchive.MaxBytes)]
    public async Task<IActionResult> Start(LaunchStartForm form, CancellationToken ct)
    {
        var gitUrl = form.GitUrl?.Trim();
        var gitRef = form.GitRef?.Trim();

        IActionResult Refused(string message) => View(nameof(Index), new LaunchQuestionModel(
            gitUrl, gitRef, message, AskedForDetails: !string.IsNullOrEmpty(gitRef) || !string.IsNullOrEmpty(form.GitToken)));

        string? archivePath = null;
        if (form.File is { } file)
        {
            if (file.Length == 0)
                return Refused(IsFa
                    ? "این فایل خالی است. پوشه‌ی پروژه را دوباره zip کن."
                    : "That file is empty. Zip your project folder again.");
            if (!UploadedArchive.LooksLikeArchive(file))
                return Refused(IsFa
                    ? "این فایل zip نیست. پوشه‌ی پروژه را zip کن و همان را بفرست."
                    : "That file isn't a zip. Zip your project folder and send that.");

            archivePath = UploadedArchive.PathForLaunch(runtime.Value.WorkDir, file.FileName);
            await UploadedArchive.SaveAsync(file, archivePath, ct);
        }
        else if (string.IsNullOrEmpty(gitUrl))
        {
            return Refused(IsFa
                ? "آدرس مخزن را بنویس، یا یک فایل zip انتخاب کن."
                : "Paste the repository's URL, or choose a zip.");
        }

        ServiceOutcome<Guid> outcome;
        try
        {
            outcome = await launches.StartAsync(new LaunchStartRequest
            {
                WorkspaceId = WorkspaceId,
                UserId = currentUser.UserId ?? Guid.Empty,
                Source = archivePath is null ? LaunchSourceKind.Git : LaunchSourceKind.Upload,
                CloneUrl = archivePath is null ? gitUrl : null,
                GitRef = archivePath is null && !string.IsNullOrEmpty(gitRef) ? gitRef : null,
                GitToken = archivePath is null && !string.IsNullOrWhiteSpace(form.GitToken) ? form.GitToken.Trim() : null,
                ArchivePath = archivePath,
                ClientIp = ClientIp
            }, ct);
        }
        catch (Exception) when (archivePath is not null)
        {
            // The launch only owns the archive once it exists.
            UploadedArchive.Delete(archivePath);
            throw;
        }

        if (!outcome.Ok)
        {
            if (archivePath is not null) UploadedArchive.Delete(archivePath);
            return Refused(Say(outcome.Refusals));
        }

        // LaunchView cannot say which branch was asked for, and a failed read draws the form again
        // with what was typed. The address is the one thing that survives until then.
        return archivePath is null && !string.IsNullOrEmpty(gitRef)
            ? Redirect($"/launch/{outcome.Value}?branch={Uri.EscapeDataString(gitRef)}")
            : Redirect($"/launch/{outcome.Value}");
    }

    /// <summary>One address for the whole of a launch's life. What it draws depends on how far it has got.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, string? branch, CancellationToken ct)
    {
        var view = await launches.GetAsync(id, ct);
        if (view is null) return NotFound();

        if (view.Status == LaunchStatus.InspectFailed)
            return View(nameof(Index), new LaunchQuestionModel(
                view.Source == LaunchSourceKind.Git ? view.SourceLabel : null,
                branch,
                IsFa ? view.FailureMessageFa : view.FailureMessage,
                ReadingFailed: true,
                AskedForDetails: view.Source == LaunchSourceKind.Git));

        if (view.Status == LaunchStatus.AwaitingConfirmation
            && await launches.ProposeAsync(id, ct) is { } proposal)
            return await ConfirmScreenAsync(proposal, FormFrom(proposal), [], secretsToRetype: false, ct);

        return View(await ProgressAsync(view, ct));
    }

    /// <summary>What the poll fetches: the same part of the page, on its own.</summary>
    [HttpGet("{id:guid}/progress")]
    public async Task<IActionResult> Progress(Guid id, CancellationToken ct)
    {
        var view = await launches.GetAsync(id, ct);
        if (view is null) return NotFound();

        return PartialView("_Progress", await ProgressAsync(view, ct));
    }

    /// <summary>
    /// The figure for the form as it stands now, for when a service or the size changes. Open to anyone
    /// in the workspace, like the screen it belongs to: it spends nothing, and confirming is what is gated.
    /// </summary>
    [HttpPost("{id:guid}/quote")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Quote(Guid id, LaunchConfirmForm form, CancellationToken ct)
    {
        if (await launches.GetAsync(id, ct) is null) return NotFound();
        if (await launches.ProposeAsync(id, ct) is not { } proposal) return NotFound();

        var (choices, _) = ChoicesFrom(form, proposal);
        return PartialView("_Cost", await launches.QuoteAsync(id, choices, ct));
    }

    /// <summary>
    /// The button. A second submit finds the launch already past this screen and lands on its progress,
    /// because the person who double-clicked wants to see it going, not to read an error about it.
    /// </summary>
    [HttpPost("{id:guid}/confirm")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> Confirm(Guid id, LaunchConfirmForm form, CancellationToken ct)
    {
        var view = await launches.GetAsync(id, ct);
        if (view is null) return NotFound();
        if (view.Status != LaunchStatus.AwaitingConfirmation
            || await launches.ProposeAsync(id, ct) is not { } proposal)
            return Redirect($"/launch/{id}");

        // A value the screen is about to draw again is only ever one it may draw again. A secret
        // comes off here, however this ends, and the screen asks for it once more.
        var typedSecret = proposal.EnvFields.Any(f =>
            f.IsSecret && form.Values.TryGetValue(f.Key, out var value) && !string.IsNullOrEmpty(value));

        if (form.Recheck is not null)
            return await ConfirmScreenAsync(proposal, form, [], typedSecret, ct);

        var (choices, formRefusals) = ChoicesFrom(form, proposal);

        var refusals = formRefusals.Count > 0
            ? formRefusals
            : await launches.ValidateConfirmAsync(id, choices, ct);
        if (refusals.Count > 0)
            return await ConfirmScreenAsync(proposal, form, refusals, typedSecret, ct);

        var outcome = await launches.ConfirmAsync(id, choices, ClientIp, ct);
        if (outcome.Ok) return Redirect($"/launch/{id}");

        // Refused before anything moved stays on the form. Anything else is the timeline's to explain:
        // a double submit that lost the race, or a step refused part-way with what exists on the row.
        var after = await launches.GetAsync(id, ct);
        return after?.Status == LaunchStatus.AwaitingConfirmation
            ? await ConfirmScreenAsync(proposal, form, outcome.Refusals, typedSecret, ct)
            : Redirect($"/launch/{id}");
    }

    [HttpPost("{id:guid}/retry")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        if (await launches.GetAsync(id, ct) is null) return NotFound();

        var outcome = await launches.RetryAsync(id, ClientIp, ct);
        if (!outcome.Ok) TempData["Error"] = Say(outcome.Refusals);

        return Redirect($"/launch/{id}");
    }

    [HttpPost("{id:guid}/abandon")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> Abandon(Guid id, CancellationToken ct)
    {
        if (await launches.GetAsync(id, ct) is null) return NotFound();

        if (await launches.AbandonAsync(id, ct)) return Redirect("/launch");

        TempData["Error"] = IsFa
            ? "دیگر نمی‌شود بی‌خیالش شد؛ ساختن شروع شده است."
            : "It's too late to call this off: creating has already begun.";
        return Redirect($"/launch/{id}");
    }

    // ---- the form and the choices ------------------------------------------------------------------

    /// <summary>The proposal as fields, before anybody has touched them.</summary>
    private static LaunchConfirmForm FormFrom(LaunchProposal proposal)
    {
        var defaults = proposal.Defaults;
        return new LaunchConfirmForm
        {
            Name = defaults.Name,
            Slug = defaults.Slug,
            Kind = defaults.Kind.ToString(),
            ContainerPort = defaults.ContainerPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Command = defaults.Command,
            ReleaseCommand = defaults.ReleaseCommand,
            RootDirectory = defaults.RootDirectory,
            EnvironmentId = defaults.EnvironmentId,
            ServerId = defaults.ServerId,
            InstanceSizeKey = defaults.InstanceSizeKey,
            Services = defaults.Services.Select(s => s.Type.ToString()).ToList(),
            Values = proposal.EnvFields
                .Where(f => f.Disposition == EnvVarDisposition.KeepExample && !f.IsSecret)
                .ToDictionary(f => f.Key, f => f.SuggestedValue)
        };
    }

    /// <summary>
    /// The posted form as what the service takes.
    ///
    /// <para>
    /// Read against the proposal rather than taken at its word: which variables exist, which are
    /// secret and which services are on offer are the server's facts, and a field in a form is only
    /// somebody's answer to one of them.
    /// </para>
    /// </summary>
    private (LaunchChoices Choices, IReadOnlyList<ServiceRefusal> Refusals) ChoicesFrom(LaunchConfirmForm form, LaunchProposal proposal)
    {
        var defaults = proposal.Defaults;
        var refusals = new List<ServiceRefusal>();

        var kind = form.Kind switch
        {
            nameof(ServiceKind.Worker) => ServiceKind.Worker,
            nameof(ServiceKind.Web) => ServiceKind.Web,
            _ => defaults.Kind
        };

        var port = defaults.ContainerPort;
        if (kind != ServiceKind.Worker && !string.IsNullOrWhiteSpace(form.ContainerPort)
            && !int.TryParse(WesternDigits(form.ContainerPort.Trim()), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out port))
        {
            port = defaults.ContainerPort;
            refusals.Add(new ServiceRefusal("The port has to be a number.", "پورت باید عدد باشد.", nameof(LaunchChoices.ContainerPort)));
        }

        // The one thing the first block can ask. The inspector found several apps and will not guess.
        var candidates = proposal.Inspection.RootCandidates;
        if (candidates.Count > 0 && !candidates.Contains(form.RootDirectory ?? "", StringComparer.Ordinal))
            refusals.Add(new ServiceRefusal(
                "There's more than one app in here. Choose which folder to launch.",
                "اینجا بیشتر از یک اپ هست. بگو کدام پوشه را راه بیندازم.",
                nameof(LaunchChoices.RootDirectory)));

        var ticked = form.Services.ToHashSet(StringComparer.Ordinal);
        var services = proposal.ServiceOptions
            .Where(o => ticked.Contains(o.Type.ToString()))
            .Select(o => defaults.Services.FirstOrDefault(s => s.Type == o.Type) ?? new LaunchServiceChoice(o.Type))
            .ToList();

        var variables = new List<LaunchEnvChoice>();
        foreach (var field in proposal.EnvFields)
        {
            if (field.Disposition == EnvVarDisposition.GeneratedSecret)
            {
                variables.Add(new LaunchEnvChoice(field.Key, null, IsSecret: true));
            }
            else if (field.Disposition is EnvVarDisposition.NeedsAnswer or EnvVarDisposition.KeepExample
                     && form.Values.TryGetValue(field.Key, out var value) && !string.IsNullOrEmpty(value))
            {
                // Blank is allowed, and means the variable is not set: an empty string in a variable is
                // a value, and a surprisingly confusing one for an app to read.
                variables.Add(new LaunchEnvChoice(field.Key, value, field.IsSecret));
            }
        }

        var choices = defaults with
        {
            // Blanking the name is not a reason to stop anybody. It goes back to what it was.
            Name = string.IsNullOrWhiteSpace(form.Name) ? proposal.AppName : form.Name.Trim(),
            Slug = Blank(form.Slug),
            Kind = kind,
            ContainerPort = port,
            Command = Blank(form.Command),
            ReleaseCommand = Blank(form.ReleaseCommand),
            RootDirectory = Blank(form.RootDirectory),
            EnvironmentId = form.EnvironmentId ?? defaults.EnvironmentId,
            ServerId = form.ServerId,
            InstanceSizeKey = Blank(form.InstanceSizeKey) ?? defaults.InstanceSizeKey,
            Services = services,
            EnvVars = variables
        };

        return (choices, refusals);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A Persian keyboard types ۸۰۸۰, and that is as much a number as 8080 is.</summary>
    private static string WesternDigits(string value) =>
        new(value.Select(c => char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : c).ToArray());

    private async Task<IActionResult> ConfirmScreenAsync(
        LaunchProposal proposal, LaunchConfirmForm form, IReadOnlyList<ServiceRefusal> refusals, bool secretsToRetype, CancellationToken ct)
    {
        // The chooser opens on a host and a tier of its own whenever nothing is asked for, and then posts
        // them. The figure beside the button has to be the figure for what the button would submit.
        var picker = await sizes.BuildAsync(WorkspaceId, nameof(form.InstanceSizeKey), nameof(form.ServerId),
            form.InstanceSizeKey, form.ServerId, allowNoLimit: false, pinnedServerId: null, ct);
        form.ServerId ??= picker.SelectedServerId;
        form.InstanceSizeKey ??= picker.SelectedSizeKey;

        var (choices, _) = ChoicesFrom(form, proposal);
        var quote = await launches.QuoteAsync(proposal.LaunchId, choices, ct);

        // Asked as a web app whatever this one is: the kind can be changed on the screen, and a worker
        // simply hides the line.
        var rootDomain = await addresses.RootDomainAsync(ct);
        var address = await addresses.PreviewAsync(
            new App { Kind = ServiceKind.Web, Slug = choices.Slug ?? AppProvisioningService.Slugify(choices.Name) }, ct);

        foreach (var secret in proposal.EnvFields.Where(f => f.IsSecret))
            form.Values.Remove(secret.Key);

        return View("Confirm", new LaunchConfirmModel(
            proposal, form, quote, address.HasAddress ? rootDomain : null, address.Host,
            address.Outcome == AppAddressOutcome.Reserved,
            await EnvironmentsAsync(ct), refusals, secretsToRetype));
    }

    /// <summary>The same places <c>AppsController</c>'s create form offers.</summary>
    private async Task<IReadOnlyList<SelectListItem>> EnvironmentsAsync(CancellationToken ct)
    {
        var query = db.Environments.Where(e => e.WorkspaceId == WorkspaceId);
        if (await access.VisibleProjectIdsAsync(ct) is { } visible)
            query = query.Where(e => visible.Contains(e.ProjectId));

        return await query
            .OrderBy(e => e.Project!.Name).ThenByDescending(e => e.IsDefault).ThenBy(e => e.Name)
            .Select(e => new SelectListItem($"{e.Project!.Name} / {e.Name}", e.Id.ToString()))
            .ToListAsync(ct);
    }

    /// <summary>
    /// <see cref="LaunchView"/> does not say what kind of thing the app turned out to be, and a bot with
    /// no address must not read like a website that lost its own.
    /// </summary>
    private async Task<LaunchProgressModel> ProgressAsync(LaunchView view, CancellationToken ct)
    {
        ServiceKind? kind = null;
        if (view is { IsTerminal: true, AppId: { } appId })
            kind = await db.Apps.AsNoTracking()
                .Where(a => a.Id == appId && a.WorkspaceId == WorkspaceId)
                .Select(a => (ServiceKind?)a.Kind)
                .FirstOrDefaultAsync(ct);

        return new LaunchProgressModel(view, kind);
    }
}
