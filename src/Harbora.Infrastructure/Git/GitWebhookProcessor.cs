using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Auditing;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Harbora.Infrastructure.Projects;

namespace Harbora.Infrastructure.Git;

/// <summary>
/// Verifies inbound Git webhooks and queues deploys. GitHub/Gitea sign the body with HMAC-SHA256
/// (compared in constant time); GitLab sends a shared token. A push to a tracked branch or a tag
/// matching an app's pattern triggers a deployment carrying the pushed commit.
/// </summary>
public sealed class GitWebhookProcessor(
    HarboraDbContext db,
    IDeploymentEngine deployEngine,
    Projects.PreviewEnvironmentService previews,
    ILogger<GitWebhookProcessor> logger) : IGitWebhookProcessor
{
    public async Task<WebhookResult> ProcessAsync(Guid repositoryId, WebhookRequest request, CancellationToken ct)
    {
        var repo = await db.GitRepositories.Include(r => r.Provider)
            .FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repo is null) return new WebhookResult(false, "Unknown repository.", 0);

        if (!Verify(request, repo.WebhookSecret))
        {
            logger.LogWarning("Rejected webhook for repo {Repo}: bad signature.", repo.FullName);
            return new WebhookResult(false, "Signature verification failed.", 0);
        }

        if (!TryParse(request.RawBody, out var ev))
            return new WebhookResult(true, "Event ignored (no ref).", 0);

        var apps = await db.Apps.Include(a => a.GitRepository)
            .Where(a => a.GitRepositoryId == repositoryId).ToListAsync(ct);

        var queued = 0;
        foreach (var app in apps)
        {
            // A branch that is not the tracked one belongs to previews, if this app wants them.
            if (!ev.IsTag && PreviewPolicy.ShouldPreview(
                    app.PreviewsEnabled, ev.RefName, ev.IsTag, app.GitRef ?? app.GitRepository?.DefaultBranch))
            {
                queued += await HandlePreviewAsync(app, ev, ct);
                continue;
            }

            var trigger = ev.IsTag ? DeploymentTrigger.GitTag : DeploymentTrigger.GitPush;
            var shouldDeploy = ev.IsTag
                ? !string.IsNullOrWhiteSpace(app.DeployOnTagPattern) && GlobMatch(app.DeployOnTagPattern!, ev.RefName)
                : app.AutoDeployOnPush &&
                  string.Equals(app.GitRef ?? app.GitRepository?.DefaultBranch, ev.RefName, StringComparison.OrdinalIgnoreCase);

            if (!shouldDeploy) continue;

            try
            {
                await deployEngine.QueueDeploymentAsync(
                    new DeploymentRequest(app.Id, trigger, Guid.Empty, ev.RefName, ev.Sha), ct);
                queued++;
            }
            catch (InvalidOperationException)
            {
                // A rollback is mid-flight for this app — skip it rather than failing the whole
                // webhook (other apps in the same push must still deploy). The push is audited below.
            }
        }

        db.AuditLogs.Add(new AuditLog
        {
            ActorEmail = "webhook",
            Action = ev.IsTag ? "git.tag" : "git.push",
            TargetType = "GitRepository",
            TargetId = repositoryId.ToString(),
            MetadataJson = JsonSerializer.Serialize(new { ev.RefName, ev.Sha, queued })
        });
        await db.SaveChangesAsync(ct);

        return new WebhookResult(true, $"Queued {queued} deployment(s) for {ev.RefName}.", queued);
    }

    /// <summary>
    /// Creates or refreshes the preview of a branch, or takes it away when the branch is deleted.
    /// Failures are logged and swallowed: one branch's preview must not fail the whole webhook and
    /// stop the other apps in the push from deploying.
    /// </summary>
    private async Task<int> HandlePreviewAsync(Domain.Apps.App app, PushEvent ev, CancellationToken ct)
    {
        try
        {
            if (ev.Deleted)
            {
                if (await previews.FindAsync(app.Id, ev.RefName, ct) is { } existing)
                    await previews.RemoveAsync(existing.Id, ct);
                return 0;
            }

            var deployment = await previews.EnsureAsync(app, ev.RefName, ev.Sha, ct);
            return deployment is null ? 0 : 1;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Preview for {Branch} could not be handled.", ev.RefName);
            return 0;
        }
    }

    // --- verification ---

    private static bool Verify(WebhookRequest request, string secret)
    {
        // GitLab: shared-token equality.
        if (!string.IsNullOrEmpty(request.GitLabToken))
            return FixedEquals(request.GitLabToken, secret);

        // GitHub / Gitea / custom: HMAC-SHA256 over the raw body.
        var provided = request.GitHubSignature256?.Replace("sha256=", "") ?? request.GiteaSignature;
        if (string.IsNullOrEmpty(provided)) return false;

        var computed = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.RawBody)))
            .ToLowerInvariant();

        return FixedEquals(provided.Trim().ToLowerInvariant(), computed);
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // --- parsing ---

    private static bool TryParse(string body, out PushEvent ev)
    {
        ev = default!;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ref", out var refProp) || refProp.ValueKind != JsonValueKind.String)
                return false;

            var fullRef = refProp.GetString()!;
            var isTag = fullRef.StartsWith("refs/tags/", StringComparison.Ordinal);
            var name = fullRef
                .Replace("refs/heads/", "", StringComparison.Ordinal)
                .Replace("refs/tags/", "", StringComparison.Ordinal);

            string? sha = TryString(root, "after") ?? TryString(root, "checkout_sha");
            if (root.TryGetProperty("head_commit", out var hc) && hc.ValueKind == JsonValueKind.Object)
                sha ??= TryString(hc, "id");

            // A deleted branch arrives as a push whose "deleted" flag is set — the only signal that
            // a preview should be taken away rather than redeployed.
            var deleted = root.TryGetProperty("deleted", out var del) && del.ValueKind == JsonValueKind.True;

            ev = new PushEvent(name, isTag, sha, deleted);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string? TryString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Simple glob (* and ?) match used for tag patterns like "v*".</summary>
    private static bool GlobMatch(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private readonly record struct PushEvent(string RefName, bool IsTag, string? Sha, bool Deleted);
}
