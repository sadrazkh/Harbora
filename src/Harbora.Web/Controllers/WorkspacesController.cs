using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Notifications;
using Harbora.Infrastructure.Security;
using Harbora.Web.Infrastructure;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

[Authorize]
[Route("workspaces")]
public sealed class WorkspacesController(
    HarboraDbContext db,
    ICurrentUser currentUser,
    WorkspaceAccountService accounts,
    PlatformMailer mailer,
    IAuditLogger audit,
    Harbora.Application.Abstractions.ISystemClock clock) : Controller
{
    private Guid UserId => currentUser.UserId ?? Guid.Empty;
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private static bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = IsFa ? "ورک‌اسپیس‌ها" : "Workspaces";
        var signedInUser = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == UserId, ct);
        if (signedInUser is null) return Challenge();
        await accounts.EnsurePersonalWorkspaceAsync(signedInUser, ct);
        var memberships = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == UserId)
            .Include(m => m.Workspace)
            .OrderByDescending(m => m.Workspace!.IsPersonal).ThenBy(m => m.Workspace!.Name)
            .ToListAsync(ct);
        var current = memberships.FirstOrDefault(m => m.WorkspaceId == WorkspaceId);
        if (current?.Workspace is null) return Forbid();

        var workspaceIds = memberships.Select(m => m.WorkspaceId).ToList();
        var wallets = await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .Where(w => workspaceIds.Contains(w.WorkspaceId))
            .ToDictionaryAsync(w => w.WorkspaceId, ct);
        var members = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.WorkspaceId == WorkspaceId)
            .Include(m => m.User)
            .OrderBy(m => m.Role).ThenBy(m => m.User!.Email)
            .ToListAsync(ct);
        var canManage = current.Role == WorkspaceRole.Admin;
        var invitations = canManage
            ? await db.WorkspaceInvitations.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.WorkspaceId == WorkspaceId && i.AcceptedAt == null && !i.IsRevoked)
                .OrderByDescending(i => i.CreatedAt).ToListAsync(ct)
            : [];

        return View(new WorkspaceHubViewModel
        {
            CurrentWorkspaceId = WorkspaceId,
            CurrentWorkspaceName = current.Workspace.Name,
            CurrentIsPersonal = current.Workspace.IsPersonal,
            CanManageCurrent = canManage,
            Workspaces = memberships.Select(m =>
            {
                var wallet = wallets.GetValueOrDefault(m.WorkspaceId);
                return new WorkspaceSummaryRow(m.WorkspaceId, m.Workspace!.Name, m.Workspace.Slug,
                    m.Workspace.IsPersonal, m.WorkspaceId == WorkspaceId, m.Role,
                    wallet?.BalanceMinor, wallet?.Currency ?? "IRR");
            }).ToList(),
            Members = members.Select(m => new WorkspaceMemberRow(
                m.UserId, m.User!.Email, m.User.DisplayName, m.Role,
                current.Workspace.OwnerUserId == m.UserId, m.UserId == UserId)).ToList(),
            Invitations = invitations.Select(i => new WorkspaceInvitationRow(
                i.Id, i.Email, i.Role, i.TokenHint, i.ExpiresAt)).ToList()
        });
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            return Back(IsFa ? "نام ورک‌اسپیس لازم است و حداکثر ۱۲۸ نویسه دارد." : "Enter a workspace name of at most 128 characters.", true);
        var workspace = await accounts.CreateTeamWorkspaceAsync(UserId, name.Trim(), ct);
        await SwitchSessionAsync(workspace.Id, WorkspaceRole.Admin, ct);
        await audit.LogAsync("workspace.created", "workspace", workspace.Id.ToString(), ClientIp, ct: ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("switch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Switch(Guid workspaceId, string? returnUrl, CancellationToken ct)
    {
        var role = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && m.UserId == UserId)
            .Select(m => (WorkspaceRole?)m.Role).FirstOrDefaultAsync(ct);
        if (role is null) return Forbid();
        await SwitchSessionAsync(workspaceId, role.Value, ct);
        await audit.LogAsync("workspace.switched", "workspace", workspaceId.ToString(), ClientIp, ct: ct);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    [HttpPost("invite")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Invite(string? email, WorkspaceRole role, CancellationToken ct)
    {
        if (!await CanManageAsync(WorkspaceId, ct)) return Forbid();
        try
        {
            var issued = await accounts.InviteAsync(WorkspaceId, UserId, email ?? "", role, ct);
            var link = $"{Request.Scheme}://{Request.Host}/invitations/accept?token={Uri.EscapeDataString(issued.Token)}";
            TempData["InvitationLink"] = link;
            var workspaceName = await db.Workspaces.IgnoreQueryFilters()
                .Where(w => w.Id == WorkspaceId).Select(w => w.Name).SingleAsync(ct);
            if (await mailer.IsConfiguredAsync(ct))
            {
                try
                {
                    await mailer.SendAsync(issued.Invitation.Email,
                        IsFa ? $"دعوت به {workspaceName}" : $"Invitation to {workspaceName}",
                        IsFa
                            ? $"برای پیوستن به ورک‌اسپیس {workspaceName} این لینک را تا ۷ روز آینده باز کنید:\n{link}"
                            : $"Open this link within seven days to join {workspaceName}:\n{link}", ct);
                    TempData["Message"] = IsFa ? "دعوت‌نامه ارسال شد؛ لینک هم فقط همین بار نمایش داده می‌شود." : "Invitation sent; the link is also shown this once.";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    TempData["Error"] = (IsFa ? "ایمیل ارسال نشد؛ لینک را دستی بفرستید: " : "Email failed; send the link manually: ") + ex.Message;
                }
            }
            else
                TempData["Message"] = IsFa ? "دعوت ساخته شد؛ چون SMTP تنظیم نیست لینک را کپی کنید." : "Invitation created. SMTP is not configured, so copy the link.";
            await audit.LogAsync("workspace.member_invited", "workspace", WorkspaceId.ToString(), ClientIp, ct: ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Back(ex.Message, true);
        }
        return RedirectToAction(nameof(Index));
    }

    [AllowAnonymous]
    [HttpGet("/invitations/accept")]
    public async Task<IActionResult> Accept(string? token, CancellationToken ct)
    {
        var row = await accounts.FindInvitationAsync(token, ct);
        var error = row switch
        {
            null => IsFa ? "دعوت‌نامه پیدا نشد." : "Invitation not found.",
            { IsRevoked: true } => IsFa ? "دعوت‌نامه لغو شده است." : "This invitation was revoked.",
            { AcceptedAt: not null } => IsFa ? "دعوت‌نامه قبلاً استفاده شده است." : "This invitation was already used.",
            _ when row.ExpiresAt <= clock.UtcNow => IsFa ? "دعوت‌نامه منقضی شده است." : "This invitation has expired.",
            _ => null
        };
        var signedEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        return View("AcceptInvitation", new AcceptWorkspaceInvitationViewModel
        {
            Token = token ?? "",
            WorkspaceName = row?.Workspace?.Name ?? "",
            Email = row?.Email ?? "",
            Role = row?.Role ?? WorkspaceRole.Member,
            IsAuthenticated = User.Identity?.IsAuthenticated == true,
            EmailMatches = signedEmail is not null && string.Equals(signedEmail, row?.Email, StringComparison.OrdinalIgnoreCase),
            Error = error
        });
    }

    [HttpPost("/invitations/accept")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AcceptConfirmed(string token, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == UserId, ct);
        if (user is null) return Challenge();
        try
        {
            var workspace = await accounts.AcceptInvitationAsync(token, user, ct);
            var role = await db.WorkspaceMembers.IgnoreQueryFilters()
                .Where(m => m.WorkspaceId == workspace.Id && m.UserId == UserId)
                .Select(m => m.Role).SingleAsync(ct);
            await SwitchSessionAsync(workspace.Id, role, ct);
            await audit.LogAsync("workspace.invitation_accepted", "workspace", workspace.Id.ToString(), ClientIp, ct: ct);
            return Redirect("/workspaces");
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return Redirect($"/invitations/accept?token={Uri.EscapeDataString(token)}");
        }
    }

    [HttpPost("members/{userId:guid}/role")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeMemberRole(Guid userId, WorkspaceRole role, CancellationToken ct)
    {
        if (!await CanManageAsync(WorkspaceId, ct)) return Forbid();
        if (role is not (WorkspaceRole.Admin or WorkspaceRole.Member or WorkspaceRole.Operator or WorkspaceRole.Viewer))
            return Back(IsFa ? "نقش انتخاب‌شده معتبر نیست." : "Choose a valid workspace role.", true);
        var workspace = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == WorkspaceId, ct);
        if (workspace?.OwnerUserId == userId) return Back(IsFa ? "نقش مالک قابل کاهش نیست." : "The owner's role cannot be reduced.", true);
        var member = await db.WorkspaceMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.WorkspaceId == WorkspaceId && m.UserId == userId, ct);
        if (member is null) return NotFound();
        member.Role = role;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("workspace.member_role_changed", "user", userId.ToString(), ClientIp, ct: ct);
        return Back(IsFa ? "نقش عضو تغییر کرد." : "Member role updated.");
    }

    [HttpPost("members/{userId:guid}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(Guid userId, CancellationToken ct)
    {
        if (!await CanManageAsync(WorkspaceId, ct)) return Forbid();
        var workspace = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == WorkspaceId, ct);
        if (workspace?.OwnerUserId == userId) return Back(IsFa ? "مالک را نمی‌توان حذف کرد." : "The workspace owner cannot be removed.", true);
        await db.WorkspaceMembers.IgnoreQueryFilters()
            .Where(m => m.WorkspaceId == WorkspaceId && m.UserId == userId).ExecuteDeleteAsync(ct);
        await audit.LogAsync("workspace.member_removed", "user", userId.ToString(), ClientIp, ct: ct);
        return Back(IsFa ? "عضو حذف شد." : "Member removed.");
    }

    [HttpPost("invitations/{id:guid}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeInvitation(Guid id, CancellationToken ct)
    {
        if (!await CanManageAsync(WorkspaceId, ct)) return Forbid();
        await db.WorkspaceInvitations.IgnoreQueryFilters()
            .Where(i => i.Id == id && i.WorkspaceId == WorkspaceId && i.AcceptedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.IsRevoked, true), ct);
        return Back(IsFa ? "دعوت لغو شد." : "Invitation revoked.");
    }

    private async Task<bool> CanManageAsync(Guid workspaceId, CancellationToken ct) =>
        await db.WorkspaceMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == UserId && m.Role == WorkspaceRole.Admin, ct);

    private async Task SwitchSessionAsync(Guid workspaceId, WorkspaceRole role, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == UserId, ct);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            SessionPrincipalFactory.Create(user, workspaceId, role));
    }

    private IActionResult Back(string message, bool error = false)
    {
        TempData[error ? "Error" : "Message"] = message;
        return RedirectToAction(nameof(Index));
    }
}
