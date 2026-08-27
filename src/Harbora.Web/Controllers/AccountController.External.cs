using System.Security.Claims;
using System.Text.Json;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Security;
using Harbora.Web.Infrastructure;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Signing in with Google, GitHub or an operator's own OpenID Connect provider, and connecting one
/// to an account that already exists.
///
/// <para><b>The rule this file exists to enforce.</b> An external identity whose address matches an
/// account here is <i>not</i> that account. Providers hand out addresses, take them back, and let
/// them be re-registered; an account that could be entered by anyone who ends up holding a mailbox is
/// not protected by its password at all. So a match by address buys exactly one thing: the offer to
/// prove the password and connect the two deliberately. The link is written after the proof, never
/// before it.</para>
///
/// <para><b>And the rule about leaving.</b> Unlinking refuses when it would leave an account with no
/// way in — no password the sign-in form would accept and no other provider. There is no
/// administrator-side "set somebody else's password" here to undo that with.</para>
/// </summary>
public sealed partial class AccountController
{
    private const string PendingLinkCookie = "harbora_link";

    /// <summary>
    /// Long enough to type a password and answer a two-factor prompt, short enough that an abandoned
    /// attempt is not an offer left standing.
    /// </summary>
    private static readonly TimeSpan PendingLinkLifetime = TimeSpan.FromMinutes(10);

    private ITimeLimitedDataProtector PendingLinkProtector() =>
        dataProtection.CreateProtector("Harbora.ExternalLink").ToTimeLimitedDataProtector();

    /// <summary>The offer made to somebody who has proven a provider but not yet the password.</summary>
    private sealed record PendingLink(Guid UserId, string Provider, string Subject, string? Email, string? DisplayName);

    /// <summary>
    /// The providers with a button, named the way they will be shown. Empty on every install where
    /// nobody has configured one, which is the shipped state.
    /// </summary>
    private async Task<IReadOnlyList<ExternalProviderButton>> OfferedProvidersAsync(CancellationToken ct)
    {
        var config = await externalLogins.GetAsync(ct);
        var oidcName = config.For(ExternalLoginProviders.Oidc).DisplayName;
        return config.Offered
            .Select(p => new ExternalProviderButton(
                p.Provider, ExternalLoginProviders.DisplayName(p.Provider, oidcName, IsFa)))
            .ToList();
    }

    // ---- starting ------------------------------------------------------------------------------

