using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Infrastructure;

/// <summary>The scheme names external sign-in uses, and the claim names it reads back.</summary>
public static class ExternalAuth
{
    /// <summary>
    /// The short-lived cookie holding the provider's answer between its callback and ours.
    ///
    /// <para>
    /// It is not the session: the panel's own sign-in cookie is only issued once the rules in
    /// <c>AccountController.External</c> have run. A handler that signed straight into the real
    /// scheme would make "an external identity arrived" and "this person is signed in" the same
    /// event, which is exactly the silent-link this feature must not do.
    /// </para>
    /// </summary>
    public const string ExternalScheme = "Harbora.External";

    /// <summary>The authentication scheme for one provider. Distinct from the provider key itself
    /// so nothing can confuse a stored <c>ExternalLogin.Provider</c> value with a scheme name.</summary>
    public static string SchemeFor(string provider) => "harbora-sso-" + provider;

    /// <summary>Where the panel's own callback lives, once a provider has answered.</summary>
    public const string CallbackPath = "/account/external/callback";

    /// <summary>Whether this sign-in was started from the sign-in page or from account settings.</summary>
    public const string ModeItem = "harbora:mode";
    public const string ModeSignIn = "signin";
    public const string ModeLink = "link";
    public const string ReturnUrlItem = "harbora:returnUrl";
    public const string ProviderItem = "harbora:provider";

    /// <summary>
    /// The provider's own word on whether it has proven the address. Standard OIDC spells it this
    /// way and Google's userinfo document agrees; GitHub is asked separately (see below).
    /// </summary>
    public const string EmailVerifiedClaim = "email_verified";

    /// <summary>
    /// What arrived from a provider, reduced to the five things the linking rules actually read.
    /// </summary>
    public sealed record Identity(
        string Provider, string Subject, string? Email, bool EmailVerified, string? DisplayName);

    /// <summary>
    /// Reads a provider's principal into <see cref="Identity"/>, or null when it carries no subject —
    /// which is the one thing that makes an answer unusable, since the subject is what the row is
    /// keyed on.
    /// </summary>
    public static Identity? Read(string provider, ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject)) return null;

        var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");
        var verified = principal.FindAll(EmailVerifiedClaim)
            .Any(c => string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase));

        var name = principal.FindFirstValue(ClaimTypes.Name)
                   ?? principal.FindFirstValue("name")
                   ?? principal.FindFirstValue("login");

        return new Identity(provider, subject.Trim(), Blank(email)?.Trim().ToLowerInvariant(), verified, Blank(name));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Lets an operator's change on the admin page take effect without restarting the panel.
///
/// <para>
/// <see cref="IOptionsMonitor{T}"/> caches each scheme's options for the life of the process, so the
/// first sign-in after a client id is typed in would otherwise keep using whatever was configured at
/// boot — including nothing. Clearing the cache is what makes the settings form's "Saved" true.
/// </para>
/// </summary>
public sealed class ExternalLoginSchemeCache(
    IOptionsMonitorCache<GoogleOptions> google,
    IOptionsMonitorCache<OAuthOptions> oauth,
    IOptionsMonitorCache<OpenIdConnectOptions> oidc)
{
    public void Forget()
    {
        google.Clear();
        oauth.Clear();
        oidc.Clear();
    }
}

