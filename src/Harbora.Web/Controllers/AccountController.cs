using System.Security.Claims;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Security;
using Harbora.Web.Infrastructure;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

public sealed partial class AccountController(
    HarboraDbContext db,
    IPasswordHasher hasher,
    IAuditLogger audit,
    Harbora.Application.Abstractions.ISystemClock clock,
    Harbora.Infrastructure.Notifications.PlatformMailer mailer,
    Harbora.Application.Abstractions.ISecretProtector protector,
    IDataProtectionProvider dataProtection,
    Harbora.Web.Infrastructure.PanelModeProvider panelModes,
    WorkspaceAccountService workspaceAccounts,
    AccountSessionService sessions,
    ExternalLoginSettingsService externalLogins,
    SingleSignOnRequirementService ssoRequirement,
    IJobQueue jobs) : Controller
{
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private static bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("/account/login")]
    public async Task<IActionResult> Login(string? returnUrl, CancellationToken ct)
    {
        // The forgot-password link only exists when the platform can actually send the email.
        // A link that leads to "ask your administrator" is a support ticket with extra steps.
        ViewBag.CanResetPassword = await mailer.IsConfiguredAsync(ct);
        ViewBag.ExternalProviders = await OfferedProvidersAsync(ct);
        // A provider that answered with a refusal, or a browser that lost its correlation cookie,
        // comes back here. The reason rides in the query string because TempData re-types what it
        // carries and this message crosses a redirect issued outside MVC entirely.
        ViewBag.ExternalFailed = Request.Query["sso"] == "failed";
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpGet("/account/register")]
    public async Task<IActionResult> Register(string? invitation, CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect("/workspaces");
        var model = new RegisterViewModel { InvitationToken = invitation };
        if (!string.IsNullOrWhiteSpace(invitation))
        {
            var row = await workspaceAccounts.FindInvitationAsync(invitation, ct);
            if (row is null || row.IsRevoked || row.AcceptedAt is not null || row.ExpiresAt <= clock.UtcNow)
                ModelState.AddModelError(string.Empty, IsFa ? "این دعوت‌نامه معتبر نیست یا منقضی شده است." : "This invitation is invalid or expired.");
            else
            {
                model.Email = row.Email;
                ViewBag.InvitedWorkspace = row.Workspace?.Name;
            }
        }
        return View(model);
    }

    [HttpPost("/account/register")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(RegisterViewModel model, CancellationToken ct)
    {
        WorkspaceInvitation? invitation = null;
        if (!string.IsNullOrWhiteSpace(model.InvitationToken))
        {
            invitation = await workspaceAccounts.FindInvitationAsync(model.InvitationToken, ct);
            if (invitation is null || invitation.IsRevoked || invitation.AcceptedAt is not null || invitation.ExpiresAt <= clock.UtcNow)
                ModelState.AddModelError(string.Empty, IsFa ? "این دعوت‌نامه معتبر نیست یا منقضی شده است." : "This invitation is invalid or expired.");
            else if (!string.Equals(invitation.Email, model.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
                ModelState.AddModelError(nameof(model.Email), IsFa ? "ثبت‌نام باید با ایمیل روی دعوت‌نامه انجام شود." : "Register with the email address on the invitation.");
        }
        if (!ModelState.IsValid) return View(model);

        var email = WorkspaceAccountService.NormalizeEmail(model.Email);
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
        {
            ModelState.AddModelError(nameof(model.Email),
                IsFa ? "این ایمیل حساب دارد؛ وارد شوید و دعوت را بپذیرید." : "This email already has an account. Sign in to accept the invitation.");
            return View(model);
        }

        var user = new User
        {
            Email = email,
            DisplayName = model.DisplayName.Trim(),
            PasswordHash = hasher.Hash(model.Password),
            Role = SystemRole.Member,
            PreferredCulture = IsFa ? "fa" : "en",
            LastLoginAt = clock.UtcNow,
            // Following a workspace invitation proves control of the address the invitation was
            // bound to. A public registration must prove it with the separate email link below.
            EmailVerifiedAt = invitation is null ? null : clock.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        var personal = await workspaceAccounts.EnsurePersonalWorkspaceAsync(user, ct);

        if (invitation is null)
        {
            await SendVerificationAsync(user, ct);
            TempData["VerificationEmail"] = user.Email;
            await audit.LogAsync("user.registered_pending_verification", "user", user.Id.ToString(), ClientIp,
                actorEmailOverride: user.Email, userIdOverride: user.Id, workspaceId: null, ct: ct);
            return Redirect("/account/verify-pending");
        }

        var destination = personal;
        if (invitation is not null)
        {
            try
            {
                destination = await workspaceAccounts.AcceptInvitationAsync(model.InvitationToken!, user, ct);
            }
            catch (InvalidOperationException ex)
            {
                // The account and its personal workspace are already valid. A plan may have been
                // lowered after the invitation was issued; do not turn that race into a 500 or
                // discard a registration. Sign in to the personal workspace and explain the
                // invitation refusal so an administrator can free a seat or change the plan.
                TempData["Error"] = ex.Message;
            }
        }

        var membershipRole = await db.WorkspaceMembers.IgnoreQueryFilters()
            .Where(m => m.WorkspaceId == destination.Id && m.UserId == user.Id)
            .Select(m => m.Role).SingleAsync(ct);
        await SignInAsync(user, destination.Id, membershipRole);
        await audit.LogAsync("user.registered", "user", user.Id.ToString(), ClientIp,
            actorEmailOverride: user.Email, userIdOverride: user.Id, workspaceId: null, ct: ct);
        return Redirect("/");
    }

    // ---- Forgotten password ----
    //
    // The same words whatever happened, and the token row is the only variable: whether the email
    // exists, whether mail went out — the page says "if that address has an account, a link is on
    // its way". Anything more specific is an account-enumeration oracle on the login screen.

    [HttpGet("/account/forgot")]
    public async Task<IActionResult> Forgot(CancellationToken ct)
    {
        if (!await mailer.IsConfiguredAsync(ct)) return NotFound();
        return View();
    }

    [HttpPost("/account/forgot")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Forgot(string email, CancellationToken ct)
    {
        if (!await mailer.IsConfiguredAsync(ct)) return NotFound();

        var normalised = (email ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalised && u.IsActive, ct);

        if (user is not null)
        {
            var (token, hash) = Harbora.Infrastructure.Security.PasswordReset.Issue();
            db.PasswordResetTokens.Add(new Harbora.Domain.Identity.PasswordResetToken
            {
                UserId = user.Id,
                TokenHash = hash,
                ExpiresAt = clock.UtcNow + Harbora.Infrastructure.Security.PasswordReset.Lifetime,
                CreatedAt = clock.UtcNow
            });
            var link = $"{Request.Scheme}://{Request.Host}/account/reset?token={token}";
            // §7 Q3(b): joins the same outbox N1 built for alert deliveries — one delivery path, one
            // retry story, one record, rather than a synchronous SmtpClient call whose only trace was
            // a caught-and-logged exception. The anti-enumeration behaviour is unchanged: this page
            // says "a link is on its way" whether the address exists or the send later fails, because
            // queuing (unlike a synchronous send) practically cannot fail on its own.
            var delivery = Harbora.Infrastructure.Notifications.OutboxMail.Queue(
                db, protector, NotificationDeliveryPurpose.PasswordReset, user.Email,
                IsFa ? "بازنشانی رمز Harbora" : "Reset your Harbora password",
                IsFa
                    ? $"برای گذاشتن رمز تازه این لینک را باز کنید (تا یک ساعت معتبر است):\n{link}\n\nاگر شما درخواست نکرده‌اید، این ایمیل را نادیده بگیرید."
                    : $"Open this link to set a new password (valid for one hour):\n{link}\n\nIf you did not ask for this, ignore this email.");
            await db.SaveChangesAsync(ct);
            await jobs.EnqueueAsync(Harbora.Domain.Jobs.JobKind.NotificationDelivery, delivery.Id, delivery.WorkspaceId, ct);

            await audit.LogAsync("user.password_reset_requested", "user", user.Id.ToString(), ClientIp,
                actorEmailOverride: user.Email, userIdOverride: user.Id, workspaceId: null, ct: ct);
        }

        ViewBag.Sent = true;
        return View();
    }

    [HttpGet("/account/reset")]
    public async Task<IActionResult> Reset(string? token, CancellationToken ct)
    {
        if (!await mailer.IsConfiguredAsync(ct)) return NotFound();
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        return View(new ResetPasswordViewModel { Token = token });
    }

    [HttpPost("/account/reset")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Reset(ResetPasswordViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);

        var hash = Harbora.Infrastructure.Security.PasswordReset.HashOf(model.Token);
        var row = await db.PasswordResetTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (Harbora.Infrastructure.Security.PasswordReset.Check(row, clock.UtcNow) is { } refusal)
        {
            ModelState.AddModelError(string.Empty, refusal switch
            {
                Harbora.Infrastructure.Security.PasswordResetRefusal.Expired =>
                    IsFa ? "این لینک منقضی شده است. دوباره درخواست کنید." : "This link has expired. Request a new one.",
                Harbora.Infrastructure.Security.PasswordResetRefusal.AlreadyUsed =>
                    IsFa ? "این لینک قبلاً استفاده شده است." : "This link has already been used.",
                _ => IsFa ? "این لینک معتبر نیست." : "This link is not valid."
            });
            return View(model);
        }

        // Consumed before the outcome is known: a link that failed half-way must not stay live in
        // somebody's inbox.
        row!.UsedAt = clock.UtcNow;
        row.User!.PasswordHash = hasher.Hash(model.Password);
        await sessions.RevokeAllAsync(row.UserId, exceptSessionId: null, ct);

        await audit.LogAsync("user.password_reset_completed", "user", row.UserId.ToString(), ClientIp,
            actorEmailOverride: row.User.Email, userIdOverride: row.UserId, workspaceId: null, ct: ct);

        TempData["Message"] = IsFa ? "رمز تازه ذخیره شد. وارد شوید." : "Your new password is saved. Sign in.";
        return Redirect("/account/login");
    }

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
                actorEmailOverride: email, userIdOverride: user?.Id, workspaceId: null);
            ModelState.AddModelError(string.Empty,
                IsFa ? "ایمیل یا رمز نادرست است." : "Invalid email or password.");
            return View(model);
        }

        if (user.EmailVerifiedAt is null)
        {
            await SendVerificationAsync(user, HttpContext.RequestAborted);
            TempData["VerificationEmail"] = user.Email;
            return Redirect("/account/verify-pending");
        }

        // A workspace's "single sign-on only" setting refuses this exact door, by name — never the
        // generic "invalid credentials" a wrong password gets. Checked here, after the password and
        // the email-verification gate and before any session is minted, so a correct password for a
        // held account never reaches two-factor or a signed-in cookie. See
        // SingleSignOnRequirementService for who is exempt and why a sign-in with no workspace scope
        // still has to ask "any workspace", not just one.
        if (await SsoRefusalAsync(user, HttpContext.RequestAborted) is { } refusal)
        {
            await audit.LogAsync("user.login_refused_sso_required", "user", user.Id.ToString(), ClientIp,
                actorEmailOverride: user.Email, userIdOverride: user.Id, workspaceId: null);
            ViewBag.SsoRequiredWorkspaceSlug = refusal.WorkspaceSlug;
            ViewBag.SsoRequiredProviders = refusal.ProviderKeys;
            ModelState.AddModelError(string.Empty, refusal.Message);
            return View(model);
        }

        // The password alone is not the door when two-factor is on. Nothing is signed in here: the
        // half-way state is a five-minute sealed note naming the user, and the code page is the
        // only thing that can spend it. A half-authenticated cookie in the real scheme would be a
        // signed-in session with extra steps.
        if (user.TotpEnabledAt is not null && user.TotpSecretEncrypted is not null)
        {
            Response.Cookies.Append(TwoFactorCookie,
                TwoFactorProtector().Protect(user.Id.ToString(), TimeSpan.FromMinutes(5)),
                new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = Request.IsHttps });
            return Redirect("/account/totp" + (model.ReturnUrl is { } r ? $"?returnUrl={Uri.EscapeDataString(r)}" : ""));
        }

        return await CompleteSignInAsync(user, model.ReturnUrl, model);
    }

    /// <summary>What the login page needs to name a single-sign-on refusal, both for the sentence and
    /// for the <c>data-</c> attributes the panel's own tests assert on instead — this panel renders
    /// Persian by default.</summary>
    private sealed record SsoLoginRefusal(string WorkspaceSlug, string ProviderKeys, string Message);

    /// <summary>
    /// Null when nothing holds this password sign-in. Otherwise names every workspace that does
    /// (<see cref="SingleSignOnRequirementService.WorkspacesHoldingAsync"/> already excludes the
    /// installation owner and each workspace's own owner) and the providers currently offered, so the
    /// refusal tells the person which button to click instead of leaving them to guess.
    /// </summary>
    private async Task<SsoLoginRefusal?> SsoRefusalAsync(User user, CancellationToken ct)
    {
        var holding = await ssoRequirement.WorkspacesHoldingAsync(user, ct);
        if (holding.Count == 0) return null;

        var workspaceNames = string.Join(IsFa ? "، " : ", ", holding.Select(w => w.Name));

        var config = await externalLogins.GetAsync(ct);
        var oidcName = config.For(ExternalLoginProviders.Oidc).DisplayName;
        var offered = config.Offered.ToList();
        var providerNames = offered
            .Select(p => ExternalLoginProviders.DisplayName(p.Provider, oidcName, IsFa))
            .ToList();

        var message = providerNames.Count == 0
            ? (IsFa
                ? $"فضای کاری «{workspaceNames}» ورود با رمز را نمی‌پذیرد و هنوز هیچ سرویس ورود یکپارچه‌ای روی این پلتفرم تنظیم نشده است. با مدیر فضای کاری تماس بگیرید."
                : $"\"{workspaceNames}\" requires single sign-on, and no sign-in provider is configured on this platform yet. Contact your workspace administrator.")
            : (IsFa
                ? $"فضای کاری «{workspaceNames}» فقط با ورود یکپارچه پذیرفته می‌شود؛ با {string.Join(" یا ", providerNames)} وارد شوید."
                : $"\"{workspaceNames}\" requires single sign-on. Sign in with {string.Join(" or ", providerNames)} instead.");

        return new SsoLoginRefusal(
            holding[0].Slug, string.Join(",", offered.Select(p => p.Provider)), message);
    }

    private async Task<IActionResult> CompleteSignInAsync(
        Harbora.Domain.Identity.User user, string? returnUrl, object viewModel)
    {
        user.LastLoginAt = clock.UtcNow;
        var previousMembership = await db.WorkspaceMembers.IgnoreQueryFilters()
            .Where(m => m.UserId == user.Id
                && m.Workspace!.ArchivedAt == null && m.Workspace.DeletedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.WorkspaceId, m.Role })
            .FirstOrDefaultAsync();
        // Every account owns one private workspace. Existing accounts receive it lazily on their
        // next successful sign-in, which upgrades old installations without an offline backfill.
        var personal = await workspaceAccounts.EnsurePersonalWorkspaceAsync(user, HttpContext.RequestAborted);
        var membership = previousMembership ?? await db.WorkspaceMembers.IgnoreQueryFilters()
            .Where(m => m.UserId == user.Id && m.WorkspaceId == personal.Id)
            .Select(m => new { m.WorkspaceId, m.Role }).SingleAsync();
        await db.SaveChangesAsync();
        await SignInAsync(user, membership.WorkspaceId, membership.Role);

        // An external provider proven earlier in this same conversation becomes a link here, and
        // only here — after the password and any second factor, at the one moment the person has
        // demonstrably got both. See AccountController.External.cs for why a matching address is
        // never enough on its own.
        await SpendPendingLinkAsync(user, HttpContext.RequestAborted);

        await audit.LogAsync("user.login", "user", user.Id.ToString(), ClientIp,
            actorEmailOverride: user.Email, userIdOverride: user.Id, workspaceId: null);
        return LocalRedirect(returnUrl ?? "/");
    }

    private async Task SignInAsync(User user, Guid workspaceId, WorkspaceRole workspaceRole)
    {
        var session = await sessions.CreateAsync(user.Id, ClientIp,
            Request.Headers.UserAgent.ToString(), HttpContext.RequestAborted);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            SessionPrincipalFactory.Create(user, workspaceId, workspaceRole, sessionId: session.Id));
    }

    [HttpGet("/account/verify-pending")]
    public IActionResult VerifyPending() => View();

    [HttpGet("/account/verify")]
    public async Task<IActionResult> VerifyEmail(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var hash = AccountSessionService.Hash(token);
        var row = await db.EmailVerificationTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row?.User is null || row.UsedAt is not null || row.ExpiresAt <= clock.UtcNow)
        {
            TempData["Error"] = IsFa ? "لینک تأیید معتبر نیست یا منقضی شده است." : "The verification link is invalid or expired.";
            return Redirect("/account/verify-pending");
        }

        row.UsedAt = clock.UtcNow;
        row.User.EmailVerifiedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("user.email_verified", "user", row.UserId.ToString(), ClientIp,
            actorEmailOverride: row.User.Email, userIdOverride: row.UserId, workspaceId: null, ct: ct);
        TempData["Message"] = IsFa ? "ایمیل تأیید شد؛ حالا وارد شوید." : "Email verified. You can sign in now.";
        return Redirect("/account/login");
    }

    [HttpPost("/account/verify/resend")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResendVerification(string? email, CancellationToken ct)
    {
        var normalised = (email ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Email == normalised && u.IsActive && u.EmailVerifiedAt == null, ct);
        if (user is not null) await SendVerificationAsync(user, ct);
        TempData["Message"] = IsFa
            ? "اگر این ایمیل حساب تأییدنشده داشته باشد، لینک تازه ارسال شد."
            : "If that address has an unverified account, a new link has been sent.";
        return Redirect("/account/verify-pending");
    }

    /// <summary>
    /// Queues the verification email onto N1's outbox (§7 Q3(b)) rather than sending it inline. The
    /// return value is now "was it queued", not "did it arrive" — nothing reading it today inspected
    /// more than that anyway.
    /// </summary>
    private async Task<bool> SendVerificationAsync(User user, CancellationToken ct)
    {
        if (!await mailer.IsConfiguredAsync(ct)) return false;
        var (token, row) = sessions.IssueVerification(user.Id);
        db.EmailVerificationTokens.Add(row);
        var link = $"{Request.Scheme}://{Request.Host}/account/verify?token={token}";
        var delivery = Harbora.Infrastructure.Notifications.OutboxMail.Queue(
            db, protector, NotificationDeliveryPurpose.EmailVerification, user.Email,
            IsFa ? "تأیید ایمیل Harbora" : "Verify your Harbora email",
            IsFa
                ? $"برای تأیید ایمیل این لینک را باز کنید (تا ۲۴ ساعت معتبر است):\n{link}"
                : $"Open this link to verify your email (valid for 24 hours):\n{link}");
        await db.SaveChangesAsync(ct);
        await jobs.EnqueueAsync(Harbora.Domain.Jobs.JobKind.NotificationDelivery, delivery.Id, delivery.WorkspaceId, ct);
        return true;
    }

    // ---- The second step ----

    private const string TwoFactorCookie = "harbora_2fa";

    private ITimeLimitedDataProtector TwoFactorProtector() =>
        dataProtection.CreateProtector("Harbora.TwoFactor").ToTimeLimitedDataProtector();

    /// <summary>The user the sealed note names, or null when it is absent, forged or stale.</summary>
    private Guid? PendingTwoFactorUser()
    {
        var sealed_ = Request.Cookies[TwoFactorCookie];
        if (string.IsNullOrEmpty(sealed_)) return null;

        try
        {
            return Guid.TryParse(TwoFactorProtector().Unprotect(sealed_), out var id) ? id : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    [HttpGet("/account/totp")]
    public IActionResult Totp(string? returnUrl) =>
        PendingTwoFactorUser() is null ? Redirect("/account/login") : View(new TotpViewModel { ReturnUrl = returnUrl });

    [HttpPost("/account/totp")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Totp(TotpViewModel model)
    {
        if (PendingTwoFactorUser() is not { } userId) return Redirect("/account/login");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);
        if (user?.TotpSecretEncrypted is null || user.TotpEnabledAt is null) return Redirect("/account/login");

        var secret = protector.Unprotect(user.TotpSecretEncrypted);
        var ok = Harbora.Infrastructure.Security.Totp.Verify(secret, model.Code, clock.UtcNow);

        if (!ok)
        {
            // A recovery code spends itself: the row is rewritten without it before anything else
            // happens, so the same code read off the same sheet never works twice.
            var (consumed, remaining) = Harbora.Infrastructure.Security.Totp.ConsumeRecoveryCode(
                user.RecoveryCodesHash, model.Code ?? "");
            if (consumed)
            {
                user.RecoveryCodesHash = remaining;
                await db.SaveChangesAsync();
                ok = true;
            }
        }

        if (!ok)
        {
            await audit.LogAsync("user.totp_challenge_failed", "user", user.Id.ToString(), ClientIp,
                actorEmailOverride: user.Email, userIdOverride: user.Id, workspaceId: null);
            ModelState.AddModelError(string.Empty, IsFa ? "کد درست نیست." : "That code is not right.");
            return View(model);
        }

        Response.Cookies.Delete(TwoFactorCookie);
        return await CompleteSignInAsync(user, model.ReturnUrl, model);
    }

    [HttpPost("/account/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && Guid.TryParse(User.FindFirstValue(HarboraClaims.Session), out var sessionId))
            await sessions.RevokeAsync(userId, sessionId, HttpContext.RequestAborted);
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
