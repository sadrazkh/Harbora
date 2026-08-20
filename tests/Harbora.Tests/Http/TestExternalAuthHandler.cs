using System.Security.Claims;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Harbora.Tests;

/// <summary>
/// Stands in for Google, GitHub and an OpenID Connect provider at the exact seam where the panel
/// reads their answer.
///
/// <para><b>What this can and cannot prove.</b> There are no Google or GitHub credentials on this
/// machine, so a real round trip — consent screen, authorisation code, token exchange, userinfo — is
/// impossible here and remains unverified until the owner configures real credentials on the server.
/// What is provable, and what this handler exists to prove, is everything the panel itself decides:
/// the whole of <c>AccountController.External</c> runs against a genuine HTTP request, through the
/// real routing, antiforgery, rate limiting and Razor, with only the provider's word substituted.
/// The seam is deliberately the same one production uses — the external cookie scheme — rather than
/// a controller method called directly, so the callback's own reading of the principal is under test
/// too.</para>
///
/// <para>
/// It answers from request headers rather than from state, so one host serves every case: no header
/// means <see cref="AuthenticateResult.NoResult"/>, which is precisely what an absent or expired
/// external cookie looks like.
/// </para>
/// </summary>
public sealed class TestExternalAuthHandler : IAuthenticationSignInHandler
{
    public const string ProviderHeader = "X-Harbora-Test-Sso-Provider";
    public const string SubjectHeader = "X-Harbora-Test-Sso-Subject";
    public const string EmailHeader = "X-Harbora-Test-Sso-Email";
    public const string EmailVerifiedHeader = "X-Harbora-Test-Sso-Email-Verified";
    public const string NameHeader = "X-Harbora-Test-Sso-Name";

    /// <summary>"link" when the sign-in was started from account settings, otherwise "signin".</summary>
    public const string ModeHeader = "X-Harbora-Test-Sso-Mode";

    private AuthenticationScheme _scheme = null!;
    private HttpContext _context = null!;

    public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context)
    {
        _scheme = scheme;
        _context = context;
        return Task.CompletedTask;
    }

    public Task<AuthenticateResult> AuthenticateAsync()
    {
        var provider = Header(ProviderHeader);
        var subject = Header(SubjectHeader);
        if (provider is null) return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>();
        // A missing subject is a case under test, not a mistake: a provider that sends no identifier
        // must be refused rather than guessed at, so the header is allowed to be absent.
        if (subject is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        if (Header(EmailHeader) is { } email) claims.Add(new Claim(ClaimTypes.Email, email));
        if (Header(NameHeader) is { } name) claims.Add(new Claim(ClaimTypes.Name, name));
        claims.Add(new Claim(ExternalAuth.EmailVerifiedClaim,
            string.Equals(Header(EmailVerifiedHeader), "true", StringComparison.OrdinalIgnoreCase)
                ? "true"
                : "false"));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, _scheme.Name));
        var properties = new AuthenticationProperties();
        properties.Items[ExternalAuth.ProviderItem] = provider;
        properties.Items[ExternalAuth.ModeItem] = Header(ModeHeader) ?? ExternalAuth.ModeSignIn;

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, properties, _scheme.Name)));
    }

    /// <summary>The real scheme's cookie is deleted here; there is nothing to delete.</summary>
    public Task SignOutAsync(AuthenticationProperties? properties) => Task.CompletedTask;

    public Task SignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task ChallengeAsync(AuthenticationProperties? properties) => Task.CompletedTask;
    public Task ForbidAsync(AuthenticationProperties? properties) => Task.CompletedTask;

    private string? Header(string name) =>
        _context.Request.Headers.TryGetValue(name, out var value) && value.ToString() is { Length: > 0 } text
            ? text
            : null;
}

/// <summary>The request an external provider's callback looks like, once the provider has answered.</summary>
public static class ExternalSignInConversation
{
    /// <summary>
    /// Walks the panel's own callback with a provider's answer attached — the request the browser
    /// makes after the handler has put the external identity down.
    /// </summary>
    public static Task<HttpResponseMessage> ExternalCallbackAsync(
        this HttpClient client,
        string provider,
        string? subject,
        string? email = null,
        bool emailVerified = true,
        string? displayName = null,
        bool link = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ExternalAuth.CallbackPath);
        request.Headers.Add(TestExternalAuthHandler.ProviderHeader, provider);
        if (subject is not null) request.Headers.Add(TestExternalAuthHandler.SubjectHeader, subject);
        if (email is not null) request.Headers.Add(TestExternalAuthHandler.EmailHeader, email);
        if (displayName is not null) request.Headers.Add(TestExternalAuthHandler.NameHeader, displayName);
        request.Headers.Add(TestExternalAuthHandler.EmailVerifiedHeader, emailVerified ? "true" : "false");
        request.Headers.Add(TestExternalAuthHandler.ModeHeader,
            link ? ExternalAuth.ModeLink : ExternalAuth.ModeSignIn);

        return client.SendAsync(request);
    }
}
