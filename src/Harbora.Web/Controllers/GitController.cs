using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Harbora.Infrastructure.Git;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Connect Git providers (token-based), import repositories, and surface each repo's webhook
/// URL + secret for deploy-on-push/tag.
/// </summary>
[Authorize]
[Route("git")]
public sealed class GitController(
    HarboraDbContext db,
    IGitProviderClient providerClient,
    IGitOAuthService oauth,
    ISecretProtector protector,
    ICurrentUser currentUser,
    IGitHookService hooks) : Controller
{
    private const string OAuthCookie = "harbora_oauth";
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    /// <param name="reveal">
    /// The one repository whose webhook secret to show. Named as a query parameter deliberately —
    /// see <see cref="AuditController"/> for what happens to a parameter that shares a name with a
    /// route value.
    /// </param>
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] Guid? reveal, CancellationToken ct)
    {
        ViewData["Title"] = "Git";
        var vm = new GitPageViewModel
        {
            Providers = await db.GitProviders.Include(p => p.Repositories)
                .Where(p => p.WorkspaceId == WorkspaceId).ToListAsync(ct),
            WebhookBase = $"{Request.Scheme}://{Request.Host}",
            RevealedRepositoryId = reveal
        };

        // Which provider types have an OAuth app configured (so we can show "Connect with …").
        var configuredKeys = await db.Settings
            .Where(s => s.Key.StartsWith("oauth.") && s.Key.EndsWith(".client_id") && s.Value != "")
            .Select(s => s.Key).ToListAsync(ct);
        ViewBag.OAuthConfigured = configuredKeys
            .Select(k => k.Split('.')[1]) // oauth.{Type}.client_id
            .ToHashSet();
        return View(vm);
    }

    [HttpPost("connect")]
    [Authorize(Policy = Capabilities.GitManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Connect(string name, GitProviderType type, string? apiBaseUrl, string token, CancellationToken ct)
    {
        db.GitProviders.Add(new GitProvider
        {
            WorkspaceId = WorkspaceId,
            Name = string.IsNullOrWhiteSpace(name) ? type.ToString() : name,
            Type = type,
            ApiBaseUrl = apiBaseUrl ?? string.Empty,
            EncryptedCredential = string.IsNullOrWhiteSpace(token) ? null : protector.Protect(token)
        });
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("providers/{id:guid}/repos")]
    public async Task<IActionResult> Repos(Guid id, CancellationToken ct)
    {
        var provider = await db.GitProviders.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (provider is null) return NotFound();

        ViewData["Title"] = provider.Name;
        var vm = new RemoteReposViewModel { Provider = provider };
        try
        {
            var token = provider.EncryptedCredential is { } enc ? protector.Unprotect(enc) : "";
            vm.Repositories = await providerClient.ListRepositoriesAsync(provider.Type, provider.ApiBaseUrl, token, ct);
        }
        catch (Exception ex)
        {
            vm.Error = ex.Message;
        }
        return View(vm);
    }

    [HttpPost("repos")]
    [Authorize(Policy = Capabilities.GitManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportRepo(Guid providerId, string fullName, string cloneUrl, string defaultBranch, CancellationToken ct)
    {
        var owns = await db.GitProviders.AnyAsync(p => p.Id == providerId && p.WorkspaceId == WorkspaceId, ct);
        if (!owns) return NotFound();

        if (!await db.GitRepositories.AnyAsync(r => r.GitProviderId == providerId && r.FullName == fullName, ct))
        {
            db.GitRepositories.Add(new GitRepository
            {
                GitProviderId = providerId,
                FullName = fullName,
                CloneUrl = cloneUrl,
                DefaultBranch = string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch,
                WebhookSecret = Guid.NewGuid().ToString("N")
            });
            await db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Index));
    }

    // --- OAuth authorization-code flow ---

    /// <summary>Save an OAuth app's client id/secret/base for a provider type (secret encrypted).</summary>
    [HttpPost("oauth/config")]
    [Authorize(Policy = Capabilities.GitManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveOAuthConfig(GitProviderType type, string clientId, string clientSecret, string? oauthBase, CancellationToken ct)
    {
        await SetSetting($"oauth.{type}.client_id", clientId, secret: false, ct);
        if (!string.IsNullOrWhiteSpace(clientSecret))
            await SetSetting($"oauth.{type}.client_secret", protector.Protect(clientSecret), secret: true, ct);
        await SetSetting($"oauth.{type}.base", oauthBase ?? "", secret: false, ct);
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("oauth/{type}/start")]
    [Authorize(Policy = Capabilities.GitManage)]
    public async Task<IActionResult> OAuthStart(GitProviderType type, CancellationToken ct)
    {
        var clientId = await GetSetting($"oauth.{type}.client_id", ct);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            TempData["Error"] = $"Configure the {type} OAuth app first.";
            return RedirectToAction(nameof(Index));
        }
        var oauthBase = await GetSetting($"oauth.{type}.base", ct) ?? oauth.DefaultOAuthBase(type);
        var state = Guid.NewGuid().ToString("N");
        var redirectUri = $"{Request.Scheme}://{Request.Host}/git/oauth/callback";

        // Stash flow state in an encrypted, http-only cookie to validate on callback.
        Response.Cookies.Append(OAuthCookie,
            protector.Protect(System.Text.Json.JsonSerializer.Serialize(new OAuthState((int)type, oauthBase, state))),
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.AddMinutes(10) });

        return Redirect(oauth.BuildAuthorizeUrl(type, oauthBase, clientId, redirectUri, state));
    }

    [HttpGet("oauth/callback")]
    public async Task<IActionResult> OAuthCallback(string code, string state, CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue(OAuthCookie, out var raw))
        {
            TempData["Error"] = "OAuth session expired. Please try again.";
            return RedirectToAction(nameof(Index));
        }
        Response.Cookies.Delete(OAuthCookie);

        OAuthState flow;
        try { flow = System.Text.Json.JsonSerializer.Deserialize<OAuthState>(protector.Unprotect(raw))!; }
        catch { TempData["Error"] = "Invalid OAuth state."; return RedirectToAction(nameof(Index)); }

        if (flow.State != state) { TempData["Error"] = "OAuth state mismatch."; return RedirectToAction(nameof(Index)); }

        var type = (GitProviderType)flow.Type;
        var clientId = await GetSetting($"oauth.{type}.client_id", ct) ?? "";
        var clientSecret = await GetSetting($"oauth.{type}.client_secret", ct) is { } enc ? SafeUnprotect(enc) : "";
        var redirectUri = $"{Request.Scheme}://{Request.Host}/git/oauth/callback";

        try
        {
            var token = await oauth.ExchangeCodeAsync(type, flow.OAuthBase, clientId, clientSecret, code, redirectUri, ct);
            db.GitProviders.Add(new GitProvider
            {
                WorkspaceId = WorkspaceId,
                Name = $"{type} (OAuth)",
                Type = type,
                ApiBaseUrl = type == GitProviderType.GitHub ? "" : flow.OAuthBase,
                EncryptedCredential = protector.Protect(token)
            });
            await db.SaveChangesAsync(ct);
            TempData["Message"] = $"Connected {type} via OAuth.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"OAuth failed: {ex.Message}";
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// A new secret. On a connected account's repository the hook Harbora set up is given it in the same
    /// press, or every push after this would be refused as unsigned while the page still said they deploy.
    /// </summary>
    [HttpPost("repos/{id:guid}/rotate-secret")]
    [Authorize(Policy = Capabilities.GitManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateSecret(Guid id, CancellationToken ct)
    {
        var repo = await db.GitRepositories.Include(r => r.Provider)
            .FirstOrDefaultAsync(r => r.Id == id && r.Provider!.WorkspaceId == WorkspaceId, ct);
        if (repo is not null)
        {
            repo.WebhookSecret = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync(ct);

            if (repo.Provider is { } account && GitAccounts.Connectable.Contains(account.Type))
                Say(await hooks.EnsureAsync(repo.Id, ct));
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Sets the push webhook up on the provider, or finds it there and gives it the current secret: the
    /// app page's button for a repository of a connected account. Pressed twice, it still leaves one hook.
    /// </summary>
    /// <param name="back">A path on this panel to return to, the app page that has the button.</param>
    [HttpPost("repos/{id:guid}/webhook")]
    [Authorize(Policy = Capabilities.GitManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetUpWebhook(Guid id, [FromForm] string? back, CancellationToken ct)
    {
        var owned = await db.GitRepositories.AnyAsync(r => r.Id == id && r.Provider!.WorkspaceId == WorkspaceId, ct);
        if (!owned) return NotFound();

        Say(await hooks.EnsureAsync(id, ct));
        return !string.IsNullOrEmpty(back) && Url.IsLocalUrl(back) ? LocalRedirect(back) : RedirectToAction(nameof(Index));
    }

    private void Say(GitHookOutcome outcome) =>
        TempData[outcome.IsConfirmed ? "Message" : "Error"] = IsFa ? outcome.MessageFa : outcome.Message;

    private async Task<string?> GetSetting(string key, CancellationToken ct) =>
        await db.Settings.Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);

    private async Task SetSetting(string key, string value, bool secret, CancellationToken ct)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) db.Settings.Add(new Harbora.Domain.Settings.Setting { Key = key, Value = value, IsSecret = secret });
        else { setting.Value = value; setting.IsSecret = secret; }
    }

    private string SafeUnprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch { return string.Empty; }
    }

    private sealed record OAuthState(int Type, string OAuthBase, string State);
}
