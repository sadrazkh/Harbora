using System.Text.Json;
using System.Text.Json.Serialization;
using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Launch;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Launch;
using Harbora.Shared.Inspection;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers.Api;

/// <summary>
/// "Here is my code, make it work", for a terminal: what <c>harbora launch</c> talks to, and the same
/// thing <see cref="Harbora.Web.Controllers.LaunchController"/> is for a browser. Only the HTTP shape
/// lives here; the launch itself is <see cref="ILaunchService"/>, which judges the caller through the
/// same services the forms use.
///
/// <para>
/// One rule the routes share: <see cref="ILaunchService.GetAsync"/> is the workspace's read, so every
/// route naming a launch asks it first, and another workspace's id is a 404 whatever the route goes on
/// to do.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/launch")]
[Authorize(AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
public sealed class LaunchApiController(
    ILaunchService launches,
    ICurrentUser currentUser,
    IOptions<HarboraRuntimeOptions> runtime) : ControllerBase
{
    /// <summary>
    /// How this API's enums go over the wire, in both directions: by name, and only by name. The panel
    /// has no global JSON configuration, so without this <c>status</c> would be a 6 that the CLI prints
    /// and a client hardcodes, and that means something else the day the enum grows. A number coming in
    /// is refused for the same reason. Local to this controller because the rest of the API keeps its
    /// shape, and <c>[FromBody]</c> would read <see cref="LaunchChoices"/> with the global options,
    /// which is why that one body is read by hand below.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>Starts reading a repository. Nothing is created and nothing is charged here.</summary>
    [HttpPost("")]
    [Authorize(Policy = Capabilities.AppsCreate, AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
    public async Task<IActionResult> Start([FromBody] StartBody? body, CancellationToken ct)
    {
        var outcome = await launches.StartAsync(new LaunchStartRequest
        {
            WorkspaceId = WorkspaceId,
            UserId = currentUser.UserId ?? Guid.Empty,
            Source = LaunchSourceKind.Git,
            CloneUrl = body?.CloneUrl,
            GitRef = body?.GitRef,
            GitToken = body?.GitToken,
            ClientIp = ClientIp
        }, ct);

        return outcome.Ok ? Answer(new { id = outcome.Value }) : Refused(outcome.Refusals);
    }

    /// <summary>
    /// Starts reading a pushed folder: a gzipped tar in the body, streamed straight to disk the way
    /// <see cref="ApiV1Controller.DeployArchive"/> does it, so a large project never has to fit in
    /// memory. A launch gets its name from this file's name, because the path is all it is told, so the
    /// name asked for goes into the file; a blank or unusable one is the launch's plain default.
    /// </summary>
    [HttpPost("archive")]
    [Authorize(Policy = Capabilities.AppsCreate, AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
    [RequestSizeLimit(UploadedArchive.MaxBytes)]
    public async Task<IActionResult> StartFromArchive([FromQuery] string? name, CancellationToken ct)
    {
        if (Request.ContentLength is 0 or null && !Request.Headers.ContainsKey("Transfer-Encoding"))
            return Refused([new ServiceRefusal(
                "Send the project as a gzipped tar in the request body.",
                "پروژه را به صورت یک tar فشرده در بدنه‌ی درخواست بفرست.")]);

        var archivePath = UploadedArchive.PathForLaunch(runtime.Value.WorkDir, $"{name}.tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        try
        {
            await using (var file = System.IO.File.Create(archivePath))
                await Request.Body.CopyToAsync(file, ct);
        }
        catch (Exception)
        {
            // Never leave a half-written archive behind for a launch to trip over.
            UploadedArchive.Delete(archivePath);
            throw;
        }

        ServiceOutcome<Guid> outcome;
        try
        {
            outcome = await launches.StartAsync(new LaunchStartRequest
            {
                WorkspaceId = WorkspaceId,
                UserId = currentUser.UserId ?? Guid.Empty,
                Source = LaunchSourceKind.Upload,
                ArchivePath = archivePath,
                ClientIp = ClientIp
            }, ct);
        }
        catch (Exception)
        {
            // The launch only owns the archive once it exists.
            UploadedArchive.Delete(archivePath);
            throw;
        }

        if (outcome.Ok) return Answer(new { id = outcome.Value });

        UploadedArchive.Delete(archivePath);
        return Refused(outcome.Refusals);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await launches.GetAsync(id, ct) is { } view ? Answer(view) : LaunchNotFound();

    /// <summary>What was found and what would be created. 409 once it is past the point of asking.</summary>
    [HttpGet("{id:guid}/proposal")]
    public async Task<IActionResult> Proposal(Guid id, CancellationToken ct)
    {
        var view = await launches.GetAsync(id, ct);
        if (view is null) return LaunchNotFound();

        return await launches.ProposeAsync(id, ct) is { } proposal
            ? Answer(WithoutExampleValues(proposal))
            : Conflict(new { error = "This launch has no proposal to show: it is not waiting for confirmation.", status = view.Status.ToString() });
    }

    /// <summary>Which folder of a several-app repository this launch is about.</summary>
    [HttpPost("{id:guid}/root")]
    [Authorize(Policy = Capabilities.AppsCreate, AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
    public async Task<IActionResult> ChooseRoot(Guid id, [FromBody] RootBody? body, CancellationToken ct)
    {
        if (await launches.GetAsync(id, ct) is null) return LaunchNotFound();

        var outcome = await launches.ChooseRootAsync(id, body?.Candidate ?? "", ct);
        return outcome.Ok ? Answer(WithoutExampleValues(outcome.Value!)) : Refused(outcome.Refusals);
    }

    /// <summary>
    /// What these choices would cost. Open to anyone in the workspace, like the screen it belongs to: it
    /// spends nothing, and confirming is what is gated.
    /// </summary>
    [HttpPost("{id:guid}/quote")]
    public async Task<IActionResult> Quote(Guid id, CancellationToken ct)
    {
        if (await launches.GetAsync(id, ct) is null) return LaunchNotFound();

        var (choices, unreadable) = await ReadChoicesAsync(ct);
        if (choices is null) return unreadable!;

        var quote = await launches.QuoteAsync(id, choices, ct);

        // The quote, with its figures already written: how many minor units make a major one is the
        // panel's to know (MinorUnits), and a client that divided for itself would be a second copy.
        var perHour = quote.Lines.Sum(l => l.HourlyMinor ?? 0);
        return Answer(new
        {
            quote.Lines,
            quote.FirstHourMinor,
            quote.BalanceMinor,
            quote.Currency,
            quote.Exempt,
            quote.Refusal,
            quote.RefusalIsAboutMoney,
            firstHour = MinorUnits.Format(quote.FirstHourMinor, quote.Currency),
            perHour = MinorUnits.Format(perHour, quote.Currency),
            balance = quote.BalanceMinor is { } balance ? MinorUnits.Format(balance, quote.Currency) : null
        });
    }

    /// <summary>
    /// Creates what was agreed to. A 200 can still be a launch that stopped part-way, because a refusal
    /// from there on is a failed step with what exists named on it rather than "nothing changed": the
    /// answer's <c>status</c> says which.
    /// </summary>
    [HttpPost("{id:guid}/confirm")]
    [Authorize(Policy = Capabilities.AppsCreate, AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken ct)
    {
        if (await launches.GetAsync(id, ct) is null) return LaunchNotFound();

        var (choices, unreadable) = await ReadChoicesAsync(ct);
        if (choices is null) return unreadable!;

        var refusals = await launches.ValidateConfirmAsync(id, choices, ct);
        if (refusals.Count > 0) return Refused(refusals);

        var outcome = await launches.ConfirmAsync(id, choices, ClientIp, ct);
        return outcome.Ok ? Answer(outcome.Value!) : Refused(outcome.Refusals);
    }

    [HttpPost("{id:guid}/retry")]
    [Authorize(Policy = Capabilities.AppsCreate, AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        if (await launches.GetAsync(id, ct) is null) return LaunchNotFound();

        var outcome = await launches.RetryAsync(id, ClientIp, ct);
        return outcome.Ok ? Answer(outcome.Value!) : Refused(outcome.Refusals);
    }

    [HttpPost("{id:guid}/abandon")]
    [Authorize(Policy = Capabilities.AppsCreate, AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
    public async Task<IActionResult> Abandon(Guid id, CancellationToken ct)
    {
        if (await launches.GetAsync(id, ct) is null) return LaunchNotFound();

        return await launches.AbandonAsync(id, ct)
            ? Answer(new { abandoned = true })
            : Refused([new ServiceRefusal(
                "It's too late to call this off: creating has already begun.",
                "دیگر نمی‌شود بی‌خیالش شد؛ ساختن شروع شده است.")]);
    }

    /// <summary>
    /// A proposal with the example file's values taken off everything but a <c>KeepExample</c>. The
    /// inspection carries the file as it was read, and a page only ever draws from it what it means to;
    /// an answer sends all of it. A value on a line the classifier decided a person must answer is
    /// exactly the one somebody might have committed by mistake.
    /// </summary>
    private static LaunchProposal WithoutExampleValues(LaunchProposal proposal) => proposal with
    {
        Inspection = proposal.Inspection with
        {
            EnvVars = proposal.Inspection.EnvVars
                .Select(v => v.Disposition == EnvVarDisposition.KeepExample ? v : v with { ExampleValue = null })
                .ToList()
        }
    };

    /// <summary>
    /// The body, or the answer to give instead of acting on it. Lists and a name that arrive as an
    /// explicit <c>null</c> get past the parser and past the type's own promises, and would otherwise
    /// reach the service as a 500; a blank name it refuses itself, in both languages.
    /// </summary>
    private async Task<(LaunchChoices? Choices, IActionResult? Unreadable)> ReadChoicesAsync(CancellationToken ct)
    {
        try
        {
            var read = await JsonSerializer.DeserializeAsync<LaunchChoices>(Request.Body, WireOptions, ct);
            if (read is not null)
                return (read with
                {
                    Name = read.Name ?? "",
                    Services = read.Services?.OfType<LaunchServiceChoice>().ToList() ?? [],
                    EnvVars = read.EnvVars?.OfType<LaunchEnvChoice>().ToList() ?? []
                }, null);
        }
        catch (JsonException ex)
        {
            return (null, Refused([new ServiceRefusal(
                $"These are not a launch's choices: {ex.Path ?? ex.Message}. Enums go by name.",
                $"این‌ها انتخاب‌های یک راه‌اندازی نیستند: {ex.Path ?? ex.Message}. مقدارهای شمارشی با نامشان فرستاده می‌شوند.")]));
        }

        return (null, Refused([new ServiceRefusal(
            "Send the launch's choices as JSON in the request body.",
            "انتخاب‌های راه‌اندازی را به صورت JSON در بدنه‌ی درخواست بفرست.")]));
    }

    private JsonResult Answer(object value) => new(value, WireOptions);

    private NotFoundObjectResult LaunchNotFound() => NotFound(new { error = "Launch not found." });

    private BadRequestObjectResult Refused(IEnumerable<ServiceRefusal> refusals) => BadRequest(new
    {
        errors = refusals.Select(r => new { field = r.Field, message = r.Message, messageFa = r.MessageFa })
    });

    /// <summary>Body of POST /launch. A blank <paramref name="GitRef"/> reads the default branch.</summary>
    public sealed record StartBody(string? CloneUrl, string? GitRef = null, string? GitToken = null);

    /// <summary>Body of POST /launch/{id}/root.</summary>
    public sealed record RootBody(string? Candidate);
}
