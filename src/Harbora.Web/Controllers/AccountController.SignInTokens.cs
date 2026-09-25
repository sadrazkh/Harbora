using System.Security.Claims;
using Harbora.Domain.Authorization;
using Harbora.Domain.Identity;
using Harbora.Web.Infrastructure;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Minting and redeeming one-time sign-in tokens — the owner's-own-agent analogue of
/// <see cref="TenantsController.Support"/>.
///
/// <para>
/// The owner mints from inside the panel, sees the secret exactly once, and can revoke any token from
/// the same page. The redemption route on the other hand runs signed out — an agent presenting a token
/// has no cookie yet — and accepts the credential two ways: as a query string on the link the owner
/// copies (the shape the credential already travels in, per <see cref="SignInToken"/>'s own remark),
/// and as a POST form body, so a paste-the-token flow never has to put the secret in a URL at all.
/// </para>
/// </summary>
public sealed partial class AccountController
{
    private Guid CurrentUserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id
        : Guid.Empty;

    private Guid CurrentWorkspaceId => Guid.TryParse(User.FindFirstValue(HarboraClaims.Workspace), out var id)
        ? id
        : Guid.Empty;

    [HttpGet("/account/tokens")]
    [Authorize]
    public async Task<IActionResult> Tokens(CancellationToken ct)
    {
        ViewData["Title"] = IsFa ? "توکن‌های ورود یک‌بارمصرف" : "One-time sign-in tokens";
        return View(await TokensPageAsync(ct));
    }

    /// <summary>
    /// Mints a token scoped to the owner's own current workspace. Re-renders the same page either
    /// way: on refusal, with the purpose typed back in; on success, with the plaintext shown exactly
    /// once. Never redirected through — TempData is no place for a secret to sit even briefly.
    ///
    /// <para>
    /// Refused under a support session for the same reason minting an API/CLI token already is
    /// (<see cref="SupportRestrictedAct.ApiToken"/>, reused rather than given a sibling enum value —
    /// the customer-facing vocabulary is "a durable credential", and this is exactly that): a token
    /// redeemed during the impersonated hour would go on granting a session after the support session
    /// itself has ended, which is precisely the durable, self-owned way into the account the existing
    /// restriction exists to refuse.
    /// </para>
    /// </summary>
    [HttpPost("/account/tokens")]
    [Authorize]
    [ValidateAntiForgeryToken]
    [RefuseUnderSupportSession(SupportRestrictedAct.ApiToken)]
    public async Task<IActionResult> CreateToken(string? purpose, CancellationToken ct)
    {
        ViewData["Title"] = IsFa ? "توکن‌های ورود یک‌بارمصرف" : "One-time sign-in tokens";

        var issue = await signInTokens.IssueAsync(CurrentUserId, CurrentWorkspaceId, purpose, ct);
        if (!issue.Ok)
        {
            var refused = await TokensPageAsync(ct, error: issue.Refusal, purposeEntered: purpose);
            return View(nameof(Tokens), refused);
        }

        await audit.LogAsync("signin_token.created", "signin_token", issue.Token!.Id.ToString(), ClientIp,
            workspaceId: issue.Token.WorkspaceId);

        var minted = await TokensPageAsync(ct,
            mintedPlaintext: issue.PlaintextToken, mintedPurpose: issue.Token.Purpose);
        return View(nameof(Tokens), minted);
    }

    [HttpPost("/account/tokens/{id:guid}/revoke")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeToken(Guid id, CancellationToken ct)
    {
        var revoked = await signInTokens.RevokeAsync(CurrentUserId, id, ct);
        if (revoked is not null)
            await audit.LogAsync("signin_token.revoked", "signin_token", id.ToString(), ClientIp,
                workspaceId: revoked.WorkspaceId);

        return Redirect("/account/tokens");
    }

    /// <summary>The link path: a token riding in the query string, exactly where the owner's copy
    /// button put it. Redeems immediately, the same shape <c>VerifyEmail</c> already uses for a
    /// bare-GET one-time credential.</summary>
    [HttpGet("/account/token-signin")]
    public async Task<IActionResult> TokenSignIn(string? token, CancellationToken ct)
    {
        ViewData["Title"] = IsFa ? "ورود با توکن" : "Sign in with a token";

        if (string.IsNullOrWhiteSpace(token))
            return View(new TokenSignInViewModel());

        return await RedeemAndSignInAsync(token, ct);
    }

    /// <summary>The form path: the same redemption, taken from a POST body instead of the query
    /// string, so a paste-the-token flow never puts the credential in a URL. A distinct method name
    /// from the GET above only because C# overload resolution ignores nullable-reference annotations —
    /// both bind to the same route and the same form field name either way.</summary>
    [HttpPost("/account/token-signin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitTokenSignIn(string token, CancellationToken ct) =>
        await RedeemAndSignInAsync(token, ct);

    private async Task<IActionResult> RedeemAndSignInAsync(string? token, CancellationToken ct)
    {
        var redemption = await signInTokens.RedeemAsync(
            token, ClientIp, Request.Headers.UserAgent.ToString(), ct);
        if (!redemption.Ok)
        {
            ViewData["Title"] = IsFa ? "ورود با توکن" : "Sign in with a token";
            return View(new TokenSignInViewModel { Error = redemption.Refusal });
        }

        var opened = redemption.Token!;
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == opened.UserId, ct);
        var membership = await db.WorkspaceMembers.IgnoreQueryFilters()
            .FirstAsync(m => m.UserId == opened.UserId && m.WorkspaceId == opened.WorkspaceId, ct);

        var principal = SessionPrincipalFactory.Create(
            user, opened.WorkspaceId, membership.Role,
            sessionId: redemption.Session!.Id, signInToken: opened);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        // Mirrors TenantsController.Support.StartSupport's own ordering exactly, and for the identical
        // reason: SignInAsync writes the cookie but does not change who this in-flight request thinks
        // it is, and the audit row below must be attributed to the token session it opens rather than
        // written a half-step too early.
        HttpContext.User = principal;

        await audit.LogAsync("session.started", "workspace", opened.WorkspaceId.ToString(), ClientIp,
            workspaceId: opened.WorkspaceId,
            metadataJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                purpose = opened.Purpose,
                sessionExpiresAt = opened.SessionExpiresAt!.Value.ToUnixTimeSeconds()
            }), ct: ct);

        return Redirect("/");
    }

    private async Task<SignInTokensPageViewModel> TokensPageAsync(
        CancellationToken ct, string? error = null, string? purposeEntered = null,
        string? mintedPlaintext = null, string? mintedPurpose = null)
    {
        var now = clock.UtcNow;
        var tokens = await signInTokens.ForOwnerAsync(CurrentUserId, take: 200, ct);

        return new SignInTokensPageViewModel
        {
            Tokens = tokens.Select(t => new SignInTokenRow(
                t.Id, t.Purpose, t.CreatedAt, t.ExpiresAt, t.RedeemedAt, t.SessionExpiresAt, t.RevokedAt,
                t.StatusAt(now))).ToList(),
            MaxPurposeLength = SignInTokenAccess.MaxPurposeLength,
            RedemptionLifetimeMinutes = (int)SignInTokenAccess.RedemptionLifetime.TotalMinutes,
            SessionLifetimeMinutes = (int)SignInTokenAccess.SessionLifetime.TotalMinutes,
            Purpose = purposeEntered,
            Error = error,
            MintedPlaintext = mintedPlaintext,
            MintedPurpose = mintedPurpose
        };
    }
}
