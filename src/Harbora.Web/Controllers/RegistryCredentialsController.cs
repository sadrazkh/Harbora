using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Registries;
using Harbora.Infrastructure.Nodes;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Per-workspace credentials for pulling images off a private container registry (1.3, 2026-09
/// market-gaps round two) — a company's own Harbor, a private Docker Hub repository, GHCR, or any
/// other registry that refuses an anonymous pull.
///
/// <para>
/// Mirrors <see cref="EmailProvidersController"/>/<see cref="StorageController"/> exactly: the secret
/// is encrypted through <see cref="ISecretProtector"/>, revealed only for the one row somebody
/// explicitly clicked to reveal, and a blank secret field on <see cref="Update"/> leaves the stored
/// one untouched. Unlike those two, there is no attach/detach action here — a credential is matched to
/// an image automatically, by registry host, the first time <c>DeploymentPipeline</c> pulls it. See
/// <see cref="RegistryCredential"/>'s own doc for why the unique index on (workspace, host) is what
/// makes that match deterministic, and the Index view for where that rule is explained to the person
/// configuring it, not just in code.
/// </para>
/// </summary>
[Authorize]
[Route("registry-credentials")]
public sealed class RegistryCredentialsController(
    HarboraDbContext db,
    ISecretProtector protector,
    IAuditLogger audit,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? reveal, CancellationToken ct)
    {
        ViewData["Title"] = IsFa ? "اعتبارنامه‌های رجیستری خصوصی" : "Private registry credentials";

        var credentials = await db.RegistryCredentials.AsNoTracking()
            .OrderBy(c => c.RegistryHost)
            .ToListAsync(ct);

        return View(new RegistryCredentialsPageViewModel
        {
            Credentials = credentials.Select(c => new RegistryCredentialViewModel(
                c.Id, c.RegistryHost, c.Username,
                // Revealed only for the one credential asked for, and only on an explicit click — the
                // same rule EmailProvidersController.Index and StorageController.Index apply.
                reveal == c.Id ? Unprotect(c.EncryptedSecret) : null,
                c.UpdatedAt)).ToList(),
            RevealedCredentialId = reveal
        });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Create(string registryHost, string? username, string? secret, CancellationToken ct)
    {
        var host = NormalizeHost(registryHost);
        if (host is null)
            return Back(IsFa ? "نام میزبان رجیستری لازم است." : "A registry host is required.", error: true);

        if (string.IsNullOrWhiteSpace(secret))
            return Back(IsFa ? "رمز یا توکن لازم است." : "A secret or token is required.", error: true);

        // The unique index backstops this at the database; checking here first turns a would-be
        // constraint violation into the named refusal a person can actually act on — "edit the one you
        // already have" rather than a raw 500.
        if (await db.RegistryCredentials.AnyAsync(c => c.WorkspaceId == WorkspaceId && c.RegistryHost == host, ct))
            return Back(IsFa
                ? $"این ورک‌اسپیس از قبل اعتبارنامه‌ای برای «{host}» دارد. برای تغییر آن را ویرایش کنید."
                : $"This workspace already has a credential for '{host}'. Edit it instead of adding a second one.",
                error: true);

        db.RegistryCredentials.Add(new RegistryCredential
        {
            WorkspaceId = WorkspaceId,
            RegistryHost = host,
            Username = username?.Trim() ?? "",
            EncryptedSecret = protector.Protect(secret)
        });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("registry_credential.created", "registry_credential", host, ClientIp, workspaceId: WorkspaceId, ct: ct);

        return Back(IsFa ? $"اعتبارنامه برای «{host}» ساخته شد." : $"A credential for '{host}' was created.");
    }

    /// <summary>Replaces username and, when a new one is typed, the secret — the same "blank means
    /// unchanged" rule <see cref="EmailProvidersController.Update"/> follows. The host is not
    /// editable: it is the whole of what a pull matches this row by, and changing it silently would
    /// leave whatever previously matched it without a credential on the app's very next deploy — the
    /// exact kind of quiet breakage 1.3 exists to replace with a named refusal instead. Rotating to a
    /// different registry means deleting this row and creating a new one for the new host.</summary>
    [HttpPost("{id:guid}/update")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Update(Guid id, string? username, string? secret, CancellationToken ct)
    {
        var credential = await db.RegistryCredentials.FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == WorkspaceId, ct);
        if (credential is null) return NotFound();

        credential.Username = username?.Trim() ?? "";
        // 1.3's rotation rule: a new secret overwrites the ciphertext in place. Nothing else in the
        // platform retains a copy of the old value, and the next deployment that pulls from this
        // registry reads this row fresh — there is no cached or "still live" old credential for a
        // rotation to leave behind.
        if (!string.IsNullOrEmpty(secret)) credential.EncryptedSecret = protector.Protect(secret);

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("registry_credential.updated", "registry_credential", credential.RegistryHost, ClientIp, workspaceId: WorkspaceId, ct: ct);

        return Back(IsFa
            ? $"اعتبارنامه‌ی «{credential.RegistryHost}» به‌روزرسانی شد."
            : $"The credential for '{credential.RegistryHost}' was updated.");
    }

    /// <summary>
    /// Refuses while any app's own image still resolves to this credential's registry host, naming
    /// them — the <c>StorageController.Delete</c> / <c>EmailProvidersController.Delete</c> idiom
    /// (itself the <c>ProjectsController.Delete</c> named-list refusal, reused), so a deleted
    /// credential is never a silent way to break that app's next deploy.
    ///
    /// <para>
    /// Scoped to <see cref="AppSourceType.PrebuiltImage"/> apps — the gap 1.3 closes. A Template app's
    /// pinned image or a Compose service's image can equally reference a private registry, but neither
    /// is stored anywhere this query can see without re-reading a template manifest or a checked-out
    /// compose file, so this check does not follow either. Deleting a credential a Template or Compose
    /// app actually needs still fails honestly at that app's next deploy, by registry name
    /// (<c>RegistryPullDiagnostics</c>) — it is only the pre-emptive named warning here that does not
    /// yet reach those two source types.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var credential = await db.RegistryCredentials.FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == WorkspaceId, ct);
        if (credential is null) return NotFound();

        var dependentApps = await DependentAppsAsync(credential.RegistryHost, ct);
        if (dependentApps.Count > 0)
        {
            return Back(IsFa
                ? $"این اعتبارنامه هنوز برای {NamedList(dependentApps)} استفاده می‌شود. ابتدا تصویر آن اپ‌ها را تغییر دهید یا حذف کنید."
                : $"This credential is still used by {NamedList(dependentApps)}. Change or remove those apps' image first, then delete it.",
                error: true);
        }

        db.RegistryCredentials.Remove(credential);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("registry_credential.deleted", "registry_credential", credential.RegistryHost, ClientIp, workspaceId: WorkspaceId, ct: ct);

        return Back(IsFa ? "اعتبارنامه حذف شد." : "The credential was deleted.");
    }

    /// <summary>Every <see cref="AppSourceType.PrebuiltImage"/> app in this workspace whose image
    /// resolves to the given registry host — computed in memory because
    /// <see cref="ImageDigestResolver.Parse"/> is not translatable to SQL, the same reason
    /// <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline.ResolveRegistryCredentialAsync"/>
    /// reads a candidate row back before comparing rather than filtering the query by host.</summary>
    private async Task<IReadOnlyList<string>> DependentAppsAsync(string registryHost, CancellationToken ct)
    {
        var candidates = await db.Apps.AsNoTracking()
            .Where(a => a.WorkspaceId == WorkspaceId
                && a.SourceType == AppSourceType.PrebuiltImage
                && a.PrebuiltImage != null && a.PrebuiltImage != "")
            .Select(a => new { a.Name, a.PrebuiltImage })
            .ToListAsync(ct);

        return candidates
            .Where(a => ImageDigestResolver.Parse(a.PrebuiltImage!).Registry == registryHost)
            .Select(a => a.Name)
            .ToList();
    }

    /// <summary>Trimmed and lower-cased, so it matches the exact shape
    /// <see cref="ImageDigestResolver.Parse"/> produces for an image's own registry host — the
    /// comparison <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline.ResolveRegistryCredentialAsync"/>
    /// makes at pull time. Null for anything left with nothing usable.</summary>
    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var trimmed = host.Trim().Trim('/').ToLowerInvariant();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>"2 apps: api, worker" — the <c>EmailProvidersController</c>/<c>StorageController</c>
    /// refusal idiom, reused.</summary>
    private string NamedList(IReadOnlyList<string> names)
    {
        const int shown = 3;
        var listed = names.Count > shown
            ? string.Join(IsFa ? "، " : ", ", names.Take(shown)) +
              (IsFa ? $" و {names.Count - shown} مورد دیگر" : $" and {names.Count - shown} more")
            : string.Join(IsFa ? "، " : ", ", names);

        return IsFa ? $"{names.Count} اپ: {listed}" : $"{names.Count} app{(names.Count == 1 ? "" : "s")}: {listed}";
    }

    private string? Unprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch { return null; }
    }

    private IActionResult Back(string? message, bool error = false)
    {
        TempData[error ? "Error" : "Message"] = message;
        return RedirectToAction(nameof(Index));
    }
}
