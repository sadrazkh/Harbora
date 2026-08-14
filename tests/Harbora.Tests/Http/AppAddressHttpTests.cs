using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The backfill screen, over real HTTP.
///
/// This is the only part of the address work that rewrites live Traefik routing, which is why it is a
/// control the operator presses rather than a sweep that happens to them. The assertion this file
/// exists for is the third one: an app that already had a working custom domain still has it
/// afterwards. "An app has no address" is an inconvenience; "an app that had one lost it" is an
/// outage.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppAddressHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private Guid SeedApp(string slug, ServiceKind kind, string? withDomain)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            Kind = kind,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };

        Panel.Seed(db =>
        {
            db.Apps.Add(app);
            if (withDomain is not null)
                db.Domains.Add(new DomainName
                {
                    AppId = app.Id, Host = withDomain, SslEnabled = true, ForceHttps = true, IsPrimary = true
                });
        });

        return app.Id;
    }

    private void SeedRootDomain(string root) => Panel.Seed(db =>
        db.Settings.Add(new Setting { Key = SettingKeys.PlatformRootDomain, Value = root }));

    [Fact]
    public async Task The_preview_lists_an_addressless_app_and_the_name_it_would_be_given()
    {
        SeedRootDomain("apps.example.com");
        SeedApp("addr-preview-shop", ServiceKind.Web, withDomain: null);
        Panel.GivenUser(fixture.WorkspaceId, "addr-preview@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.200", "addr-preview@example.com");

        var html = await (await client.GetAsync("/apps/addresses")).Content.ReadAsStringAsync();

        html.Should().Contain("addr-preview-shop.apps.example.com",
            "a hostname is not translated, so this holds whichever language rendered the page");
    }

    [Fact]
    public async Task The_preview_does_not_offer_to_rename_an_app_that_already_has_a_domain()
    {
        SeedRootDomain("apps.example.com");
        SeedApp("addr-keeps-its-own", ServiceKind.Web, withDomain: "chosen.example.com");
        Panel.GivenUser(fixture.WorkspaceId, "addr-keeps@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.201", "addr-keeps@example.com");

        var html = await (await client.GetAsync("/apps/addresses")).Content.ReadAsStringAsync();

        html.Should().NotContain("addr-keeps-its-own",
            "an app with a domain is not addressless, so it has no business on a screen offering to give it one");
    }

    [Fact]
    public async Task Applying_the_backfill_leaves_an_existing_custom_domain_exactly_as_it_was()
    {
        SeedRootDomain("apps.example.com");
        var kept = SeedApp("addr-apply-kept", ServiceKind.Web, withDomain: "chosen.example.com");
        var given = SeedApp("addr-apply-given", ServiceKind.Web, withDomain: null);
        Panel.GivenUser(fixture.WorkspaceId, "addr-apply@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.202", "addr-apply@example.com");

        var response = await client.PostFormAsync("/apps/addresses",
            await client.AntiforgeryTokenFrom("/apps/addresses"));
        response.RedirectPath().Should().Be("/apps/addresses",
            "a successful apply redirects back to the preview, the same way every other action here does");

        var untouched = Panel.Read(db => db.Domains.Where(d => d.AppId == kept).ToList());
        untouched.Should().ContainSingle().Which.Host.Should().Be("chosen.example.com",
            "this is the failure that would matter: not a missing address, but a working one replaced");
        untouched[0].IsPrimary.Should().BeTrue();

        var addressed = Panel.Read(db => db.Domains.Where(d => d.AppId == given).ToList());
        addressed.Should().ContainSingle().Which.Host.Should().Be("addr-apply-given.apps.example.com");
    }

    [Fact]
    public async Task A_worker_is_not_offered_an_address_because_nothing_would_answer_on_it()
    {
        SeedRootDomain("apps.example.com");
        SeedApp("addr-worker", ServiceKind.Worker, withDomain: null);
        Panel.GivenUser(fixture.WorkspaceId, "addr-worker@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.203", "addr-worker@example.com");

        var html = await (await client.GetAsync("/apps/addresses")).Content.ReadAsStringAsync();

        html.Should().NotContain("addr-worker.apps.example.com",
            "a worker takes no inbound traffic — an address for it is a certificate nothing ever answers on");
    }

    [Fact]
    public async Task Setting_the_root_domain_does_not_by_itself_change_any_existing_app()
    {
        var untouched = SeedApp("addr-untouched", ServiceKind.Web, withDomain: null);
        SeedRootDomain("apps.example.com");
        Panel.GivenUser(fixture.WorkspaceId, "addr-untouched@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.204", "addr-untouched@example.com");

        // Merely looking at the preview must write nothing — it is a GET.
        await client.GetAsync("/apps/addresses");

        Panel.Read(db => db.Domains.Where(d => d.AppId == untouched).ToList()).Should().BeEmpty(
            "the backfill is a control the operator presses, not something that happens to them");
    }
}
