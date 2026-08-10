using System.Security.Claims;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Settings;
using Harbora.Web.Infrastructure;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>First-run wizard: creates the owner account + first workspace, then locks itself off.</summary>
public sealed class SetupController(
    HarboraDbContext db,
    IPasswordHasher hasher,
    Harbora.Application.Abstractions.ISystemClock clock) : Controller
{
    [HttpGet("/setup")]
    public async Task<IActionResult> Index()
    {
        if (await IsCompleted()) return Redirect("/");
        return View(new SetupViewModel());
    }

    [HttpPost("/setup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(SetupViewModel model)
    {
        if (await IsCompleted()) return Redirect("/");
        if (!ModelState.IsValid) return View(model);

        // Guard against a race: only proceed if no users exist yet.
        if (await db.Users.AnyAsync())
            return Redirect("/account/login");

        var user = new User
        {
            Email = model.Email.Trim().ToLowerInvariant(),
            DisplayName = model.DisplayName,
            PasswordHash = hasher.Hash(model.Password),
            Role = SystemRole.Owner,
            PreferredCulture = model.Culture,
            LastLoginAt = clock.UtcNow
        };
        var workspace = new Workspace
        {
            Name = model.PlatformName,
            Slug = "default",
            IsDefault = true,
            IsPersonal = true,
            OwnerUser = user
        };
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        db.WorkspaceMembers.Add(new WorkspaceMember { Workspace = workspace, User = user, Role = WorkspaceRole.Admin });

        db.Settings.AddRange(
            new Setting { Key = SettingKeys.SetupCompleted, Value = "true" },
            new Setting { Key = SettingKeys.PlatformName, Value = model.PlatformName },
            new Setting { Key = SettingKeys.PlatformRootDomain, Value = model.RootDomain },
            new Setting { Key = SettingKeys.AcmeEmail, Value = model.AcmeEmail },
            new Setting { Key = SettingKeys.DefaultCulture, Value = model.Culture });

        await db.SaveChangesAsync();
        SetupGuardMiddleware.MarkCompleted();

        await SignInAsync(user, workspace.Id);
        return Redirect("/");
    }

    private async Task<bool> IsCompleted() =>
        await db.Settings.AnyAsync(s => s.Key == SettingKeys.SetupCompleted && s.Value == "true");

    private async Task SignInAsync(User user, Guid workspaceId)
    {
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            SessionPrincipalFactory.Create(user, workspaceId, WorkspaceRole.Admin));

        Response.Cookies.Append(".AspNetCore.Culture",
            Microsoft.AspNetCore.Localization.CookieRequestCultureProvider.MakeCookieValue(
                new Microsoft.AspNetCore.Localization.RequestCulture(user.PreferredCulture)));
    }
}
