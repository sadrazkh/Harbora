using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Assistant;

/// <summary>
/// Reads the assistant's configuration and asks the question, given a deployment.
///
/// The order matters and is the whole point: load the service's secrets, redact the log with them,
/// assemble the exact text, and only then send. Nothing here decides on its own that a log is safe.
/// </summary>
public sealed class AssistantService(
    HarboraDbContext db,
    AssistantClient client,
    ISecretProtector protector,
    ILogger<AssistantService> logger)
{
    /// <summary>The configured assistant. Off, with nothing set, is the normal state.</summary>
    public async Task<AssistantConfig> GetConfigAsync(CancellationToken ct)
    {
        var keys = new[]
        {
            AssistantSettingKeys.Enabled, AssistantSettingKeys.Provider,
            AssistantSettingKeys.Model, AssistantSettingKeys.ApiKey, AssistantSettingKeys.BaseUrl
        };

        var settings = await db.Settings.IgnoreQueryFilters()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        return new AssistantConfig(
            Enabled: settings.GetValueOrDefault(AssistantSettingKeys.Enabled) == "true",
            Provider: settings.GetValueOrDefault(AssistantSettingKeys.Provider),
            Model: settings.GetValueOrDefault(AssistantSettingKeys.Model),
            ApiKey: Unprotect(settings.GetValueOrDefault(AssistantSettingKeys.ApiKey)),
            BaseUrl: settings.GetValueOrDefault(AssistantSettingKeys.BaseUrl));
    }

    /// <summary>
    /// Assembles the question for a deployment — without sending it, so it can be shown first.
    /// Returns null when the deployment is not this workspace's to look at.
    /// </summary>
    public async Task<AssistantAsk?> PrepareAsync(Guid deploymentId, CancellationToken ct)
    {
        var deployment = await db.Deployments
            .Include(d => d.App)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
        if (deployment?.App is null) return null;

        // Every secret the service holds, decrypted — a secret is only recognisable in a log as its
        // plaintext. An undecryptable one is passed through as stored rather than dropped: failing to
        // redact something is the dangerous direction.
        var secrets = await db.Set<Domain.Apps.EnvironmentVariable>()
            .Where(v => v.AppId == deployment.AppId && v.IsSecret)
            .Select(v => v.Value)
            .ToListAsync(ct);

        // The log is rows, not a column. Ordered by sequence, and the tail is what will survive
        // trimming — see AssistantRequest.
        var lines = await db.DeploymentLogs
            .Where(l => l.DeploymentId == deploymentId)
            .OrderBy(l => l.Sequence)
            .Select(l => l.Message)
            .ToListAsync(ct);

        return AssistantRequest.ForFailedDeployment(
            string.Join('\n', lines),
            deployment.ErrorMessage,
            deployment.App.Kind.ToString(),
            secrets.Select(Unprotect).Where(s => !string.IsNullOrEmpty(s))!);
    }

    /// <summary>Sends a prepared question. The caller is responsible for having shown it first.</summary>
    public async Task<AssistantAnswer> AskAsync(AssistantAsk ask, CancellationToken ct)
    {
        var config = await GetConfigAsync(ct);

        // Never the prompt itself: it is the one thing here that contains customer data.
        logger.LogInformation("Assistant asked via {Provider}; {Removed} redaction(s) applied.",
            config.Provider, ask.Removed);

        return await client.AskAsync(config, ask, ct);
    }

    /// <summary>
    /// Checks the configuration against the real provider, with a prompt that contains nothing about
    /// this installation.
    ///
    /// Exists because the alternative is finding out the key is wrong at the moment somebody is
    /// already debugging a failed deployment — the provider's 401 then arrives dressed as "the
    /// assistant could not explain this", and the two problems get confused.
    /// </summary>
    public async Task<AssistantAnswer> TestAsync(CancellationToken ct)
    {
        var config = await GetConfigAsync(ct);

        var ask = new AssistantAsk(
            "You are being checked for connectivity. Reply with the single word: ok.",
            "Reply with: ok",
            Removed: 0,
            Truncated: false);

        return await client.AskAsync(config, ask, ct);
    }

    /// <summary>Stores the API key encrypted; everything else is plain.</summary>
    public async Task SaveConfigAsync(
        bool enabled, string? provider, string? model, string? apiKey, string? baseUrl, CancellationToken ct)
    {
        await SetAsync(AssistantSettingKeys.Enabled, enabled ? "true" : "false", secret: false, ct);
        await SetAsync(AssistantSettingKeys.Provider, provider?.Trim().ToLowerInvariant() ?? "", false, ct);
        await SetAsync(AssistantSettingKeys.Model, model?.Trim() ?? "", false, ct);
        await SetAsync(AssistantSettingKeys.BaseUrl, baseUrl?.Trim() ?? "", false, ct);

        // Blank means "leave the stored key alone". The settings form cannot render the key back —
        // so treating an empty box as "clear it" would wipe the key every time anything else is
        // saved. Clearing is deliberate, via its own action.
        if (!string.IsNullOrWhiteSpace(apiKey))
            await SetAsync(AssistantSettingKeys.ApiKey, protector.Protect(apiKey.Trim()), secret: true, ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes the stored key. Separate from saving, because it cannot be undone.</summary>
    public async Task ClearApiKeyAsync(CancellationToken ct)
    {
        await SetAsync(AssistantSettingKeys.ApiKey, "", secret: true, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task SetAsync(string key, string value, bool secret, CancellationToken ct)
    {
        var setting = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) db.Settings.Add(new Setting { Key = key, Value = value, IsSecret = secret });
        else { setting.Value = value; setting.IsSecret = secret; }
    }

    /// <summary>
    /// Decrypts, falling back to the stored text. A value that cannot be decrypted — written before
    /// a key rotation, say — is still more likely to be the secret than not, and failing to redact is
    /// the direction that hurts.
    /// </summary>
    private string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return protector.Unprotect(value); }
        catch { return value; }
    }
}