    /// <summary>
    /// Sends the browser to a provider.
    ///
    /// <para>
    /// A POST rather than a link, so a page on another site cannot start a sign-in on somebody's
    /// behalf, and so the mode below is a decision this panel made rather than one in a URL.
    /// </para>
    /// </summary>
    [HttpPost("/account/external/{provider}/start")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    // Linking an identity mints a durable, self-owned way into somebody else's account — what an API
    // token is, and longer-lived — and unlinking takes one of the customer's own ways in away. Every
    // route in this file is therefore closed to a support session, the callback included, since that
    // is where the link is actually written.
    [RefuseUnderSupportSession(Harbora.Domain.Authorization.SupportRestrictedAct.ExternalLogin)]
    public async Task<IActionResult> ExternalStart(string provider, string? returnUrl, bool link, CancellationToken ct)
    {
        var key = ExternalLoginProviders.Normalise(provider);
        if (key is null) return NotFound();

        // Configured is checked here, not merely hidden on the page: the button's absence is a
        // rendering decision and this is the one that actually refuses.
        var config = (await externalLogins.GetAsync(ct)).For(key);
        if (!config.IsConfigured)
        {
            TempData["Error"] = IsFa
                ? "این روش ورود روی این پلتفرم تنظیم نشده است."
                : "That sign-in method is not configured on this platform.";
            return Redirect(link ? "/settings" : "/account/login");
        }

        // Linking is something a signed-in person does to their own account. Asking for it while
        // signed out would make the callback guess whose account was meant.
        if (link && User.Identity?.IsAuthenticated != true) return Redirect("/account/login");

        var properties = new AuthenticationProperties
        {
            RedirectUri = ExternalAuth.CallbackPath,
            Items =
            {
                [ExternalAuth.ProviderItem] = key,
                [ExternalAuth.ModeItem] = link ? ExternalAuth.ModeLink : ExternalAuth.ModeSignIn,
                [ExternalAuth.ReturnUrlItem] = returnUrl
            }
        };

        return Challenge(properties, ExternalAuth.SchemeFor(key));
    }

    // ---- the callback --------------------------------------------------------------------------

    /// <summary>
    /// Where a provider's answer becomes a decision. Every branch below ends in a sign-in, an offer,
    /// a new account, or a refusal in words — never in a link nobody asked for.
    /// </summary>
    [HttpGet(ExternalAuth.CallbackPath)]
    [RefuseUnderSupportSession(Harbora.Domain.Authorization.SupportRestrictedAct.ExternalLogin)]
    public async Task<IActionResult> ExternalCallback(CancellationToken ct)
    {
        var result = await HttpContext.AuthenticateAsync(ExternalAuth.ExternalScheme);
        if (!result.Succeeded || result.Principal is null)
            return ExternalRefusal(IsFa
                ? "ورود از این سرویس کامل نشد. دوباره تلاش کنید."
                : "That sign-in did not complete. Try again.");

        var properties = result.Properties;
        var provider = ExternalLoginProviders.Normalise(Item(properties, ExternalAuth.ProviderItem));
        if (provider is null)
            return ExternalRefusal(IsFa ? "این سرویس شناخته نشد." : "That provider is not one of ours.");

        var identity = ExternalAuth.Read(provider, result.Principal);

        // The temporary cookie has done its whole job by now. It is cleared before anything else so
        // no branch below can leave a half-finished sign-in lying around in the browser.
        await HttpContext.SignOutAsync(ExternalAuth.ExternalScheme);

        if (identity is null)
            return ExternalRefusal(IsFa
                ? "این سرویس شناسه‌ای برای شما نفرستاد."
                : "That provider sent no account identifier.");

        var linking = Item(properties, ExternalAuth.ModeItem) == ExternalAuth.ModeLink;
        var returnUrl = Item(properties, ExternalAuth.ReturnUrlItem);

        return linking
            ? await LinkToSignedInAccountAsync(identity, ct)
            : await SignInWithExternalAsync(identity, returnUrl, ct);
    }

    /// <summary>One of the notes the challenge left for its own callback, or null.</summary>
    private static string? Item(AuthenticationProperties? properties, string key) =>
        properties is not null && properties.Items.TryGetValue(key, out var value) ? value : null;

    private async Task<IActionResult> SignInWithExternalAsync(
        ExternalAuth.Identity identity, string? returnUrl, CancellationToken ct)
    {
        var existing = await db.ExternalLogins.IgnoreQueryFilters()
            .Include(l => l.User)
            .FirstOrDefaultAsync(l => l.Provider == identity.Provider && l.Subject == identity.Subject, ct);

        // (1) Already connected. This is the only path that signs somebody in without a password,
        // and it is exactly the one where they proved the password once already, when they linked.
        if (existing?.User is not null)
        {
            if (!existing.User.IsActive)
                return ExternalRefusal(IsFa ? "این حساب غیرفعال است." : "That account is not active.");

            await audit.LogAsync("user.external_login", "user", existing.UserId.ToString(), ClientIp,
                actorEmailOverride: existing.User.Email, userIdOverride: existing.UserId, workspaceId: null, ct: ct);

            return await ContinueSignInAsync(existing.User, returnUrl);
        }

        // (2) An address with no account, and nothing to link. What happens next is whatever
        // registering here does — established by reading the register action's own guards, since no
        // AllowRegistration setting exists: POST /account/register accepts a registration with no
        // invitation, so this platform's registration is open and an unknown identity becomes an
        // account. ExternalRegistrationMirrorsRegisterTests fails if that ever stops being true.
        if (identity.Email is null)
            return ExternalRefusal(IsFa
                ? "این سرویس نشانی ایمیلی با Harbora به اشتراک نگذاشت، و بدون آن حسابی ساخته نمی‌شود."
                : "That provider shared no email address with Harbora, and an account cannot be made without one.");

        string email;
        try { email = WorkspaceAccountService.NormalizeEmail(identity.Email); }
        catch (ArgumentException)
        {
            return ExternalRefusal(IsFa
                ? "نشانی ایمیلی که این سرویس فرستاد معتبر نیست."
                : "The email address that provider sent is not a valid one.");
        }

        var owner = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == email, ct);

        // (3) The match that must not become a link. An account exists at this address and this
        // provider has never been connected to it. Whoever is holding the mailbox today is not
        // thereby the person who set the password, so the offer is to prove it.
        if (owner is not null)
        {
            if (!owner.IsActive)
                return ExternalRefusal(IsFa ? "این حساب غیرفعال است." : "That account is not active.");

            Response.Cookies.Append(PendingLinkCookie,
                PendingLinkProtector().Protect(
                    JsonSerializer.Serialize(new PendingLink(
                        owner.Id, identity.Provider, identity.Subject, identity.Email, identity.DisplayName)),
                    PendingLinkLifetime),
                new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = Request.IsHttps,
                    IsEssential = true
                });

            return Redirect("/account/external/confirm");
        }

