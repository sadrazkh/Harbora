using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Functions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harbora.Web.Controllers;

/// <summary>
/// Where a generated function host reports what it just did answering a public call — the door F1's
/// original decision said could not exist because the host has no database (2026-08-21
/// functions-and-services plan follow-up). Anonymous by design, the same shape as
/// <see cref="EventsIngestController"/>: the app id in the URL pins the scope, and the app's own
/// invoke secret — already in the container as <see cref="FunctionProject.SecretEnvVar"/> for the
/// panel's calls in the other direction — proves the caller owns it.
///
/// <para>
/// This door has nothing to prove about latency: the host calls it fire-and-forget, after it has
/// already answered the visitor, and swallows every failure on its own side. A slow or unreachable
/// panel changes nothing about what a customer's own webhook experienced.
/// </para>
/// </summary>
[AllowAnonymous]
[ApiController]
[Route("functions")]
[EnableRateLimiting("webhook")]
public sealed class FunctionInvocationReportController(IFunctionInvocationReportService reports) : ControllerBase
{
    [HttpPost("{appId:guid}/report")]
    public async Task<IActionResult> Report(
        Guid appId, [FromBody] FunctionInvocationReportBody? body, CancellationToken ct)
    {
        var secret = Request.Headers[FunctionProject.SecretHeader].ToString();
        var outcome = await reports.ReportAsync(
            appId, secret.Length == 0 ? null : secret,
            new FunctionInvocationReportRequest(body?.Slug, body?.StatusCode, body?.DurationMs, body?.Error), ct);

        return outcome switch
        {
            FunctionInvocationReportOutcome.Accepted => Ok(),
            FunctionInvocationReportOutcome.UnknownFunction => NotFound(),
            // Deliberately not distinguished from "unknown app id" — the same choice
            // EventsIngestController already makes between its own two failure reasons.
            _ => Unauthorized()
        };
    }
}

/// <summary>What the generated host's own fire-and-forget POST carries.</summary>
public sealed class FunctionInvocationReportBody
{
    public string? Slug { get; set; }
    public int? StatusCode { get; set; }
    public int? DurationMs { get; set; }
    public string? Error { get; set; }
}
