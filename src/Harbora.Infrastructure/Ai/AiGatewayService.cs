using System.Collections.Concurrent;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Ai;

/// <summary>Who a request belongs to, once its key has been recognised.</summary>
public sealed record AiCaller(AiUserApiKey Key, AiSubscription Subscription, AiPlan Plan);

/// <summary>A resolved, authorised request ready to forward.</summary>
public sealed record AiRoutedRequest(
    AiCaller Caller, AiModel Model, AiProvider Provider, AiProviderCredential Credential, string Token);

/// <summary>
/// The gateway's decision path: who is this, what may they do, and which token carries it.
///
/// The order is the security design. Authenticate before anything is read from the body, authorise
/// against the plan before a provider is chosen, and check quota before a request that costs money
/// is sent — refusing after the fact means the money is already gone.
/// </summary>
public sealed class AiGatewayService(
    HarboraDbContext db,
    ISecretProtector protector,
    ISystemClock clock,
    ILogger<AiGatewayService> logger)
{
    /// <summary>
    /// In-flight requests per credential, for least-load routing.
    ///
    /// Process-local. On a multi-instance deployment each instance balances its own share, which
    /// spreads load correctly in aggregate without a shared counter on the hot path — and a stale
    /// shared counter routes worse than an honest local one.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, int> InFlight = new();

    /// <summary>
    /// Recognises a key. Returns null for anything unrecognised, revoked, or belonging to a tenant
    /// with no active subscription — all of which are the same answer to the caller: 401.
    /// </summary>
    public async Task<AiCaller?> AuthenticateAsync(string? presentedKey, CancellationToken ct)
    {
        var prefix = AiApiKeys.PrefixOf(presentedKey);
        if (prefix is null) return null;

        // Narrowed by prefix so one candidate is hashed, not every key in the table.
        var candidates = await db.AiUserApiKeys
            .Where(k => k.Prefix == prefix && !k.IsRevoked)
            .ToListAsync(ct);

        var key = candidates.FirstOrDefault(k => AiApiKeys.Verify(presentedKey!, k.KeyHash));
        if (key is null) return null;

        var subscription = await db.AiSubscriptions.IgnoreQueryFilters()
            .Include(s => s.AiPlan).ThenInclude(p => p!.Models)
            .FirstOrDefaultAsync(s => s.WorkspaceId == key.WorkspaceId && s.IsActive, ct);

        if (subscription?.AiPlan is null) return null;

        // Written on a best-effort basis: a failure to stamp "last used" must not fail the request.
        key.LastUsedAt = clock.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not record API key usage."); }

        return new AiCaller(key, subscription, subscription.AiPlan);
    }

    /// <summary>The models this caller may use — exactly what their <c>/v1/models</c> shows.</summary>
    public async Task<IReadOnlyList<AiModel>> ModelsForAsync(AiCaller caller, CancellationToken ct)
    {
        var all = await db.AiModels.Where(m => m.IsEnabled).ToListAsync(ct);
        return AiPlanAccess.ModelsFor(caller.Plan, all);
    }

    /// <summary>
    /// Works out where a request should go, or why it cannot.
    ///
    /// <paramref name="exclude"/> carries credentials already tried in this request, so a retry
    /// lands somewhere new rather than on the token that just failed.
    /// </summary>
    public async Task<(AiRoutedRequest? Routed, AiRefusal? Refusal)> RouteAsync(
        AiCaller caller, string requestedModel, int? maxTokens, bool streaming,
        IReadOnlySet<Guid>? exclude, CancellationToken ct)
    {
        var all = await db.AiModels.Include(m => m.AiProvider).Where(m => m.IsEnabled).ToListAsync(ct);

        var model = AiPlanAccess.Resolve(caller.Plan, all, requestedModel);
        if (AiPlanAccess.Refuse(caller.Plan, model, requestedModel, maxTokens, streaming) is { } refusal)
            return (null, refusal);

        if (AiPlanAccess.RefuseForQuota(caller.Plan, caller.Subscription) is { } quota)
            return (null, quota);

        var provider = model!.AiProvider;
        if (provider is null)
            return (null, new AiRefusal(503, "provider_missing", "That model has no provider configured."));

        var credentials = await db.AiProviderCredentials
            .Where(c => c.AiProviderId == provider.Id)
            .ToListAsync(ct);

        var loads = credentials.ToDictionary(c => c.Id, c => InFlight.GetValueOrDefault(c.Id));

        var (chosen, failure) = AiCredentialRouter.Choose(provider, credentials, loads, clock.UtcNow, exclude);
        if (chosen is null)
            return (null, new AiRefusal(503, "no_capacity", failure?.Reason ?? "No capacity is available."));

        string token;
        try { token = protector.Unprotect(chosen.EncryptedToken); }
        catch (Exception ex)
        {
            // A credential we cannot decrypt is unusable. Parked so routing stops choosing it, and
            // the reason recorded — without the ciphertext.
            logger.LogError(ex, "Provider credential {Credential} could not be decrypted.", chosen.Id);
            AiCredentialRouter.NoteFailure(chosen, clock.UtcNow, "credential could not be decrypted");
            await db.SaveChangesAsync(ct);
            return (null, new AiRefusal(503, "credential_unusable", "No capacity is available."));
        }

        return (new AiRoutedRequest(caller, model, provider, chosen, token), null);
    }

    /// <summary>Marks a credential as carrying one more request.</summary>
    public static IDisposable Occupy(Guid credentialId) => new Occupancy(credentialId);

    /// <summary>
    /// Records what a request cost and moves the subscription's running totals.
    ///
    /// Both happen together: a usage row without the subscription update is a bill nobody is held
    /// to, and a subscription update without the row is a charge nobody can explain.
    /// </summary>
    public async Task MeterAsync(
        AiRoutedRequest routed, long inputTokens, long outputTokens, long cachedTokens,
        int statusCode, int durationMs, bool streaming, bool clientDisconnected,
        string? correlationId, string? failureReason, CancellationToken ct)
    {
        var cost = AiPricing.Calculate(routed.Model, inputTokens, outputTokens, cachedTokens);

        db.AiUsageRecords.Add(new AiUsageRecord
        {
            WorkspaceId = routed.Caller.Key.WorkspaceId,
            UserId = routed.Caller.Key.UserId,
            AiUserApiKeyId = routed.Caller.Key.Id,
            AiPlanId = routed.Caller.Plan.Id,
            RequestedModel = routed.Model.Alias,
            ProviderModelId = routed.Model.ProviderModelId,
            AiProviderId = routed.Provider.Id,
            AiProviderCredentialId = routed.Credential.Id,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CachedInputTokens = cachedTokens,
            ProviderCost = cost.ProviderCost,
            ChargedCost = cost.ChargedCost,
            DurationMs = durationMs,
            StatusCode = statusCode,
            Streaming = streaming,
            ClientDisconnected = clientDisconnected,
            CorrelationId = correlationId,
            FailureReason = failureReason
        });

        var subscription = routed.Caller.Subscription;
        subscription.PeriodTokens += inputTokens + outputTokens;
        subscription.PeriodSpend += cost.ChargedCost;

        routed.Credential.MonthToDateSpend += cost.ProviderCost;

        await db.SaveChangesAsync(ct);
    }

    private sealed class Occupancy : IDisposable
    {
        private readonly Guid _id;
        public Occupancy(Guid id) { _id = id; InFlight.AddOrUpdate(id, 1, (_, n) => n + 1); }

        public void Dispose() => InFlight.AddOrUpdate(_id, 0, (_, n) => Math.Max(0, n - 1));
    }
}