public static class ExternalAuthenticationRegistration
{
    /// <summary>
    /// Registers the three providers the owner chose, with their credentials read from the settings
    /// table rather than from configuration files.
    ///
    /// <para><b>Why they are registered even when nobody has configured them.</b>
    /// <c>UseAuthentication</c> initialises every remote handler on <i>every</i> request — that is how
    /// a provider's callback path gets served at all — and initialising one runs
    /// <c>Options.Validate()</c>, which throws on an empty client id. Registering conditionally is
    /// impossible (the credentials live in a database the container has not opened yet at this point
    /// in <c>Program.cs</c>), so an unconfigured provider is given deliberately unusable placeholders
    /// instead: the options object is valid, the pipeline stays up, no button is rendered, and
    /// <c>AccountController</c> refuses to challenge the scheme. The placeholder can never reach a
    /// provider because nothing ever challenges with it.</para>
    /// </summary>
    public static AuthenticationBuilder AddHarboraExternalLogin(this AuthenticationBuilder builder)
    {
        builder.Services.AddScoped<ExternalLoginSettingsService>();
        builder.Services.AddSingleton<ExternalLoginSchemeCache>();
        builder.Services.AddSingleton<IConfigureOptions<GoogleOptions>, ExternalProviderOptionsSetup>();
        builder.Services.AddSingleton<IConfigureOptions<OAuthOptions>, ExternalProviderOptionsSetup>();
        builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, ExternalProviderOptionsSetup>();

        builder.AddCookie(ExternalAuth.ExternalScheme, o =>
        {
            o.Cookie.Name = "harbora_external";
            o.Cookie.HttpOnly = true;
            // Lax, not Strict: the provider sends the browser back across sites and a Strict cookie
            // would not be offered on that navigation, turning every sign-in into "correlation failed".
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.IsEssential = true;
            // Long enough to cross a provider's consent screen, short enough that an abandoned
            // half-finished sign-in is not lying around. It is never a session.
            o.ExpireTimeSpan = TimeSpan.FromMinutes(15);
            o.SlidingExpiration = false;
        });

        builder.AddGoogle(ExternalAuth.SchemeFor(ExternalLoginProviders.Google), o =>
        {
            o.SignInScheme = ExternalAuth.ExternalScheme;
            o.CallbackPath = "/signin-google";
            o.SaveTokens = false;

            // Google's userinfo document says whether it has proven the address. Without these the
            // panel would have to assume a verification it was never told about, and assuming it is
            // how an auto-provisioned account ends up trusted for a mailbox nobody proved.
            o.ClaimActions.MapJsonKey(ExternalAuth.EmailVerifiedClaim, "email_verified");
            o.ClaimActions.MapJsonKey(ExternalAuth.EmailVerifiedClaim, "verified_email");
            o.Events.OnRemoteFailure = RemoteFailure;
        });

        // GitHub speaks plain OAuth 2 and publishes no OpenID Connect discovery document, so its
        // endpoints are named here rather than discovered.
        builder.AddOAuth<OAuthOptions, OAuthHandler<OAuthOptions>>(
            ExternalAuth.SchemeFor(ExternalLoginProviders.GitHub), o =>
        {
            o.SignInScheme = ExternalAuth.ExternalScheme;
            o.CallbackPath = "/signin-github";
            o.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            o.TokenEndpoint = "https://github.com/login/oauth/access_token";
            o.UserInformationEndpoint = "https://api.github.com/user";
            o.SaveTokens = false;
            o.Scope.Add("read:user");
            // Asked for because GitHub's profile carries only the address a person chose to make
            // public — often none at all — and an account cannot be created without one.
            o.Scope.Add("user:email");
            o.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
            o.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
            o.ClaimActions.MapJsonKey("login", "login");
            o.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
            o.Events.OnCreatingTicket = GitHubProfileAsync;
            o.Events.OnRemoteFailure = RemoteFailure;
        });

        builder.AddOpenIdConnect(ExternalAuth.SchemeFor(ExternalLoginProviders.Oidc), o =>
        {
            o.SignInScheme = ExternalAuth.ExternalScheme;
            o.CallbackPath = "/signin-oidc";
            o.ResponseType = "code";
            o.UsePkce = true;
            o.SaveTokens = false;
            o.GetClaimsFromUserInfoEndpoint = true;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.Scope.Add("email");
            o.Events.OnRemoteFailure = RemoteFailure;
        });

        return builder;
    }

