namespace Harbora.Application.Abstractions;

/// <summary>
/// Verifies an inbound Git webhook (HMAC signature or shared token), parses the push/tag event,
/// and queues deployments for every app tracking the matching branch/tag. Provider-agnostic:
/// supports GitHub, GitLab, Gitea and a generic custom sender.
/// </summary>
public interface IGitWebhookProcessor
{
    Task<WebhookResult> ProcessAsync(Guid repositoryId, WebhookRequest request, CancellationToken ct);
}

/// <summary>The raw material the controller extracts from the HTTP request.</summary>
public sealed record WebhookRequest(
    string RawBody,
    string? GitHubSignature256,   // X-Hub-Signature-256: "sha256=..."
    string? GiteaSignature,       // X-Gitea-Signature: hex
    string? GitLabToken,          // X-Gitlab-Token
    string? EventName,            // X-GitHub-Event / X-Gitlab-Event / X-Gitea-Event
    string? ProviderHint);        // which header family was present

public sealed record WebhookResult(bool Accepted, string Message, int DeploymentsQueued);
