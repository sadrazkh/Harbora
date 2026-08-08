using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Opening a managed database to something outside Harbora.
///
/// Every action here returns the page directly instead of redirecting, which is unusual and
/// deliberate: a password is shown once, and the redirect-after-post pattern would have to carry it
/// through TempData. TempData in this application is cookie-backed, so that would write a live
/// database password into the customer's browser cookie jar, where it survives the page they were
/// warned to copy it from.
/// </summary>
public sealed partial class DatabasesController
{
    [HttpGet("{id:guid}/access")]
    public async Task<IActionResult> Access(Guid id, CancellationToken ct)
    {
        if (!await access.CanSeeServiceAsync(id, ct)) return NotFound();

        var page = await BuildAccessPageAsync(id, ct);
        return page is null ? NotFound() : View("Access", page);
    }

    [HttpPost("{id:guid}/access")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> IssueAccess(
        Guid id, DatabaseAccessKind kind, int minutes, string? allowedIps, CancellationToken ct)
    {
        await Guard(id, ct);

        var service = await FindDatabaseAsync(id, ct);
        if (service is null) return NotFound();

        // Asked again here and not only when the page was drawn: the database may have stopped, or
        // the agent may have gone away, in the minutes since somebody opened the form.
        if (ExternalAccessAvailability.Refuse(node, service, databaseAccess.CanOpenLocally) is { } unavailable)
            return View("Access", await BuildAccessPageAsync(id, ct, error: IsFa ? unavailable.ReasonFa : unavailable.Reason));

        var result = await databaseAccess.IssueAsync(
            id,
            kind,
            kind == DatabaseAccessKind.Temporary ? TimeSpan.FromMinutes(minutes) : null,
            string.IsNullOrWhiteSpace(allowedIps) ? null : allowedIps.Trim(),
            currentUser.UserId,
            User.Identity?.Name,
            ct);

        if (!result.Ok)
            return View("Access", await BuildAccessPageAsync(id, ct, error: result.Error));

        var issued = result.Issued!;
        return View("Access", await BuildAccessPageAsync(id, ct, issued: new IssuedCredentialViewModel(
            issued.Grant.Username, issued.Password, issued.ConnectionString, issued.Grant.ExpiresAt, Rotated: false)));
    }

    [HttpPost("{id:guid}/access/{grantId:guid}/extend")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> ExtendAccess(Guid id, Guid grantId, int minutes, CancellationToken ct)
    {
        await Guard(id, ct);

        var grant = await FindGrantAsync(id, grantId, ct);
        if (grant is null) return NotFound();

        var error = await databaseAccess.ExtendAsync(grant, TimeSpan.FromMinutes(minutes), User.Identity?.Name, ct);
        return View("Access", await BuildAccessPageAsync(id, ct,
            error: error,
            message: error is null ? (IsFa ? "مهلت تمدید شد." : "The window was extended.") : null));
    }

    [HttpPost("{id:guid}/access/{grantId:guid}/revoke")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> RevokeAccess(Guid id, Guid grantId, CancellationToken ct)
    {
        await Guard(id, ct);

        var grant = await FindGrantAsync(id, grantId, ct);
        if (grant is null) return NotFound();

        // Closed even when the agent is simulated. Refusing here would leave a row nobody can shut,
        // and a grant that cannot be revoked is worse than one that was never issued.
        await databaseAccess.CloseAsync(
            grant, DatabaseAccessStatus.Revoked, "Revoked from the panel.", User.Identity?.Name, ct);

        return View("Access", await BuildAccessPageAsync(id, ct,
            message: IsFa ? "دسترسی بسته شد." : "The access was closed."));
    }

    [HttpPost("{id:guid}/access/{grantId:guid}/rotate")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> RotateAccess(Guid id, Guid grantId, CancellationToken ct)
    {
        await Guard(id, ct);

        var grant = await FindGrantAsync(id, grantId, ct);
        if (grant is null) return NotFound();

        var service = await FindDatabaseAsync(id, ct);
        if (ExternalAccessAvailability.Refuse(node, service, databaseAccess.CanOpenLocally) is { } unavailable)
            return View("Access", await BuildAccessPageAsync(id, ct, error: IsFa ? unavailable.ReasonFa : unavailable.Reason));

        var (password, error) = await databaseAccess.RotateAsync(grant, User.Identity?.Name, ct);
        if (password is null)
            return View("Access", await BuildAccessPageAsync(id, ct, error: error));

        var connection = service is null || grant.GatewayHost is null || grant.GatewayPort is null
            ? null
            : DatabaseCredentialManager.ConnectionString(
                service.Type.ToString(), grant.GatewayHost, grant.GatewayPort.Value,
                grant.Username, password, service.DatabaseName,
                service.TlsEnabled ? DatabaseTls.ConnectionParameter(service.Type) : null);

        // A password *and* an error is the half-finished rotation: the database took the new
        // password and Harbora could not record it, or never heard whether it did. Both are shown —
        // the banner because the old password may be dead, the panel because this page is the only
        // place the new one will ever exist.
        return View("Access", await BuildAccessPageAsync(id, ct, error: error, issued: new IssuedCredentialViewModel(
            grant.Username, password, connection, grant.ExpiresAt, Rotated: true)));
    }

    // ---- helpers ----

    private Task<ManagedService?> FindDatabaseAsync(Guid id, CancellationToken ct) =>
        db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);

    /// <summary>
    /// The grant, scoped to the database in the route as well as to the workspace.
    ///
    /// Both checks matter: without the first, a grant id from one database could be revoked or
    /// rotated through another database's URL, and the audit trail would record it against the
    /// wrong service.
    /// </summary>
    private Task<DatabaseAccessGrant?> FindGrantAsync(Guid serviceId, Guid grantId, CancellationToken ct) =>
        db.DatabaseAccessGrants.FirstOrDefaultAsync(
            g => g.Id == grantId && g.ManagedServiceId == serviceId && g.WorkspaceId == WorkspaceId, ct);

    private async Task<DatabaseAccessPageViewModel?> BuildAccessPageAsync(
        Guid id, CancellationToken ct,
        string? error = null, string? message = null, IssuedCredentialViewModel? issued = null)
    {
        var service = await FindDatabaseAsync(id, ct);
        if (service is null) return null;

        ViewData["Title"] = service.Name;

        return new DatabaseAccessPageViewModel
        {
            Database = service,
            Unavailable = ExternalAccessAvailability.Refuse(node, service, databaseAccess.CanOpenLocally),

            // Closed grants stay listed. "Who opened this database in March, and for how long" is a
            // question that gets asked, and a list that only shows what is open cannot answer it.
            Grants = await db.DatabaseAccessGrants
                .Where(g => g.ManagedServiceId == id && g.WorkspaceId == WorkspaceId)
                .OrderByDescending(g => g.CreatedAt)
                .Take(50)
                .ToListAsync(ct),

            History = await db.DatabaseAccessAudits
                .Where(a => a.ManagedServiceId == id && a.WorkspaceId == WorkspaceId)
                .OrderByDescending(a => a.CreatedAt)
                .Take(20)
                .ToListAsync(ct),

            Issued = issued,
            Error = error,
            Message = message
        };
    }
}
