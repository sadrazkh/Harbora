using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Identity;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Security;

/// <summary>
/// One provider's configuration, secret decrypted and ready to use.
/// </summary>
/// <param name="Provider">One of <see cref="ExternalLoginProviders"/>.</param>
/// <param name="Enabled">The operator's switch. Off is a complete answer, and the default.</param>
/// <param name="ClientId">The application id the provider issued.</param>
/// <param name="ClientSecret">Decrypted at the point of use, never rendered back to a form.</param>
/// <param name="Authority">Generic OIDC only: the issuer whose discovery document describes it.</param>
/// <param name="DisplayName">Generic OIDC only: what to call it on the sign-in page.</param>
public sealed record ExternalProviderConfig(
    string Provider,
    bool Enabled,
    string? ClientId,
    string? ClientSecret,
    string? Authority,
    string? DisplayName)
{
    /// <summary>
    /// Whether a button for this provider may be shown and its scheme challenged.
    ///
    /// <para>
    /// A provider missing its id or secret is not offered at all. The alternative — rendering the
    /// button and letting the handler fail — sends the person to a stack trace on somebody else's
    /// domain, and this codebase has already learned once that a surface must be capable of saying no
    /// before the request rather than after it.
    /// </para>
    /// </summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && (Provider != ExternalLoginProviders.Oidc || !string.IsNullOrWhiteSpace(Authority));
}

/// <summary>Every configured provider at once, in the order the sign-in page offers them.</summary>
public sealed record ExternalLoginConfig(IReadOnlyList<ExternalProviderConfig> Providers)
{
    public ExternalProviderConfig For(string provider) =>
        Providers.FirstOrDefault(p => p.Provider == provider)
        ?? new ExternalProviderConfig(provider, false, null, null, null, null);

    /// <summary>The ones with a button. Empty is the normal state on a fresh install.</summary>
    public IReadOnlyList<ExternalProviderConfig> Offered =>
        Providers.Where(p => p.IsConfigured).ToList();

    public bool Any => Offered.Count > 0;
}

/// <summary>Setting keys for external sign-in. Only the client secret is stored encrypted.</summary>
public static class ExternalLoginSettingKeys
{
    public static string Enabled(string provider) => $"sso.{provider}.enabled";
    public static string ClientId(string provider) => $"sso.{provider}.client_id";
    public static string ClientSecret(string provider) => $"sso.{provider}.client_secret";

    /// <summary>Generic OIDC only.</summary>
    public static string Authority(string provider) => $"sso.{provider}.authority";

    /// <summary>Generic OIDC only.</summary>
    public static string DisplayName(string provider) => $"sso.{provider}.display_name";

    /// <summary>Every key this feature owns, for a single round trip to the settings table.</summary>
    public static IReadOnlyList<string> All() =>
        ExternalLoginProviders.All
            .SelectMany(p => new[] { Enabled(p), ClientId(p), ClientSecret(p), Authority(p), DisplayName(p) })
            .ToList();
}

/// <summary>
/// Reads and writes the provider configuration an operator types into the admin settings page.
///
/// <para>
/// The same shape <see cref="Harbora.Infrastructure.Notifications.PlatformMailer"/> uses for SMTP and
/// the assistant uses for its API key: rows in <c>Settings</c>, read at the point of use, secret
/// through <see cref="ISecretProtector"/>, and a decryption failure reported as "not configured"
/// rather than thrown at whoever happened to open the sign-in page. There is no DB-backed
/// <c>IOptionsMonitor</c> idiom anywhere in this codebase to follow instead.
/// </para>
/// </summary>
public sealed class ExternalLoginSettingsService(
    HarboraDbContext db,
    ISecretProtector protector,
    ILogger<ExternalLoginSettingsService> logger)
{
    public async Task<ExternalLoginConfig> GetAsync(CancellationToken ct)
    {
        var keys = ExternalLoginSettingKeys.All();

        // Platform-level rows: the workspace filter would hide them from an anonymous sign-in page,
        // which has no workspace at all.
        var rows = await db.Settings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        return new ExternalLoginConfig(ExternalLoginProviders.All.Select(provider =>
        {
            var secret = rows.GetValueOrDefault(ExternalLoginSettingKeys.ClientSecret(provider), "");
            if (secret.Length > 0)
            {
                try { secret = protector.Unprotect(secret); }
                catch (Exception e)
                {
                    // A rotated master key losing this secret must read as "this provider is not
                    // configured", not crash the sign-in page for everyone including the people with
                    // passwords.
                    logger.LogWarning(e, "The stored {Provider} client secret could not be decrypted.", provider);
                    secret = "";
                }
            }

            return new ExternalProviderConfig(
                provider,
                string.Equals(rows.GetValueOrDefault(ExternalLoginSettingKeys.Enabled(provider)), "true",
                    StringComparison.OrdinalIgnoreCase),
                Blank(rows.GetValueOrDefault(ExternalLoginSettingKeys.ClientId(provider))),
                Blank(secret),
                Blank(rows.GetValueOrDefault(ExternalLoginSettingKeys.Authority(provider))),
                Blank(rows.GetValueOrDefault(ExternalLoginSettingKeys.DisplayName(provider))));
        }).ToList());
    }

    /// <summary>
    /// Stores one provider. A blank secret keeps the stored one, exactly as the SMTP form does — a
    /// settings page that must be re-fed a secret to save a display name is a settings page that
    /// leaks it into autofill and screen shares.
    /// </summary>
    public async Task SaveAsync(
        string provider, bool enabled, string? clientId, string? clientSecret,
        string? authority, string? displayName, CancellationToken ct)
    {
        var key = ExternalLoginProviders.Normalise(provider)
                  ?? throw new ArgumentException($"Unknown provider '{provider}'.", nameof(provider));

        await WriteAsync(ExternalLoginSettingKeys.Enabled(key), enabled ? "true" : "false", false, ct);
        await WriteAsync(ExternalLoginSettingKeys.ClientId(key), (clientId ?? "").Trim(), false, ct);
        if (!string.IsNullOrWhiteSpace(clientSecret))
            await WriteAsync(ExternalLoginSettingKeys.ClientSecret(key), protector.Protect(clientSecret), true, ct);

        if (key == ExternalLoginProviders.Oidc)
        {
            await WriteAsync(ExternalLoginSettingKeys.Authority(key), (authority ?? "").Trim(), false, ct);
            await WriteAsync(ExternalLoginSettingKeys.DisplayName(key), (displayName ?? "").Trim(), false, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Whether a secret is stored for this provider, without reading it back to a page.</summary>
    public async Task<bool> HasSecretAsync(string provider, CancellationToken ct) =>
        await db.Settings.IgnoreQueryFilters()
            .AnyAsync(s => s.Key == ExternalLoginSettingKeys.ClientSecret(provider) && s.Value != "", ct);

    private async Task WriteAsync(string key, string value, bool isSecret, CancellationToken ct)
    {
        var row = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new Setting { Key = key, IsSecret = isSecret };
            db.Settings.Add(row);
        }

        row.Value = value;
        row.IsSecret = isSecret;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
