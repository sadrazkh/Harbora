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
    ICurrentUser currentUser,
    Harbora.Application.Abstractions.ISystemClock clock,
    Harbora.Infrastructure.Notifications.PlatformMailer mailer,
    IQuotaService quota,
    Harbora.Infrastructure.Security.AccountSessionService sessions,
    ISecretProtector protector,
    IJobQueue jobs) : Controller
{
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private static bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Users";

        // Users are a platform-wide concept, not a tenant's: the filter would hide every account
        // that is not a member of the caller's own workspace, including the other administrators.
        var users = await db.Users.IgnoreQueryFilters()
            .OrderBy(u => u.Role).ThenBy(u => u.Email)
            .Select(u => new UserRow(
                u.Id, u.Email, u.DisplayName, u.Role, u.IsActive, u.ScopedToProjects,
                u.LastLoginAt, u.EmailVerifiedAt))
            .ToListAsync(ct);
        var personal = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.IsPersonal && w.OwnerUserId != null)
            .ToDictionaryAsync(w => w.OwnerUserId!.Value, w => w.Id, ct);
        // Projected before materialising — see WorkspaceMembershipCounts for why a bare
        // GroupBy(...).ToDictionaryAsync(...) is the wrong shape here.
        var membershipCounts = await Harbora.Infrastructure.Security.WorkspaceMembershipCounts.ByUserAsync(db, ct);
        var scopedUsers = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ScopedToProjects).Select(m => m.UserId).Distinct().ToListAsync(ct);
        users = users.Select(u => u with
        {
            PersonalWorkspaceId = personal.GetValueOrDefault(u.Id),
            WorkspaceCount = membershipCounts.GetValueOrDefault(u.Id),
            ScopedToProjects = scopedUsers.Contains(u.Id)
        }).ToList();

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
            Role = role,
            EmailVerifiedAt = clock.UtcNow
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

        await using var quotaReservation = await quota.AcquireCreationLockAsync(workspaceId.Value, ct);
        var seat = await quota.CanAddGovernedResourcesAsync(workspaceId.Value,
            new GovernanceQuotaDelta(Members: 1), ct);
        if (!seat.Allowed) return Back((IsFa ? seat.ReasonFa : null) ?? seat.Reason!, error: true);

        db.WorkspaceMembers.Add(
            Harbora.Infrastructure.Security.WorkspaceMembership.For(workspaceId.Value, created.Id, role));

        await db.SaveChangesAsync(ct);
        await quotaReservation.CommitAsync(ct);

        await audit.LogAsync("user.created", "user", email, ClientIp, ct: ct);
        return Back($"Created {email}.");
    }

    /// <summary>
    /// Create the account and email a set-password link instead of whispering a temporary password
    /// over chat. The account is born with a random password nobody knows; the link is the only way
    /// in, and it is the same single-use, hour-long token the forgot-password flow uses.
    /// </summary>
    [HttpPost("invite")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Invite(
        string email, string? displayName, SystemRole role, CancellationToken ct)
    {
        if (!await mailer.IsConfiguredAsync(ct))
            return Back(IsFa ? "اول SMTP را در تنظیمات پلتفرم تنظیم کنید." : "Configure platform SMTP first.", error: true);

        var actorRole = await ActorRoleAsync(ct);
        if (UserAdministration.RefuseCreation(actorRole, role) is { } refusal)
            return Back(refusal, error: true);

        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email))
            return Back(IsFa ? "ایمیل لازم است." : "An email address is required.", error: true);
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
            return Back(IsFa ? "این ایمیل حساب دارد." : "That email address already has an account.", error: true);

        var created = new User
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName.Trim(),
            // Random and unrecorded: until the person follows their link, there is nothing to leak.
            PasswordHash = hasher.Hash(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))),
            Role = role,
            // The set-password message is itself delivered to this address.
            EmailVerifiedAt = clock.UtcNow
        };
        db.Users.Add(created);

        var workspaceId = await db.Workspaces.IgnoreQueryFilters()
            .Where(w => w.Id == currentUser.WorkspaceId).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct)
            ?? await db.Workspaces.IgnoreQueryFilters().Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct);
        if (workspaceId is null)
            return Back("This installation has no workspace to add the account to.", error: true);
        await using var quotaReservation = await quota.AcquireCreationLockAsync(workspaceId.Value, ct);
        var seat = await quota.CanAddGovernedResourcesAsync(workspaceId.Value,
            new GovernanceQuotaDelta(Members: 1), ct);
        if (!seat.Allowed) return Back((IsFa ? seat.ReasonFa : null) ?? seat.Reason!, error: true);
        db.WorkspaceMembers.Add(
            Harbora.Infrastructure.Security.WorkspaceMembership.For(workspaceId.Value, created.Id, role));

        var (token, hash) = Harbora.Infrastructure.Security.PasswordReset.Issue();
        db.PasswordResetTokens.Add(new Harbora.Domain.Identity.PasswordResetToken
        {
            UserId = created.Id,
            TokenHash = hash,
            ExpiresAt = clock.UtcNow + Harbora.Infrastructure.Security.PasswordReset.Lifetime,
            CreatedAt = clock.UtcNow
        });
        var link = $"{Request.Scheme}://{Request.Host}/account/reset?token={token}";
        // §7 Q3(b): queued onto N1's outbox rather than sent inline. The account is created either
        // way — always was, since the account row is saved before this point — so there is no longer
        // a "created but the email failed" branch to report: queuing (unlike a synchronous send)
        // practically cannot fail on its own, and a channel refusal is now retried three times rather
        // than lost the moment it happens.
        var delivery = Harbora.Infrastructure.Notifications.OutboxMail.Queue(
            db, protector, NotificationDeliveryPurpose.PlatformInvite, email,
            IsFa ? "دعوت به Harbora" : "You are invited to Harbora",
            IsFa
                ? $"برایتان حسابی ساخته شده. با این لینک رمزتان را بگذارید (تا یک ساعت معتبر است):\n{link}"
                : $"An account has been created for you. Set your password with this link (valid for one hour):\n{link}");

        await db.SaveChangesAsync(ct);
        await quotaReservation.CommitAsync(ct);
        await jobs.EnqueueAsync(Harbora.Domain.Jobs.JobKind.NotificationDelivery, delivery.Id, delivery.WorkspaceId, ct);

        await audit.LogAsync("user.invited", "user", email, ClientIp, ct: ct);
        return Back(IsFa ? $"دعوت‌نامه به {email} صف شد." : $"An invitation to {email} has been queued.");
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
        if (active)
            await db.SaveChangesAsync(ct);
        else
            await sessions.RevokeAllAsync(user.Id, exceptSessionId: null, ct);

        await audit.LogAsync(active ? "user.reactivated" : "user.suspended", "user", user.Email, ClientIp, ct: ct);
        return Back(active ? $"{user.Email} can sign in again." : $"{user.Email} is suspended.");
    }

    [HttpPost("{id:guid}/email/verify")]
    [ValidateAntiForgeryToken]
    // The nearest thing this panel has to changing somebody's email: marking an address proven
    // without anybody having proved it. There is no self-serve email-change action to block beside
    // it — if one is ever added, it belongs on this list and SupportRestrictionCensusTests will say so.
    [Harbora.Web.Infrastructure.RefuseUnderSupportSession(SupportRestrictedAct.Email)]
    public async Task<IActionResult> VerifyEmail(Guid id, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        if (user.EmailVerifiedAt is null)
        {
            user.EmailVerifiedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync("user.email_verified_by_admin", "user", user.Email, ClientIp, ct: ct);
        }

        return Back(IsFa ? $"ایمیل {user.Email} تأیید شد." : $"{user.Email} is verified.");
    }

    /// <summary>
    /// Strip a locked-out account of its second factor. The person lost their phone and their
    /// recovery sheet; this is the human path back in, and it is deliberately an administrator's
    /// act with an audit row rather than anything self-service.
    /// </summary>
    [HttpPost("{id:guid}/totp/reset")]
    [ValidateAntiForgeryToken]
    [Harbora.Web.Infrastructure.RefuseUnderSupportSession(SupportRestrictedAct.TwoFactor)]
    public async Task<IActionResult> ResetTotp(Guid id, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        user.TotpSecretEncrypted = null;
        user.TotpEnabledAt = null;
        user.RecoveryCodesHash = null;
        await sessions.RevokeAllAsync(user.Id, exceptSessionId: null, ct);

        await audit.LogAsync("user.totp_reset_by_admin", "user", user.Email, ClientIp, ct: ct);
        return Back(IsFa ? $"ورود دومرحله‌ای {user.Email} برداشته شد." : $"Two-factor was removed from {user.Email}.");
    }

    [HttpPost("{id:guid}/password")]
    [ValidateAntiForgeryToken]
    [Harbora.Web.Infrastructure.RefuseUnderSupportSession(SupportRestrictedAct.Password)]
    public async Task<IActionResult> ResetPassword(Guid id, string password, CancellationToken ct)
    {
        var (user, context) = await ContextForAsync(id, ct);
        if (user is null) return NotFound();

        if (UserAdministration.RefusePasswordReset(context) is { } refusal)
            return Back(refusal, error: true);

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return Back("A password of at least 8 characters is required.", error: true);

        user.PasswordHash = hasher.Hash(password);
        await sessions.RevokeAllAsync(user.Id, exceptSessionId: null, ct);

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