    /// <summary>
    /// A provider that answered with a refusal, or a browser that lost its correlation cookie, must
    /// land back on the sign-in page saying so — not on a framework exception page on a route the
    /// person never typed. The reason travels in the query string rather than TempData, which
    /// re-types what it carries across a redirect.
    /// </summary>
    private static Task RemoteFailure(RemoteFailureContext context)
    {
        context.Response.Redirect("/account/login?sso=failed");
        context.HandleResponse();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fetches the GitHub profile, then the verified primary address.
    ///
    /// <para>
    /// Plain <c>AddOAuth</c> does not call the user information endpoint by itself — unlike
    /// <c>AddGoogle</c>, whose handler does — so without this the ticket would carry no subject at all.
    /// The second call exists because GitHub answers <c>email</c> on the profile only when the person
    /// made it public, and never says whether it is verified; <c>/user/emails</c> is the only place
    /// that does.
    /// </para>
    /// </summary>
    private static async Task GitHubProfileAsync(OAuthCreatingTicketContext context)
    {
        var profile = await GetJsonAsync(context, context.Options.UserInformationEndpoint);
        context.RunClaimActions(profile.RootElement);

        try
        {
            using var addresses = await GetJsonAsync(context, "https://api.github.com/user/emails");
            var primary = addresses.RootElement.EnumerateArray().FirstOrDefault(entry =>
                entry.TryGetProperty("primary", out var isPrimary) && isPrimary.ValueKind == JsonValueKind.True &&
                entry.TryGetProperty("verified", out var isVerified) && isVerified.ValueKind == JsonValueKind.True);

            if (primary.ValueKind == JsonValueKind.Object &&
                primary.TryGetProperty("email", out var address) &&
                address.GetString() is { Length: > 0 } value)
            {
                context.Identity?.AddClaim(new Claim(ClaimTypes.Email, value));
                context.Identity?.AddClaim(new Claim(ExternalAuth.EmailVerifiedClaim, "true"));
            }
        }
        catch (HttpRequestException)
        {
            // The person may have withheld the user:email scope. The sign-in continues with whatever
            // the profile carried, and the callback refuses honestly if that turns out to be nothing —
            // rather than failing here with a stack trace on GitHub's redirect.
        }
        finally
        {
            profile.Dispose();
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(OAuthCreatingTicketContext context, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        // GitHub rejects a request without one.
        request.Headers.UserAgent.ParseAdd("Harbora");

        using var response = await context.Backchannel.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, context.HttpContext.RequestAborted);
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
    }
}

/// <summary>
/// Fills each provider's options from the settings table at the moment the scheme is first used.
///
/// <para>
/// There is no DB-backed options idiom in this codebase to copy — SMTP and the assistant both read
/// their rows at the point of use — but a handler cannot read anything at the point of use: the
/// framework hands it an options object. So the read happens here, once per scheme per cache
/// lifetime, and <see cref="ExternalLoginSchemeCache"/> empties that cache when an operator saves.
/// </para>
/// </summary>
internal sealed class ExternalProviderOptionsSetup(IServiceScopeFactory scopes)
    : IConfigureNamedOptions<GoogleOptions>,
      IConfigureNamedOptions<OAuthOptions>,
      IConfigureNamedOptions<OpenIdConnectOptions>
{
    /// <summary>
    /// Stands in for a client id nobody has set. It has to be non-empty or the whole panel stops
    /// serving requests (see <see cref="ExternalAuthenticationRegistration.AddHarboraExternalLogin"/>),
    /// and it has to be obviously unusable if it ever appears in a log.
    /// </summary>
    private const string NotConfigured = "harbora-sso-not-configured";

    /// <summary>An authority that exists nowhere. <c>.invalid</c> is reserved for exactly this.</summary>
    private const string NoAuthority = "https://sso-not-configured.invalid";

    public void Configure(string? name, GoogleOptions options) =>
        Apply(name, ExternalLoginProviders.Google, config =>
        {
            options.ClientId = config?.ClientId ?? NotConfigured;
            options.ClientSecret = config?.ClientSecret ?? NotConfigured;
        });

    public void Configure(string? name, OAuthOptions options) =>
        Apply(name, ExternalLoginProviders.GitHub, config =>
        {
            options.ClientId = config?.ClientId ?? NotConfigured;
            options.ClientSecret = config?.ClientSecret ?? NotConfigured;
        });

    public void Configure(string? name, OpenIdConnectOptions options) =>
        Apply(name, ExternalLoginProviders.Oidc, config =>
        {
            options.ClientId = config?.ClientId ?? NotConfigured;
            options.ClientSecret = config?.ClientSecret;
            options.Authority = config?.Authority ?? NoAuthority;
        });

    public void Configure(GoogleOptions options) { }
    public void Configure(OAuthOptions options) { }
    public void Configure(OpenIdConnectOptions options) { }

    /// <summary>
    /// Applies the stored configuration for one provider, or nothing at all when the named scheme is
    /// not ours — another feature's OAuth options must not be overwritten by this one.
    /// </summary>
    private void Apply(string? name, string provider, Action<ExternalProviderConfig?> apply)
    {
        if (name != ExternalAuth.SchemeFor(provider)) return;

        var config = Read(provider);
        apply(config is { IsConfigured: true } ? config : null);
    }

    private ExternalProviderConfig? Read(string provider)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ExternalLoginSettingsService>();

            // Blocking, deliberately: the options pipeline is synchronous and there is no async door
            // into it. This runs once per scheme per cache lifetime, off a thread-pool thread with no
            // synchronisation context, on one indexed read of the settings table.
            return settings.GetAsync(CancellationToken.None).GetAwaiter().GetResult().For(provider);
        }
        catch (Exception)
        {
            // A database that is not up yet, or an install whose Settings table predates this
            // feature, means "no provider is configured" — never a panel that will not serve a page.
            return null;
        }
    }
}
