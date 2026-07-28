using Harbora.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harbora.Web.Controllers;

/// <summary>
/// Public webhook endpoint that Git providers POST to. Anonymous by design — authenticity is
/// established by the per-repository HMAC signature / shared token, not by a session.
/// </summary>
[AllowAnonymous]
[ApiController]
[Route("webhooks")]
[EnableRateLimiting("webhook")]
public sealed class WebhooksController(IGitWebhookProcessor processor) : ControllerBase
{
    [HttpPost("git/{repositoryId:guid}")]
    public async Task<IActionResult> Git(Guid repositoryId, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);

        var request = new WebhookRequest(
            RawBody: body,
            GitHubSignature256: Header("X-Hub-Signature-256"),
            GiteaSignature: Header("X-Gitea-Signature"),
            GitLabToken: Header("X-Gitlab-Token"),
            EventName: Header("X-GitHub-Event") ?? Header("X-Gitlab-Event") ?? Header("X-Gitea-Event"),
            ProviderHint: null);

        var result = await processor.ProcessAsync(repositoryId, request, ct);
        return result.Accepted
            ? Ok(new { result.Message, result.DeploymentsQueued })
            : Unauthorized(new { result.Message });
    }

    private string? Header(string name) =>
        Request.Headers.TryGetValue(name, out var v) ? v.ToString() : null;
}