        return await ProvisionFromExternalAsync(identity, email, returnUrl, ct);
    }

    /// <summary>
    /// Creates the account an unknown external identity gets, mirroring
    /// <see cref="Register(RegisterViewModel, CancellationToken)"/> line for line — including the part
    /// people forget, which is that a public registration here does not sign anybody in until the
    /// address is proven.
    ///
    /// <para>
    /// The one thing read differently: a provider that says it has verified the address has proven
    /// control of it at least as well as a link sent to it would, so that account starts verified.
    /// A provider that says nothing gets the same emailed link a public registration gets — an
    /// unverified claim is not evidence, and treating it as evidence is how an account ends up
    /// trusted for a mailbox nobody proved.
    /// </para>
    /// </summary>
    private async Task<IActionResult> ProvisionFromExternalAsync(
        ExternalAuth.Identity identity, string email, string? returnUrl, CancellationToken ct)
    {
        var user = new User
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(identity.DisplayName) ? email : identity.DisplayName.Trim(),
            // No password, ever set. Pbkdf2PasswordHasher.Verify reads an empty hash as a refusal, so
            // the sign-in form can never let anybody in here — and ExternalLoginPolicy asks this same
            // question before allowing the provider to be unlinked again.
            PasswordHash = string.Empty,
            Role = SystemRole.Member,
            PreferredCulture = IsFa ? "fa" : "en",
            LastLoginAt = clock.UtcNow,
            EmailVerifiedAt = identity.EmailVerified ? clock.UtcNow : null
        };

        db.Users.Add(user);
        db.ExternalLogins.Add(new ExternalLogin
        {
            Provider = identity.Provider,
            Subject = identity.Subject,
            UserId = user.Id,
            LinkedAt = clock.UtcNow,
            Email = identity.Email,
            DisplayName = identity.DisplayName
        });
        await db.SaveChangesAsync(ct);

        await workspaceAccounts.EnsurePersonalWorkspaceAsync(user, ct);

        if (user.EmailVerifiedAt is null)
        {
            await SendVerificationAsync(user, ct);
            TempData["VerificationEmail"] = user.Email;
            await audit.LogAsync("user.registered_pending_verification", "user", user.Id.ToString(), ClientIp,
                actorEmailOverride: user.Email, userIdOverride: user.Id, workspaceId: null, ct: ct);
            return Redirect("/account/verify-pending");
        }

        await audit.LogAsync("user.registered", "user", user.Id.ToString(), ClientIp,
            actorEmailOverride: user.Email, userIdOverride: user.Id, workspaceId: null, ct: ct);
        return await ContinueSignInAsync(user, returnUrl);
    }

    /// <summary>
    /// Finishes an external sign-in the same way a password one finishes — which means the local
    /// two-factor prompt still stands in the way.
    ///
    /// <para>
    /// Conservatively, and on purpose: whether the provider asked for a second factor, and how well,
    /// is not something this panel is told or can check. Somebody who turned two-factor on here asked
    /// this panel for it, not Google.
    /// </para>
    /// </summary>
    private async Task<IActionResult> ContinueSignInAsync(User user, string? returnUrl)
    {
        if (user.TotpEnabledAt is not null && user.TotpSecretEncrypted is not null)
        {
            Response.Cookies.Append(TwoFactorCookie,
                TwoFactorProtector().Protect(user.Id.ToString(), TimeSpan.FromMinutes(5)),
                new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = Request.IsHttps });
            return Redirect("/account/totp" +
                            (returnUrl is { } r ? $"?returnUrl={Uri.EscapeDataString(r)}" : ""));
        }

        // The same refusal a password sign-in meets: an address nobody proved is not a way in yet.
        if (user.EmailVerifiedAt is null)
        {
            await SendVerificationAsync(user, HttpContext.RequestAborted);
            TempData["VerificationEmail"] = user.Email;
            return Redirect("/account/verify-pending");
        }

        return await CompleteSignInAsync(user, returnUrl, new LoginViewModel());
    }

    // ---- proving the password before the link ---------------------------------------------------

    [HttpGet("/account/external/confirm")]
    [RefuseUnderSupportSession(Harbora.Domain.Authorization.SupportRestrictedAct.ExternalLogin)]
    public async Task<IActionResult> ExternalConfirm(CancellationToken ct)
    {
        var (pending, user) = await ReadPendingLinkAsync(ct);
        if (pending is null || user is null) return Redirect("/account/login");

        return View("ExternalConfirm", await ConfirmModelAsync(pending, user, ct));
    }

    [HttpPost("/account/external/confirm")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    [RefuseUnderSupportSession(Harbora.Domain.Authorization.SupportRestrictedAct.ExternalLogin)]
    public async Task<IActionResult> ExternalConfirm(string? password, CancellationToken ct)
    {
        var (pending, user) = await ReadPendingLinkAsync(ct);
        if (pending is null || user is null) return Redirect("/account/login");

        if (!hasher.Verify(password ?? "", user.PasswordHash))
        {
            await audit.LogAsync("user.external_link_password_failed", "user", user.Id.ToString(), ClientIp,
                actorEmailOverride: user.Email, userIdOverride: user.Id, workspaceId: null, ct: ct);

            var model = await ConfirmModelAsync(pending, user, ct);
            ModelState.AddModelError(string.Empty, IsFa ? "رمز نادرست است." : "That password is not right.");
            return View("ExternalConfirm", model);
        }

        // Not linked here. The offer travels on to the two-factor prompt when there is one, and
        // CompleteSignInAsync spends it once the person is really signed in — so a correct password
        // alone never connects a provider to an account that also wanted a second factor.
        return await ContinueSignInAsync(user, returnUrl: null);
    }

    private async Task<ExternalConfirmViewModel> ConfirmModelAsync(PendingLink pending, User user, CancellationToken ct)
    {
        var oidcName = (await externalLogins.GetAsync(ct)).For(ExternalLoginProviders.Oidc).DisplayName;
        return new ExternalConfirmViewModel
        {
            Provider = pending.Provider,
            ProviderName = ExternalLoginProviders.DisplayName(pending.Provider, oidcName, IsFa),
            Email = user.Email
        };
    }

    /// <summary>The offer this browser is carrying, and whose account it names, or (null, null).</summary>
    private async Task<(PendingLink? Pending, User? User)> ReadPendingLinkAsync(CancellationToken ct)
    {
        var sealed_ = Request.Cookies[PendingLinkCookie];
        if (string.IsNullOrEmpty(sealed_)) return (null, null);

        PendingLink? pending;
        try
        {
            pending = JsonSerializer.Deserialize<PendingLink>(PendingLinkProtector().Unprotect(sealed_));
        }
        catch (System.Security.Cryptography.CryptographicException) { return (null, null); }
        catch (JsonException) { return (null, null); }

        if (pending is null) return (null, null);

        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == pending.UserId && u.IsActive, ct);
        return user is null ? (null, null) : (pending, user);
    }

    /// <summary>
    /// Turns a proven offer into a row, at the one moment the person is demonstrably both the holder
    /// of the provider account and the holder of this one. Called from
    /// <c>CompleteSignInAsync</c> so it happens on the password path and the two-factor path alike.
    /// </summary>
    private async Task SpendPendingLinkAsync(User user, CancellationToken ct)
    {
        var (pending, _) = await ReadPendingLinkAsync(ct);
        Response.Cookies.Delete(PendingLinkCookie);

        // A different person finishing a sign-in in this browser must not inherit the offer.
        if (pending is null || pending.UserId != user.Id) return;

        var taken = await db.ExternalLogins.IgnoreQueryFilters().AnyAsync(
            l => (l.Provider == pending.Provider && l.Subject == pending.Subject)
                 || (l.UserId == user.Id && l.Provider == pending.Provider), ct);
        if (taken) return;

        db.ExternalLogins.Add(new ExternalLogin
        {
            Provider = pending.Provider,
            Subject = pending.Subject,
            UserId = user.Id,
            LinkedAt = clock.UtcNow,
            Email = pending.Email,
            DisplayName = pending.DisplayName
        });
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("user.external_login_linked", "user", user.Id.ToString(), ClientIp,
            actorEmailOverride: user.Email, userIdOverride: user.Id, workspaceId: null, ct: ct);
    }

    // ---- linking and unlinking from account settings --------------------------------------------

    private async Task<IActionResult> LinkToSignedInAccountAsync(ExternalAuth.Identity identity, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Redirect("/account/login");

        var existing = await db.ExternalLogins.IgnoreQueryFilters().FirstOrDefaultAsync(
            l => l.Provider == identity.Provider && l.Subject == identity.Subject, ct);

        if (existing is not null)
        {
            TempData[existing.UserId == userId ? "Message" : "Error"] = existing.UserId == userId
                ? (IsFa ? "این حساب از قبل وصل بود." : "That account was already connected.")
                : (IsFa
                    ? "این حساب به حساب دیگری در Harbora وصل است."
                    : "That account is connected to a different Harbora account.");
            return Redirect("/settings");
        }

        if (await db.ExternalLogins.IgnoreQueryFilters()
                .AnyAsync(l => l.UserId == userId && l.Provider == identity.Provider, ct))
        {
            TempData["Error"] = IsFa
                ? "برای این سرویس از قبل حسابی وصل است؛ اول آن را جدا کنید."
                : "Another account from that provider is already connected. Disconnect it first.";
            return Redirect("/settings");
        }

        db.ExternalLogins.Add(new ExternalLogin
        {
            Provider = identity.Provider,
            Subject = identity.Subject,
            UserId = userId,
            LinkedAt = clock.UtcNow,
            Email = identity.Email,
            DisplayName = identity.DisplayName
        });
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("user.external_login_linked", "user", userId.ToString(), ClientIp,
            userIdOverride: userId, workspaceId: null, ct: ct);

        TempData["Message"] = IsFa ? "وصل شد." : "Connected.";
        return Redirect("/settings");
    }

    /// <summary>
    /// Disconnects a provider — unless doing so would leave the account with nothing to sign in
    /// with. That refusal is the whole point: an account provisioned by a provider has no password
    /// to fall back on, and nobody here can give it one.
    /// </summary>
    [HttpPost("/account/external/{provider}/unlink")]
    [ValidateAntiForgeryToken]
    [Authorize]
    [RefuseUnderSupportSession(Harbora.Domain.Authorization.SupportRestrictedAct.ExternalLogin)]
    public async Task<IActionResult> ExternalUnlink(string provider, CancellationToken ct)
    {
        var key = ExternalLoginProviders.Normalise(provider);
        if (key is null) return NotFound();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Redirect("/account/login");

        var row = await db.ExternalLogins.IgnoreQueryFilters()
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Provider == key, ct);
        if (row is null) return Redirect("/settings");

        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        var others = await db.ExternalLogins.IgnoreQueryFilters()
            .CountAsync(l => l.UserId == userId && l.Provider != key, ct);

        if (ExternalLoginPolicy.WouldLeaveNoWayIn(
                ExternalLoginPolicy.HasUsablePassword(user.PasswordHash), others))
        {
            TempData["Error"] = IsFa
                ? "این تنها راه ورود به این حساب است. اول رمزی بگذارید یا سرویس دیگری وصل کنید."
                : "This is the only way into this account. Set a password or connect another provider first.";
            return Redirect("/settings");
        }

        db.ExternalLogins.Remove(row);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("user.external_login_unlinked", "user", userId.ToString(), ClientIp,
            userIdOverride: userId, workspaceId: null, ct: ct);

        TempData["Message"] = IsFa ? "جدا شد." : "Disconnected.";
        return Redirect("/settings");
    }

    /// <summary>A refusal the person can read, on the page they started from.</summary>
    private IActionResult ExternalRefusal(string message)
    {
        TempData["Error"] = message;
        return Redirect("/account/login");
    }
}
