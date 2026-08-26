using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        if (ExternalAccessAvailability.Refuse(
                node, service, databaseAccess.CanOpenLocally, await RunsElsewhereAsync(service, ct))
            is { } unavailable)
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
        var runsElsewhere = service is not null && await RunsElsewhereAsync(service, ct);
        if (ExternalAccessAvailability.Refuse(node, service, databaseAccess.CanOpenLocally, runsElsewhere) is { } unavailable)
            return View("Access", await BuildAccessPageAsync(id, ct, error: IsFa ? unavailable.ReasonFa : unavailable.Reason));

        // Refuse() answers for a database that is not there, so past that line there is one. Said
        // out loud rather than inferred, because everything below produces a password and needs
        // somewhere to put it that does not depend on reading anything again.
        if (service is null) return NotFound();

        var (password, error) = await databaseAccess.RotateAsync(grant, User.Identity?.Name, ct);
        if (password is null)
            return View("Access", await BuildAccessPageAsync(id, ct, error: error));

        var connection = grant.GatewayHost is null || grant.GatewayPort is null
            ? null
            : DatabaseCredentialManager.ConnectionString(
                service.Type.ToString(), grant.GatewayHost, grant.GatewayPort.Value,
                grant.Username, password, service.DatabaseName,
                service.TlsEnabled ? DatabaseTls.ConnectionParameter(service.Type) : null);

        // A password *and* an error is the half-finished rotation: the database took the new
        // password and Harbora could not record it, or never heard whether it did. Both are shown —
        // the banner because the old password may be dead, the panel because this page is the only
        // place the new one will ever exist.
        var issued = new IssuedCredentialViewModel(
            grant.Username, password, connection, grant.ExpiresAt, Rotated: true);

        try
        {
            // Null rather than thrown: the database row was deleted between the read at the top of
            // this action and this rebuild. Not an exception, so the catch below never sees it, and
            // Access.cshtml dereferences Model.Database on its first line — falling through with
            // null would lose the password to a NullReferenceException instead of a database one.
            if (await BuildAccessPageAsync(id, ct, error: error, issued: issued) is { } page)
                return View("Access", page);
        }
        catch (Exception ex)
        {
            // Rebuilding the page reads the same connection the rotation just wrote through, and the
            // failures worth surviving here are precisely the ones that take both: a dropped
            // connection, a failover, an exhausted pool. Unguarded, those reads throw, MVC serves
            // /Home/Error, and the password is destroyed by the second failure rather than the first
            // — the exact end state the whole path above exists to prevent.
            //
            // Logged here rather than relying on RotateAsync's log: this also covers the arm where
            // the save *succeeded* and the read then failed, which nothing else in the request has
            // anything to say about. Resolved off the request rather than taken in the constructor,
            // which is thirteen arguments long already.
            HttpContext.RequestServices.GetRequiredService<ILogger<DatabasesController>>()
                .LogError(ex, "The access page could not be rebuilt after rotating grant {Grant}.", grant.Id);
        }

        // Everything this render needs is already in this request's hands, so it is drawn without
        // asking the database anything — and through AccessRecovered.cshtml, which sets Layout to
        // null. That last part is not cosmetic: Razor runs after this method returns, so no try here
        // can protect a render, and _ViewStart wraps every other view in _Layout, whose sidebar
        // reads db.Users and db.Settings and whose topbar reads db.Environments — three more reads
        // on the context that has just stopped answering, all of them emitted *before* RenderBody,
        // so the password would already be buffered behind the partial that threw.
        return View("AccessRecovered", FromMemory(service, grant, issued, error));
    }

    // ---- helpers ----

    /// <summary>
    /// The access page built out of what the request is already holding, for the moment Harbora's
    /// own database cannot be read. Rendered by <c>AccessRecovered.cshtml</c>, which carries no
    /// layout — see the note where this is returned.
    ///
    /// <para>
    /// The lists are what this request knows rather than what the store holds — the one grant it
    /// touched, and no history. That is a worse page than the real one and an immeasurably better
    /// one than the error page, which would take an unrecoverable password with it.
    /// </para>
    /// </summary>
    private DatabaseAccessPageViewModel FromMemory(
        ManagedService service, DatabaseAccessGrant grant, IssuedCredentialViewModel issued, string? error)
    {
        ViewData["Title"] = service.Name;

        return new DatabaseAccessPageViewModel
        {
            Database = service,
            Grants = [grant],
            History = [],

            // Filled rather than left null. The recovered view has no "New grant" form to hide, so
            // nothing reads this today — but a model that says a database Harbora cannot currently
            // read is available for new grants is a lie waiting for the next view to believe it.
            Unavailable = new AccessUnavailable(
                "Harbora could not read its own database while finishing this rotation, so nothing "
                + "further can be issued until this page loads normally again.",
                "Harbora هنگام پایان این تعویض نتوانست دیتابیس خودش را بخواند، پس تا وقتی این صفحه "
                + "دوباره درست بارگذاری نشود چیز تازه‌ای صادر نمی‌شود."),
            Issued = issued,

            // The rotation's own sentence wins when there is one — it is the more important of the
            // two facts. Otherwise the reader is told why the rest of the page looks thin, so a
            // missing history is not mistaken for a missing history.
            Error = error ?? (IsFa
                ? "Harbora نتوانست بقیهٔ این صفحه را دوباره بخواند، پس فهرست مجوزها و تاریخچه کامل نیست. "
                  + "رمز پایین از این موضوع اثر نگرفته — همین حالا کپی‌اش کنید؛ فقط یک بار نشان داده می‌شود."
                : "Harbora could not read the rest of this page back, so the grants and history below "
                  + "are incomplete. The password below is unaffected — copy it now, it is shown only once.")
        };
    }

    private Task<ManagedService?> FindDatabaseAsync(Guid id, CancellationToken ct) =>
        db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);

    /// <summary>
    /// Whether this database's own server is not the machine external access publishes on.
    ///
    /// <para>
    /// HARBORA-0059: the availability check used to have no opinion on placement at all, so the
    /// button was offered for a database on any server, and the only refusal came from
    /// <c>DockerTcpGateway.OpenAsync</c> — after <c>DatabaseAccessService.IssueAsync</c> had already
    /// created a login on that database and had to undo it again. Resolved the same way the gateway
    /// itself decides it: by reference against <c>IServerEngineFactory.Local</c>, so the two can never
    /// disagree about which machine is "this one".
    /// </para>
    /// </summary>
    private async Task<bool> RunsElsewhereAsync(ManagedService service, CancellationToken ct)
    {
        try
        {
            return !ReferenceEquals(await engines.ResolveAsync(service.ServerId, ct), engines.Local);
        }
        catch
        {
            // Cannot be reached at all reads as "elsewhere" for this purpose too: nothing that
            // cannot be resolved is going to answer as though it were this machine.
            return true;
        }
    }

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
            Unavailable = ExternalAccessAvailability.Refuse(
                node, service, databaseAccess.CanOpenLocally, await RunsElsewhereAsync(service, ct)),

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
