using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Ai;
using Harbora.Domain.Authorization;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Platform administration of the AI service: providers, their tokens, the model catalogue, plans
/// and who is subscribed to what.
///
/// A provider token is written here and never read back. The form can replace one; it cannot show
/// one. An interface that renders a secret so it can be re-saved is an interface that leaks it into
/// every browser cache, screen recording and support screenshot it ever appears in.
/// </summary>
[Authorize(Policy = Capabilities.PlatformManage)]
[Route("admin/ai")]
public sealed class AiAdminController(
    HarboraDbContext db,
    ISecretProtector protector,
    IAuditLogger audit,
    ISystemClock clock) : Controller
{
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    // ---- providers and their credentials ----

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "AI providers";

        var vm = new AiAdminViewModel
        {
            Providers = await db.AiProviders
                .Include(p => p.Credentials)
                .OrderBy(p => p.Priority).ThenBy(p => p.Name)
                .ToListAsync(ct),
            Models = await db.AiModels.Include(m => m.AiProvider).OrderBy(m => m.Alias).ToListAsync(ct),
            Plans = await db.AiPlans.Include(p => p.Models).OrderBy(p => p.MonthlyPrice).ToListAsync(ct)
        };

        // Health, read from what the router actually wrote rather than probed here: a page that
        // probes on render reports the health of the page load, not of the traffic.
        var since = clock.UtcNow.AddDays(-7);
        vm.RecentFailures = await db.AiUsageRecords.IgnoreQueryFilters()
            .Where(u => u.CreatedAt >= since && u.StatusCode >= 400)
            .OrderByDescending(u => u.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        vm.SpendLast30Days = await db.AiUsageRecords.IgnoreQueryFilters()
            .Where(u => u.CreatedAt >= clock.UtcNow.AddDays(-30))
            .SumAsync(u => u.ProviderCost, ct);

        vm.ChargedLast30Days = await db.AiUsageRecords.IgnoreQueryFilters()
            .Where(u => u.CreatedAt >= clock.UtcNow.AddDays(-30))
            .SumAsync(u => u.ChargedCost, ct);

        vm.RequestsLast30Days = await db.AiUsageRecords.IgnoreQueryFilters()
            .CountAsync(u => u.CreatedAt >= clock.UtcNow.AddDays(-30), ct);

        return View(vm);
    }

    [HttpPost("providers")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveProvider(
        Guid? id, string name, AiProviderType type, string baseUrl,
        int priority, decimal? monthlyBudget, bool isEnabled, CancellationToken ct)
    {
        // Checked before it is stored, not only before it is called: an address that can never be
        // used should be refused where somebody can see why.
        if (Harbora.Infrastructure.Ai.AiUpstreamUrl.Build(baseUrl, "models") is null)
        {
            TempData["Error"] = "That base URL is not usable. It must be HTTPS and must not point inside this network.";
            return RedirectToAction(nameof(Index));
        }

        var provider = id is { } existing
            ? await db.AiProviders.FirstOrDefaultAsync(p => p.Id == existing, ct)
            : null;

        if (provider is null)
        {
            provider = new AiProvider();
            db.AiProviders.Add(provider);
        }

        provider.Name = name.Trim();
        provider.Type = type;
        provider.BaseUrl = baseUrl.Trim();
        provider.Priority = priority;
        provider.MonthlyBudget = monthlyBudget;
        provider.IsEnabled = isEnabled;

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("ai.provider_saved", "ai_provider", provider.Name, ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = $"{provider.Name} saved.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Adds a token. The plaintext is encrypted immediately and is not kept anywhere else — there is
    /// no path in this controller that can return one.
    /// </summary>
    [HttpPost("providers/{providerId:guid}/credentials")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCredential(
        Guid providerId, string label, string token, int priority, int weight, CancellationToken ct)
    {
        var provider = await db.AiProviders.FirstOrDefaultAsync(p => p.Id == providerId, ct);
        if (provider is null) return NotFound();

        if (string.IsNullOrWhiteSpace(token))
        {
            TempData["Error"] = "A token is required.";
            return RedirectToAction(nameof(Index));
        }

        db.AiProviderCredentials.Add(new AiProviderCredential
        {
            AiProviderId = provider.Id,
            Label = string.IsNullOrWhiteSpace(label) ? "token" : label.Trim(),
            EncryptedToken = protector.Protect(token.Trim()),
            Priority = priority,
            Weight = Math.Max(0, weight),
            IsEnabled = true
        });

        await db.SaveChangesAsync(ct);

        // The label, never the token — an audit trail holding provider secrets is a second copy of
        // the thing hardest to rotate.
        await audit.LogAsync("ai.credential_added", "ai_provider", $"{provider.Name}/{label}", ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = "Token added. It cannot be shown again.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Replaces a token in place, keeping its routing settings and health history.</summary>
    [HttpPost("credentials/{id:guid}/rotate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateCredential(Guid id, string token, CancellationToken ct)
    {
        var credential = await db.AiProviderCredentials.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null) return NotFound();

        if (string.IsNullOrWhiteSpace(token))
        {
            TempData["Error"] = "A replacement token is required.";
            return RedirectToAction(nameof(Index));
        }

        credential.EncryptedToken = protector.Protect(token.Trim());

        // A rotated token deserves a clean slate: the failures belonged to the old one, and leaving
        // the circuit open would keep a working token out of rotation.
        credential.ConsecutiveFailures = 0;
        credential.LastFailureReason = null;
        credential.RateLimitedUntil = null;

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("ai.credential_rotated", "ai_credential", credential.Label, ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = $"{credential.Label} was replaced.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("credentials/{id:guid}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleCredential(Guid id, CancellationToken ct)
    {
        var credential = await db.AiProviderCredentials.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null) return NotFound();

        credential.IsEnabled = !credential.IsEnabled;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            credential.IsEnabled ? "ai.credential_enabled" : "ai.credential_disabled",
            "ai_credential", credential.Label, ClientIp, workspaceId: null, ct: ct);

        return RedirectToAction(nameof(Index));
    }

    // ---- model catalogue ----

    [HttpPost("models")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveModel(
        Guid? id, Guid providerId, string alias, string displayName, string providerModelId,
        int? contextLength, int? maxOutputTokens,
        decimal? providerInputPrice, decimal? providerOutputPrice,
        decimal? inputPriceOverride, decimal? outputPriceOverride, decimal markupPercent,
        bool supportsStreaming, bool supportsTools, bool supportsVision, bool supportsEmbeddings,
        bool isEnabled, CancellationToken ct)
    {
        var model = id is { } existing
            ? await db.AiModels.FirstOrDefaultAsync(m => m.Id == existing, ct)
            : null;

        if (model is null)
        {
            model = new AiModel();
            db.AiModels.Add(model);
        }

        model.AiProviderId = providerId;
        model.Alias = alias.Trim();
        model.DisplayName = string.IsNullOrWhiteSpace(displayName) ? alias.Trim() : displayName.Trim();
        model.ProviderModelId = providerModelId.Trim();
        model.ContextLength = contextLength;
        model.MaxOutputTokens = maxOutputTokens;
        model.ProviderInputPrice = providerInputPrice;
        model.ProviderOutputPrice = providerOutputPrice;
        model.InputPriceOverride = inputPriceOverride;
        model.OutputPriceOverride = outputPriceOverride;
        model.MarkupPercent = markupPercent;
        model.SupportsStreaming = supportsStreaming;
        model.SupportsTools = supportsTools;
        model.SupportsVision = supportsVision;
        model.SupportsEmbeddings = supportsEmbeddings;
        model.IsEnabled = isEnabled;

        // Marked so a future registry sync leaves these fields alone. A sync that silently reverts
        // an operator's pricing override is one nobody dares run again.
        model.IsManuallyManaged = true;

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("ai.model_saved", "ai_model", model.Alias, ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = $"{model.Alias} saved.";
        return RedirectToAction(nameof(Index));
    }

    // ---- plans ----

    [HttpPost("plans")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePlan(
        Guid? id, string name, decimal monthlyPrice, decimal includedCredit,
        int requestsPerMinute, int tokensPerMinute, int requestsPerDay,
        long monthlyTokenLimit, decimal? monthlySpendLimit,
        int maxContext, int maxOutputTokens, int concurrentRequests,
        bool allowStreaming, bool hardLimit, bool isEnabled, CancellationToken ct)
    {
        var plan = id is { } existing
            ? await db.AiPlans.FirstOrDefaultAsync(p => p.Id == existing, ct)
            : null;

        if (plan is null)
        {
            plan = new AiPlan();
            db.AiPlans.Add(plan);
        }

        plan.Name = name.Trim();
        plan.MonthlyPrice = monthlyPrice;
        plan.IncludedCredit = includedCredit;
        plan.RequestsPerMinute = requestsPerMinute;
        plan.TokensPerMinute = tokensPerMinute;
        plan.RequestsPerDay = requestsPerDay;
        plan.MonthlyTokenLimit = monthlyTokenLimit;
        plan.MonthlySpendLimit = monthlySpendLimit;
        plan.MaxContext = maxContext;
        plan.MaxOutputTokens = maxOutputTokens;
        plan.ConcurrentRequests = concurrentRequests;
        plan.AllowStreaming = allowStreaming;
        plan.HardLimit = hardLimit;
        plan.IsEnabled = isEnabled;

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("ai.plan_saved", "ai_plan", plan.Name, ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = $"{plan.Name} saved.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Sets which models a plan may use. Replaces the whole set rather than adding to it, so
    /// unticking really removes access — an "add only" form is one where a mistake cannot be undone.
    /// </summary>
    [HttpPost("plans/{planId:guid}/models")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPlanModels(Guid planId, Guid[]? modelIds, CancellationToken ct)
    {
        var plan = await db.AiPlans.Include(p => p.Models).FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null) return NotFound();

        var wanted = (modelIds ?? []).ToHashSet();

        foreach (var existing in plan.Models.Where(m => !wanted.Contains(m.AiModelId)).ToList())
            db.AiPlanModels.Remove(existing);

        foreach (var modelId in wanted.Where(w => plan.Models.All(m => m.AiModelId != w)))
            db.AiPlanModels.Add(new AiPlanModel { AiPlanId = plan.Id, AiModelId = modelId });

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("ai.plan_models_set", "ai_plan", plan.Name, ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = $"{plan.Name} now includes {wanted.Count} model(s).";
        return RedirectToAction(nameof(Index));
    }
}
