using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Servers;
using Harbora.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The create form's success path actually crosses its own redirect.
///
/// Every other test on this action stops at the 302 (see
/// <see cref="AppCreateSlugRefusalHttpTests.Creating_an_app_with_an_available_typed_slug_still_succeeds"/>)
/// — which proves the controller decided to redirect, not that the page it points at renders. TempData
/// is exactly the kind of thing that looks fine at that boundary and breaks one hop later: a GUID-shaped
/// string comes back from TempData typed as <c>System.Guid</c>, so a view doing <c>TempData["X"] as
/// string</c> silently gets null. A unit test that builds the model and reads <c>ViewData</c> never
/// crosses the redirect at all, so it stays green while that would be broken. This follows the real
/// <c>Location</c> header with the same client, the way a browser would.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppCreateRedirectHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private Guid GivenAFreshWorkspace()
    {
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            var planId = db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefault();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId, Name = "Create redirect test",
                Slug = "create-redirect-" + workspaceId.ToString("N")[..8],
                PlanId = planId == Guid.Empty ? null : planId
            });
        });
        return workspaceId;
    }

    private void GivenALocalServer() =>
        Panel.Seed(db => db.Servers.Add(new Server
        {
            Id = Guid.NewGuid(), Name = "local", Hostname = "localhost", IsLocal = true,
            Status = ServerStatus.Online, TotalMemoryBytes = 8L << 30, CpuCores = 4
        }));

    private string GivenAFreeInstanceSize()
    {
        var key = "free-create-redirect";
        Panel.Seed(db => db.InstanceSizes.Add(new InstanceSize
        {
            Key = key, Name = "Free", NameFa = "رایگان",
            CpuCores = 0.1, MemoryBytes = 128L << 20, DiskBytes = 1L << 30,
            RunningRatePerHourMinor = 0
        }));
        return key;
    }

    private void GivenAFundedWallet(Guid workspaceId) =>
        Panel.Seed(db => db.Wallets.Add(new Harbora.Domain.Billing.Wallet
        { WorkspaceId = workspaceId, BalanceMinor = 100_000, Currency = "IRR" }));

    [Fact]
    public async Task Creating_an_app_without_deploying_now_redirects_to_a_details_page_that_actually_renders()
    {
        var workspaceId = GivenAFreshWorkspace();
        GivenALocalServer();
        var sizeKey = GivenAFreeInstanceSize();
        GivenAFundedWallet(workspaceId);
        Panel.GivenUser(workspaceId, "create-redirect@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.70", "create-redirect@example.com");
        var token = await client.AntiforgeryTokenFrom("/apps");

        var response = await client.PostFormAsync("/Apps/Create", token,
            ("Name", "Redirect Crossing App"), ("Slug", "redirect-crossing-app"),
            ("InstanceSizeKey", sizeKey), ("SourceType", nameof(AppSourceType.PrebuiltImage)),
            ("PrebuiltImage", "nginx:1.27"),
            // DeployNow defaults to true on the model, which would route this through the deployment
            // engine instead of straight to the app it just created — a different page, and not the
            // one this test is proving.
            ("DeployNow", "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Found,
            "the create action redirects on success");
        var path = response.RedirectPath();
        path.Should().StartWith("/Apps/Details/", "DeployNow=false must land on the app it just made");

        var details = await client.GetAsync(path);
        details.StatusCode.Should().Be(HttpStatusCode.OK,
            "the redirect target has to actually render, not 404 or throw on the way there");

        var appId = Panel.Read(db => db.Apps.IgnoreQueryFilters()
            .Where(a => a.WorkspaceId == workspaceId && a.Slug == "redirect-crossing-app")
            .Select(a => a.Id).Single());
        path.Should().Be($"/Apps/Details/{appId}", "the id in the redirect must be the app that was created");

        var html = await details.Content.ReadAsStringAsync();
        html.Should().Contain("redirect-crossing-app",
            "the rendered page has to be this app's own Details page, not merely any 200");
    }
}
