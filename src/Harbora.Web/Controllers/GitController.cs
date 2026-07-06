using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
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
    ISecretProtector protector,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Git";
        var vm = new GitPageViewModel
        {
            Providers = await db.GitProviders.Include(p => p.Repositories)
                .Where(p => p.WorkspaceId == WorkspaceId).ToListAsync(ct),
            WebhookBase = $"{Request.Scheme}://{Request.Host}"
        };
        return View(vm);
    }

    [HttpPost("connect")]
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

    [HttpPost("repos/{id:guid}/rotate-secret")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateSecret(Guid id, CancellationToken ct)
    {
        var repo = await db.GitRepositories
            .FirstOrDefaultAsync(r => r.Id == id && r.Provider!.WorkspaceId == WorkspaceId, ct);
        if (repo is not null)
        {
            repo.WebhookSecret = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Index));
    }
}
