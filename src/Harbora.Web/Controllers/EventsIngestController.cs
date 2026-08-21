using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Functions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harbora.Web.Controllers;

/// <summary>
/// Where a customer's own function-app code raises a custom event (F3, 2026-08-21
/// functions-and-services plan, "Custom events from customer apps"). Anonymous by design, the same
/// shape as <see cref="WebhooksController"/>: the app id in the URL pins the scope, and the app's own
/// invoke secret — <see cref="FunctionProject.SecretEnvVar"/> in its container, already there for the
/// panel's calls in the other direction — proves the caller owns it. No session, no cookie, no bearer
/// token to mint or rotate.
/// </summary>
[AllowAnonymous]
[ApiController]
[Route("events")]
[EnableRateLimiting("webhook")]
public sealed class EventsIngestController(ICustomEventIngestService ingest) : ControllerBase
{
    [HttpPost("ingest/{appId:guid}")]
    public async Task<IActionResult> Ingest(Guid appId, [FromBody] CustomEventIngestBody? body, CancellationToken ct)
    {
        var secret = Request.Headers[FunctionProject.SecretHeader].ToString();
        var result = await ingest.IngestAsync(
            appId, secret.Length == 0 ? null : secret,
            new CustomEventIngestRequest(body?.Key, body?.Subject, body?.Data), ct);

        return result.Outcome switch
        {
            CustomEventIngestOutcome.Accepted => Ok(new { key = result.Key }),
            CustomEventIngestOutcome.InvalidKey => BadRequest(new
            {
                message = "Give the event a usable key — letters, digits, dots, underscores or hyphens."
            }),
            // Deliberately not distinguished from "unknown app id" — the same choice
            // WebhooksController already makes between "unknown repository" and "bad signature" for
            // its own two failure reasons, folded into a message here instead of a second one.
            _ => Unauthorized(new { message = "This app id and secret do not match." })
        };
    }
}

/// <summary>What the POST body carries. <c>Data</c> is flat and already whatever the caller wants a
/// function to see as <c>event.data</c> — no schema, per the plan's own scope: this is an ingest
/// endpoint and a seen-keys list, not a registry.</summary>
public sealed class CustomEventIngestBody
{
    public string? Key { get; set; }
    public string? Subject { get; set; }
    public Dictionary<string, string?>? Data { get; set; }
}
