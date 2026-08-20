using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The operator's half: typing a provider's credentials in and having the sign-in page start
/// offering it, without restarting the panel.
///
/// <para>
/// Every test here puts the provider back the way it found it, because the settings are
/// platform-wide and the whole HTTP collection shares one panel.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public sealed class ExternalLoginAdminHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private const string Provider = ExternalLoginProviders.GitHub;

    [Fact]
    public async Task Configuring_a_provider_makes_its_button_appear_without_a_restart()
    {
        var admin = await SignedInAdmin("203.0.113.91", "sso-admin-configure");
        var token = await admin.AntiforgeryTokenFrom("/admin/settings");

        try
        {
            var response = await admin.PostFormAsync("/admin/settings/sso", token,
                ("provider", Provider), ("enabled", "true"),
                ("clientId", "github-client"), ("clientSecret", "github-secret"));

            response.RedirectPath().Should().Be("/admin/settings");

            var login = await Panel.ClientFrom("203.0.113.92").GetAsync("/account/login");
            (await login.Content.ReadAsStringAsync())
                .Should().Contain($"data-external-provider=\"{Provider}\"",
                    "the framework caches a scheme's options for the life of the process, so saving has to empty that cache");
        }
        finally
        {
            await ResetAsync();
        }
    }

    [Fact]
    public async Task A_stored_client_secret_is_reported_but_never_rendered_back()
    {
        var admin = await SignedInAdmin("203.0.113.93", "sso-admin-secret");
        var token = await admin.AntiforgeryTokenFrom("/admin/settings");

        try
        {
            await admin.PostFormAsync("/admin/settings/sso", token,
                ("provider", Provider), ("enabled", "true"),
                ("clientId", "github-client"), ("clientSecret", "top-secret-value"));

            var page = await (await admin.GetAsync("/admin/settings")).Content.ReadAsStringAsync();

            page.Should().NotContain("top-secret-value",
                "a settings screen that renders a secret leaks it into every browser cache and screen recording");
            // The panel renders Persian by default here, so the fact is asserted on the attribute
            // rather than on the sentence beside it.
            page.Should().Contain("data-sso-has-secret=\"true\"", "but the page still has to say one is there");
            page.Should().Contain("github-client", "the client id is not a secret and re-typing it to save anything else is a trap");
        }
        finally
        {
            await ResetAsync();
        }
    }

    [Fact]
    public async Task Saving_a_second_time_without_the_secret_keeps_the_stored_one()
    {
        var admin = await SignedInAdmin("203.0.113.94", "sso-admin-keeps");
        var token = await admin.AntiforgeryTokenFrom("/admin/settings");

        try
        {
            await admin.PostFormAsync("/admin/settings/sso", token,
                ("provider", Provider), ("enabled", "true"),
                ("clientId", "first-id"), ("clientSecret", "the-secret"));

            await admin.PostFormAsync("/admin/settings/sso", await admin.AntiforgeryTokenFrom("/admin/settings"),
                ("provider", Provider), ("enabled", "true"),
                ("clientId", "second-id"), ("clientSecret", ""));

            var config = await ReadAsync();
            config.ClientId.Should().Be("second-id");
            config.ClientSecret.Should().Be("the-secret",
                "a form that must be re-fed a secret to change a client id is a form that leaks it");
            config.IsConfigured.Should().BeTrue();
        }
        finally
        {
            await ResetAsync();
        }
    }

    /// <summary>
    /// Switched on and incomplete is a third state, not a synonym for on. The page has to say so, or
    /// an operator is left looking at an "enabled" provider with no button and nothing to explain it.
    /// </summary>
    [Fact]
    public async Task A_provider_switched_on_with_no_credentials_says_no_button_will_appear()
    {
        var admin = await SignedInAdmin("203.0.113.95", "sso-admin-incomplete");
        var token = await admin.AntiforgeryTokenFrom("/admin/settings");

        try
        {
            await admin.PostFormAsync("/admin/settings/sso", token,
                ("provider", Provider), ("enabled", "true"), ("clientId", ""), ("clientSecret", ""));

            (await ReadAsync()).IsConfigured.Should().BeFalse();
            var login = await Panel.ClientFrom("203.0.113.96").GetAsync("/account/login");
            (await login.Content.ReadAsStringAsync())
                .Should().NotContain($"data-external-provider=\"{Provider}\"");
        }
        finally
        {
            await ResetAsync();
        }
    }

    [Fact]
    public async Task A_provider_that_is_not_one_of_ours_cannot_be_configured()
    {
        var admin = await SignedInAdmin("203.0.113.97", "sso-admin-unknown");
        var token = await admin.AntiforgeryTokenFrom("/admin/settings");

        var response = await admin.PostFormAsync("/admin/settings/sso", token,
            ("provider", "facebook"), ("enabled", "true"),
            ("clientId", "x"), ("clientSecret", "y"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_ordinary_member_cannot_configure_a_sign_in_provider()
    {
        var email = $"sso-member-{Guid.NewGuid():N}@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var member = await Panel.SignedInAs("203.0.113.98", email);
        // The token comes from a page this person may read; the refusal under test is the POST's.
        var token = await member.AntiforgeryTokenFrom("/settings");

        var response = await member.PostFormAsync("/admin/settings/sso", token,
            ("provider", Provider), ("enabled", "true"),
            ("clientId", "x"), ("clientSecret", "y"));

        response.RedirectPath().Should().Be("/account/denied");
        (await ReadAsync()).Enabled.Should().BeFalse();
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private async Task<HttpClient> SignedInAdmin(string ip, string tag)
    {
        var email = $"{tag}-{Guid.NewGuid():N}@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        return await Panel.SignedInAs(ip, email);
    }

    private async Task<ExternalProviderConfig> ReadAsync()
    {
        using var scope = Panel.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ExternalLoginSettingsService>();
        return (await settings.GetAsync(CancellationToken.None)).For(Provider);
    }

    /// <summary>Back to off, so the next class in this collection sees the shipped state.</summary>
    private async Task ResetAsync()
    {
        using var scope = Panel.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExternalLoginSettingsService>()
            .SaveAsync(Provider, false, "", null, null, null, CancellationToken.None);
        scope.ServiceProvider.GetRequiredService<Harbora.Web.Infrastructure.ExternalLoginSchemeCache>().Forget();
    }
}
