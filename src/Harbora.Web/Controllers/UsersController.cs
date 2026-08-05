using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Platform-level user administration.
///
/// Every action routes its decision through <see cref="UserAdministration"/> rather than deciding
/// here, because the same question is asked from four places and four copies of "may I" is how one
/// of them ends up missing the last-owner check. The view hides what it must not offer; this
/// refuses it again, since a hidden button is not a permission check.
/// </summary>
[Authorize(Policy = Capabilities.TenantsManage)]
[Route("users")]
public sealed class UsersController(
    HarboraDbContext db,
    IPasswordHasher hasher,
    IAuditLogger audit,
    ICurrentUser currentUser) : Controller
{
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Users";

        // Users are a platform-wide concept, not a tenant's: the filter would hide every account
        // that is not a member of the caller's own workspace, including the other administrators.
        var users = await db.Users.IgnoreQueryFilters()
            .OrderBy(u => u.Role).ThenBy(u => u.Email)
            .Select(u => new UserRow(
                u.Id, u.Email, u.DisplayName, u.Role, u.IsActive, u.ScopedToProjects, u.LastLoginAt))
            .ToListAsync(ct);

        ViewBag.ActorRole = await ActorRoleAsync(ct);
        ViewBag.ActorId = currentUser.UserId ?? Guid.Empty;
        ViewBag.ActiveOwners = users.Count(u => u.Role == SystemRole.Owner && u.IsActive);

        return View(users);
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string email, string? displayName, string password, SystemRole role, CancellationToken ct)
    {
        var actorRole = await ActorRoleAsync(ct);

        if (UserAdministration.RefuseCreation(actorRole, role) is { } refusal)
            return Back(refusal, error: true);

        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email)) return Back("An email address is required.", error: true);

        // Matching what the sign-in path expects rather than a number invented here.
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return Back("A temporary password of at least 8 characters is required.", error: true);

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
            return Back("That email address already has an account.", error: true);

        var created = new User
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName.Trim(),
            PasswordHash = hasher.Hash(password),
            Role = role
        };
        db.Users.Add(created);

        // The membership is the account. Without it the person signs in to Guid.Empty: an empty
        // dashboard, an empty app list, and anything they create stamped with a workspace that does
        // not exist. Every user made through this form was born that way, and nothing failed —
        // every page returned 200.
        var workspaceId = await db.Workspaces.IgnoreQueryFilters()
            .Where(w => w.Id == currentUser.WorkspaceId).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct)
            ?? await db.Workspaces.IgnoreQueryFilters().Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct);

        if (workspaceId is null)
            return Back("This installation has no workspace to add the account to.", error: true);

        db.WorkspaceMembers.Add(
            Harbora.Infrastructure.Security.WorkspaceMembership.For(workspaceId.Value, created.Id, role));

        await db.SaveChangesAsync(ct);

        await audit.LogAsync("user.created", "user", email, ClientIp, ct: ct);
        return Back($"Created {email}.");
    }

    [HttpPost("{id:guid}/role")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeRole(Guid id, SystemRole role, CancellationToken ct)
    {
        var (user, context) = await ContextForAsync(id, ct);
        if (user is null) return NotFound();

        if (UserAdministration.RefuseRoleChange(context, role) is { } refusal)
            return Back(refusal, error: true);

        var previous = user.Role;
        user.Role = role;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("user.role_changed", "user", user.Email, ClientIp,
            metadataJson: System.Text.Json.JsonSerializer.Serialize(
                new { from = previous.ToString(), to = role.ToString() }), ct: ct);

        return Back($"{user.Email} is now {role}.");
    }

    [HttpPost("{id:guid}/active")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(Guid id, bool active, CancellationToken ct)
    {
        var (user, context) = await ContextForAsync(id, ct);
        if (user is null) return NotFound();

        // Suspending and restoring are not mirror images — see UserAdministration.
        var refusal = active
            ? UserAdministration.RefuseReactivation(context)
            : UserAdministration.RefuseDeactivation(context);

        if (refusal is { } reason) return Back(reason, error: true);

        user.IsActive = active;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(active ? "user.reactivated" : "user.suspended", "user", user.Email, ClientIp, ct: ct);
        return Back(active ? $"{user.Email} can sign in again." : $"{user.Email} is suspended.");
    }

    [HttpPost("{id:guid}/password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(Guid id, string password, CancellationToken ct)
    {
        var (user, context) = await ContextForAsync(id, ct);
        if (user is null) return NotFound();

        if (UserAdministration.RefusePasswordReset(context) is { } refusal)
            return Back(refusal, error: true);

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return Back("A password of at least 8 characters is required.", error: true);

        user.PasswordHash = hasher.Hash(password);
        await db.SaveChangesAsync(ct);

        // The password itself is never logged, only that it was replaced and by whom.
        await audit.LogAsync("user.password_reset", "user", user.Email, ClientIp, ct: ct);
        return Back($"The password for {user.Email} was replaced.");
    }

    // --- helpers ---

    private async Task<(User? User, UserAdminContext Context)> ContextForAsync(Guid id, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return (null, default!);

        var activeOwners = await db.Users.IgnoreQueryFilters()
            .CountAsync(u => u.Role == SystemRole.Owner && u.IsActive, ct);

        return (user, new UserAdminContext(
            currentUser.UserId ?? Guid.Empty,
            await ActorRoleAsync(ct),
            user.Id,
            user.Role,
            user.IsActive,
            activeOwners));
    }

    /// <summary>
    /// The signed-in user's role, read from the database rather than the cookie: a role changed
    /// after sign-in must take effect now, not whenever the claim happens to be reissued.
    /// </summary>
    private async Task<SystemRole> ActorRoleAsync(CancellationToken ct)
    {
        var id = currentUser.UserId ?? Guid.Empty;
        var role = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Id == id).Select(u => (SystemRole?)u.Role).FirstOrDefaultAsync(ct);

        return role ?? SystemRole.Viewer;
    }

    private IActionResult Back(string message, bool error = false)
    {
        TempData[error ? "Error" : "Message"] = message;
        return RedirectToAction(nameof(Index));
    }
}
