using System.Net;
using System.Text;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The first-run guard, which is a middleware and therefore invisible to every controller test.
///
/// <para>
/// Each case boots a panel of its own, because what is under test is a panel that has never been set
/// up — and the collection's shared one has been. <c>SetupGuardMiddleware</c> also caches its answer
/// in a process-wide static, so each case clears it first; the whole HTTP collection runs
/// sequentially, which is what makes that safe.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class SetupGuardHttpTests
{
    [Fact]
    public async Task Before_there_is_an_owner_every_page_leads_to_the_wizard()
    {
        HarboraWebFactory.ForgetSetupCompleted();
        using var fresh = new HarboraWebFactory();
        var client = fresh.ClientFrom("203.0.113.80");

        var dashboard = await client.GetAsync("/");
        var apps = await client.GetAsync("/apps");
        var wizard = await client.GetAsync("/setup");
        var probe = await client.GetAsync("/healthz");

        dashboard.StatusCode.Should().Be(HttpStatusCode.Found);
        dashboard.RedirectPath().Should().Be("/setup");

        // Recorded as it is, not as one might assume: the guard is registered after
        // UseAuthorization, so a page that needs a session challenges first and the wizard is only
        // reached from the anonymous front door. Reported as a wrinkle rather than a defect — but
        // this line pins it, so whoever moves SetupGuardMiddleware above UseAuthorization has to
        // change this assertion to /setup on the way. Keep it either way: it is also what would
        // catch that ordering being changed by accident.
        apps.StatusCode.Should().Be(HttpStatusCode.Found);
        apps.RedirectPath().Should().Be("/account/login");

        wizard.StatusCode.Should().Be(HttpStatusCode.OK, "the wizard itself is exempt");
        probe.StatusCode.Should().Be(HttpStatusCode.OK, "so is the installer's liveness probe");
    }

    [Fact]
    public async Task A_git_delivery_to_a_half_installed_panel_is_judged_on_its_signature()
    {
        // /webhooks is on the guard's exemption list, and this is the only place that can prove it:
        // once setup has completed the middleware short-circuits on its cached flag and every path
        // passes through for a reason that has nothing to do with the list. A provider that keeps
        // delivering to a panel mid-upgrade must be refused, or accepted, on the secret it presents
        // — never answered with a 302 to a wizard it cannot fill in.
        HarboraWebFactory.ForgetSetupCompleted();
        using var fresh = new HarboraWebFactory();

        const string secret = "the-repositorys-own-secret";
        var provider = new GitProvider
        {
            WorkspaceId = Guid.CreateVersion7(), Name = "GitLab", Type = GitProviderType.GitLab,
            ApiBaseUrl = "https://gitlab.com"
        };
        var repository = new GitRepository
        {
            GitProviderId = provider.Id, FullName = "acme/mid-install",
            CloneUrl = "https://gitlab.com/acme/mid-install.git", WebhookSecret = secret
        };
        fresh.Seed(db =>
        {
            db.GitProviders.Add(provider);
            db.GitRepositories.Add(repository);
        });

        var client = fresh.ClientFrom("203.0.113.83");

        var signed = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/git/{repository.Id}")
        {
            Content = new StringContent("""{"ref":"refs/heads/main"}""", Encoding.UTF8, "application/json")
        };
        signed.Headers.TryAddWithoutValidation("X-Gitlab-Token", secret);
        var accepted = await client.SendAsync(signed);

        var unsigned = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/git/{repository.Id}")
        {
            Content = new StringContent("""{"ref":"refs/heads/main"}""", Encoding.UTF8, "application/json")
        };
        var refused = await client.SendAsync(unsigned);

        // The panel is still unset-up throughout — the guard would have redirected an ordinary page.
        (await client.GetAsync("/")).RedirectPath().Should().Be("/setup");

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the exemption lets the delivery reach the signature check, not past it");
    }

    [Fact]
    public async Task Once_the_wizard_has_run_the_guard_stops_redirecting()
    {
        HarboraWebFactory.ForgetSetupCompleted();
        using var fresh = new HarboraWebFactory();
        var client = fresh.ClientFrom("203.0.113.81");

        var token = await client.AntiforgeryTokenFrom("/setup");
        var completed = await client.PostFormAsync("/setup", token,
            ("Email", "founder@example.com"),
            ("DisplayName", "The Founder"),
            ("Password", HarboraWebFactory.TestPassword),
            ("ConfirmPassword", HarboraWebFactory.TestPassword),
            ("PlatformName", "Harbora"),
            ("RootDomain", "localhost"),
            ("AcmeEmail", "founder@example.com"),
            ("Culture", "en"));

        completed.StatusCode.Should().Be(HttpStatusCode.Found);
        completed.RedirectPath().Should().Be("/");

        var dashboard = await client.GetAsync("/");

        dashboard.StatusCode.Should().Be(HttpStatusCode.OK,
            "the wizard signs the owner in, so the panel is theirs from the same request");
        fresh.Read(db => db.Settings.Any(s => s.Key == SettingKeys.SetupCompleted && s.Value == "true"))
            .Should().BeTrue();

        // And an unauthenticated caller now meets the login form instead of the wizard.
        var stranger = await fresh.ClientFrom("203.0.113.82").GetAsync("/apps");
        stranger.RedirectPath().Should().Be("/account/login");
    }
}
