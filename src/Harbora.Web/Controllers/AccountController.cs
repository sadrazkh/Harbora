using System.Security.Claims;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Web.Infrastructure;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

public sealed class AccountController(
    HarboraDbContext db,
    IPasswordHasher hasher,
    IAuditLogger audit,
    Harbora.Application.Abstractions.ISystemClock clock,
    Harbora.Web.Infrastructure.PanelModeProvider panelModes) : Controller
{
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private static bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("/account/login")]
    public IActionResult Login(string? returnUrl) => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost("/account/login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var email = model.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email && u.IsActive);
        // Verify even when the user is missing to avoid leaking account existence via timing.
        var ok = user is not null && hasher.Verify(model.Password, user.PasswordHash);
        if (!ok || user is null)
        {
            await audit.LogAsync("user.login_failed", "user", user?.Id.ToString(), ClientIp,
                actorEmailOverride: email, userIdOverride: user?.Id);
            ModelState.AddModelError(string.Empty,
                IsFa ? "ایمیل یا رمز نادرست است." : "Invalid email or password.");
            return View(model);
        }

        user.LastLoginAt = clock.UtcNow;
        // Bootstrap query: this is what DECIDES the caller's workspace, so it must not be filtered by
        // it. At this point the request has no workspace claim, so the global filter would match
        // nothing and every user would sign in scoped to an empty workspace — an empty dashboard,
        // and any app they create stamped with Guid.Empty.
        var memberships = await db.WorkspaceMembers.IgnoreQueryFilters()
            .Where(m => m.UserId == user.Id)
            .Select(m => m.WorkspaceId).ToListAsync();

        // FirstOrDefaultAsync used to stand here. Over an empty set it returns Guid.Empty — not
        // null, not an error, an ordinary-looking id — which went straight into the workspace claim
        // and scoped the person to a workspace that does not exist.
        var resolution = Harbora.Infrastructure.Security.WorkspaceMembership.Resolve(
            memberships, await db.Workspaces.IgnoreQueryFilters().Select(w => w.Id).ToListAsync());

        if (resolution.WorkspaceId is not { } workspaceId)
        {
            await audit.LogAsync("user.login_no_workspace", "user", user.Id.ToString(), ClientIp,
                actorEmailOverride: user.Email, userIdOverride: user.Id);
            ModelState.AddModelError(string.Empty, (IsFa ? resolution.ReasonFa : null) ?? resolution.Reason!);
            return View(model);
        }

        // Repairs an account that predates the fix, on the way through, so nobody has to be found
        // and mended by hand.
        if (memberships.Count == 0)
            db.WorkspaceMembers.Add(
                Harbora.Infrastructure.Security.WorkspaceMembership.For(workspaceId, user.Id, user.Role));

        await db.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(HarboraClaims.Workspace, workspaceId.ToString())
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        await audit.LogAsync("user.login", "user", user.Id.ToString(), ClientIp,
            actorEmailOverride: user.Email, userIdOverride: user.Id);
        return LocalRedirect(model.ReturnUrl ?? "/");
    }

    [HttpPost("/account/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/account/login");
    }

    /// <summary>
    /// Switches between the simple and full panel.
    ///
    /// The choice is written to the account, not a cookie, so it follows the person to another
    /// device. They are returned to the page they were on: switching mode mid-task and landing on
    /// the dashboard loses whatever they were doing.
    /// </summary>
    [HttpPost("/account/panel-mode")]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> SetPanelMode(string mode, string? returnUrl, CancellationToken ct)
    {
        // An unrecognised value clears the preference rather than guessing, which puts the person
        // back on whatever default an administrator chose.
        Harbora.Domain.Identity.PanelMode? chosen =
            Enum.TryParse<Harbora.Domain.Identity.PanelMode>(mode, ignoreCase: true, out var parsed)
                ? parsed
                : null;

        await panelModes.SetAsync(chosen, ct);

        // LocalRedirect refuses an absolute URL, so a crafted returnUrl cannot bounce somebody off
        // the panel to another site.
        return LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
    }

    /// <summary>
    /// Folds a side panel away, or brings it back.
    ///
    /// On the account rather than in local storage, like the panel mode above and for the same
    /// reason: somebody who put the ready-made apps shelf away has said something about how they
    /// want to work, not about the laptop they were sitting at.
    /// </summary>
    [HttpPost("/account/rail")]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> SetRail(
        string panel, bool open, string? returnUrl,
        [FromServices] Harbora.Web.Infrastructure.RailPreferences rails, CancellationToken ct)
    {
        // An unrecognised panel name changes nothing rather than folding whichever one happens to
        // be first in the enum.
        if (!Enum.TryParse<Harbora.Infrastructure.Navigation.RailPanel>(panel, ignoreCase: true, out var which))
            return LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);

        await rails.SetAsync(which, open, ct);

        return LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
    }

    /// <summary>Language switcher — sets the culture cookie and returns to the current page.</summary>
    [HttpPost("/account/language")]
    [ValidateAntiForgeryToken]
    public IActionResult SetLanguage(string culture, string? returnUrl)
    {
        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });
        return LocalRedirect(returnUrl ?? "/");
    }

    [HttpGet("/account/denied")]
    public IActionResult Denied() => View();
}
