using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project 1.9's admin surface, rendered through a real request against
/// <c>/admin/settings</c>: the current amount and how much has actually been issued so far. The
/// panel renders Persian by default in tests, so every assertion reads the
/// <c>data-signup-credit-*</c> attributes the view writes (<c>Views/AdminSettings/Index.cshtml</c>)
/// rather than a rendered sentence, the same rule every other HTTP test in this suite follows.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class SignupTrialCreditHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;
    private const string Path = "/admin/settings";

    private static async Task<IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    /// <summary>The setting is platform-wide, not per-workspace — cleared before every test sets its own.</summary>
    private void SeedAmount(long? amountMinor)
    {
        Panel.Seed(db =>
        {
            db.Settings.RemoveRange(
                db.Settings.IgnoreQueryFilters().Where(s => s.Key == SettingKeys.SignupTrialCreditMinor));
            if (amountMinor is { } minor)
                db.Settings.Add(new Setting
                {
                    Key = SettingKeys.SignupTrialCreditMinor,
                    Value = minor.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        });
    }

    [Fact]
    public async Task The_unset_shipped_default_shows_as_zero_and_nothing_issued()
    {
        SeedAmount(null);

        var email = "sc-default-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.180", email);

        var response = await client.GetAsync(Path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = await ParseAsync(await response.Content.ReadAsStringAsync());
        var form = document.QuerySelector("[data-signup-credit-amount-minor]");

        form.Should().NotBeNull("the page must always render the signup-credit panel, even unset");
        form!.GetAttribute("data-signup-credit-amount-minor").Should().Be("0");
        form.GetAttribute("data-signup-credit-issued-count").Should().Be("0");
    }

    [Fact]
    public async Task Saving_an_amount_is_reflected_back_on_the_page()
    {
        SeedAmount(null);

        var email = "sc-save-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.181", email);

        var token = await client.AntiforgeryTokenFrom(Path);
        var post = await client.PostFormAsync($"{Path}/signup-credit", token, ("amount", "500"));
        post.StatusCode.Should().Be(HttpStatusCode.Found);

        var after = await client.GetAsync(Path);
        var document = await ParseAsync(await after.Content.ReadAsStringAsync());
        document.QuerySelector("[data-signup-credit-amount-minor]")!
            .GetAttribute("data-signup-credit-amount-minor").Should().Be("50000",
                "500 in the install's major currency unit is 50,000 minor units");
    }

    [Fact]
    public async Task A_negative_amount_is_refused_and_the_stored_value_is_unchanged()
    {
        SeedAmount(10_000);

        var email = "sc-negative-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.182", email);

        var token = await client.AntiforgeryTokenFrom(Path);
        var post = await client.PostFormAsync($"{Path}/signup-credit", token, ("amount", "-5"));
        post.StatusCode.Should().Be(HttpStatusCode.Found);

        var after = await client.GetAsync(Path);
        var document = await ParseAsync(await after.Content.ReadAsStringAsync());
        document.QuerySelector("[data-signup-credit-amount-minor]")!
            .GetAttribute("data-signup-credit-amount-minor").Should().Be("10000",
                "a refused save must not overwrite what was already stored");
    }

    [Fact]
    public async Task What_has_actually_been_granted_is_shown_beside_the_amount()
    {
        var owner = Guid.CreateVersion7();
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Settings.RemoveRange(
                db.Settings.IgnoreQueryFilters().Where(s => s.Key == SettingKeys.SignupTrialCreditMinor));

            db.Users.Add(new User { Id = owner, Email = $"{owner:n}@example.test", DisplayName = "trial" });
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Trial ws", Slug = "trial-" + owner.ToString("n")[..8] });

            var voucherId = Guid.CreateVersion7();
            db.BillingVouchers.Add(new BillingVoucher
            {
                Id = voucherId, CodeHash = "hash-" + owner, CodeHint = "1234",
                AmountMinor = 15_000, Currency = "IRR", Note = "Signup trial credit",
                CreatedByUserId = owner, IsTrialCredit = true,
                RedeemedWorkspaceId = workspaceId, RedeemedByUserId = owner, RedeemedAt = DateTimeOffset.UtcNow
            });
        });

        var email = "sc-totals-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.183", email);

        var response = await client.GetAsync(Path);
        var document = await ParseAsync(await response.Content.ReadAsStringAsync());
        var form = document.QuerySelector("[data-signup-credit-issued-total-minor]");

        form!.GetAttribute("data-signup-credit-issued-total-minor").Should().Be("15000");
        form.GetAttribute("data-signup-credit-issued-count").Should().Be("1");
    }

    [Fact]
    public async Task A_workspace_member_cannot_reach_or_change_the_setting()
    {
        var email = "sc-member-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.184", email);

        var response = await client.GetAsync(Path);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
    }
}
