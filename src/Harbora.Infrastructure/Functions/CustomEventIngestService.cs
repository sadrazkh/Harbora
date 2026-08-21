using System.Security.Cryptography;
using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Functions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Functions;

/// <summary>
/// Verifies a custom event against the app it claims to come from and hands it to
/// <see cref="IFunctionEventBus"/> — the same bus every platform event already goes through, per the
/// plan's own decision: no new event bus, custom events ride the existing plumbing.
///
/// <para>
/// Authenticity is <see cref="App.FunctionInvokeSecret"/> in the direction it was never used before —
/// the same secret the generated host already checks when the panel calls in
/// (<see cref="FunctionProject.SecretHeader"/> / <see cref="FunctionProject.SecretEnvVar"/>), compared
/// here the other way round. An app only ever knows its own secret, so an app id in the URL with the
/// wrong secret — or a workspace's own secret against another workspace's app id — both fail the same
/// comparison, before <see cref="App.WorkspaceId"/> is ever trusted for anything.
/// </para>
/// </summary>
public sealed class CustomEventIngestService(
    HarboraDbContext db,
    ISecretProtector protector,
    IFunctionEventBus bus,
    ILogger<CustomEventIngestService> logger) : ICustomEventIngestService
{
    public async Task<CustomEventIngestResult> IngestAsync(
        Guid appId, string? providedSecret, CustomEventIngestRequest request, CancellationToken ct)
    {
        // Anonymous door, same trap as GitWebhookProcessor: no session means the tenant filter hides
        // every row, including the one whose own secret is about to prove it. The scope is pinned by
        // the app id in the URL instead, exactly like the repository id there.
        var app = await db.Apps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == appId && a.SourceType == AppSourceType.InlineCode, ct);
        if (app is null) return new(CustomEventIngestOutcome.Unauthorized);

        var secret = SafeUnprotect(app.FunctionInvokeSecret);
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(providedSecret) ||
            !FixedEquals(providedSecret, secret))
        {
            logger.LogWarning("Rejected a custom event for app {App}: bad or missing invoke secret.", appId);
            return new(CustomEventIngestOutcome.Unauthorized);
        }

        var key = FunctionEvents.NormaliseCustomKey(request.Key);
        if (key is null) return new(CustomEventIngestOutcome.InvalidKey);

        // Recorded before publishing, and unconditionally on every ingest — including the far more
        // common case where nothing has subscribed yet. This is what keeps an unsubscribed key from
        // vanishing behind the 200 below: it is why a workspace can go find "custom.order.paid" was
        // received at all, and go subscribe a function to it.
        await RecordSeenAsync(app.WorkspaceId, key, ct);

        await bus.PublishAsync(
            new FunctionEvent(key, app.WorkspaceId, request.Subject?.Trim(), request.Data ?? new Dictionary<string, string?>()),
            ct);

        return new(CustomEventIngestOutcome.Accepted, key);
    }

    private async Task RecordSeenAsync(Guid workspaceId, string key, CancellationToken ct)
    {
        // Same IgnoreQueryFilters reasoning as the app lookup above — this call has already proven
        // which workspace it belongs to via the secret; the ambient (nonexistent) session scope must
        // not be allowed to hide the row that fact should update.
        var seen = await db.FunctionCustomEventKeys.IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.WorkspaceId == workspaceId && k.Key == key, ct);

        if (seen is null)
        {
            db.FunctionCustomEventKeys.Add(new FunctionCustomEventKey
            {
                WorkspaceId = workspaceId, Key = key, TimesSeen = 1
            });
        }
        else
        {
            seen.TimesSeen++;
            seen.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private string? SafeUnprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try { return protector.Unprotect(ciphertext); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A function app's invoke secret could not be decrypted.");
            return null;
        }
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
