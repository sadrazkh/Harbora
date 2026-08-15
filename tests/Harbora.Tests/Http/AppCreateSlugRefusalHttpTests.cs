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
/// The create form's own half of 2026-08-15-unique-app-names-design: a typed slug another workspace
/// already holds is refused — not silently renamed the way an auto-derived one still is — with a
/// message that says the namespace is platform-wide. "That name is taken" alone would be baffling
/// here: the person can see their own workspace and there is nothing called it in there.
///
/// The panel renders Persian by default in this harness (Program.cs: <c>DefaultRequestCulture =
/// new RequestCulture("fa")</c>), so the assertions below read the rendered Persian text rather than
/// the English one — and specifically the literal word "Workspace", left untranslated in
/// <c>AppsController.TakenAppSlugRefusal</c> on purpose, as the marker that this is that refusal and
/// not some other validation error.
///
/// Each test gets its own fresh workspace rather than <c>fixture.WorkspaceId</c>: that one is shared
/// by every test in the HTTP collection, and by the time this file's tests run it may already be at
/// its plan's app ceiling from everything else the collection created in it — a quota refusal would
/// make <see cref="Creating_an_app_with_an_available_typed_slug_still_succeeds"/> pass for a reason
/// that has nothing to do with slugs.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppCreateSlugRefusalHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>A workspace of this test's own, on the same default plan the fixture's workspace uses.</summary>
    private Guid GivenAFreshWorkspace()
    {
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            var planId = db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefault();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId, Name = "Slug refusal test",
                Slug = "slug-test-" + workspaceId.ToString("N")[..8],
                PlanId = planId == Guid.Empty ? null : planId
            });
        });
        return workspaceId;
    }

    /// <summary>A local node with room for a zero-instance-size app, so scheduling never refuses first.</summary>
    private void GivenALocalServer()
    {
        Panel.Seed(db => db.Servers.Add(new Server
        {
            Id = Guid.NewGuid(), Name = "local", Hostname = "localhost", IsLocal = true,
            Status = ServerStatus.Online, TotalMemoryBytes = 8L << 30, CpuCores = 4
        }));
    }

    /// <summary>Somebody else's workspace, with an app already sitting at <paramref name="slug"/>.</summary>
    private void GivenAStrangersAppAt(string slug)
    {
        Panel.Seed(db => db.Apps.Add(new App
        {
            WorkspaceId = Guid.CreateVersion7(), Name = slug, Slug = slug,
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "nginx:1.27", Status = AppStatus.Created
        }));
    }

    /// <summary>
    /// A zero-priced tier, so a create in a non-default workspace clears
    /// <c>ResourceCreationBilling</c>'s "nobody has priced this size" refusal — orthogonal to what
    /// this file tests, but real in this harness: <c>fixture.WorkspaceId</c> is exempt from billing
    /// only because <c>HarboraHttpFixture</c> marks it <c>IsDefault</c>, and a fresh workspace of this
    /// test's own is not.
    /// </summary>
    private string GivenAFreeInstanceSize()
    {
        var key = "free-cts";
        Panel.Seed(db => db.InstanceSizes.Add(new Harbora.Domain.Tenancy.InstanceSize
        {
            Key = key, Name = "Free", NameFa = "رایگان",
            CpuCores = 0.1, MemoryBytes = 128L << 20, DiskBytes = 1L << 30,
            RunningRatePerHourMinor = 0
        }));
        return key;
    }

    /// <summary>
    /// <c>ResourceCreationBilling.EnsureAffordable</c> refuses even a zero-rate resource for an
    /// unfunded wallet on purpose ("a zero balance is not an active customer account") — so a
    /// non-default workspace needs one seeded before a create can succeed at all.
    /// </summary>
    private void GivenAFundedWallet(Guid workspaceId) =>
        Panel.Seed(db => db.Wallets.Add(new Harbora.Domain.Billing.Wallet
        { WorkspaceId = workspaceId, BalanceMinor = 100_000, Currency = "IRR" }));

    [Fact]
    public async Task Creating_an_app_with_a_slug_another_workspace_already_holds_is_refused_platform_wide()
    {
        var workspaceId = GivenAFreshWorkspace();
        GivenALocalServer();
        GivenAStrangersAppAt("api-slug-refusal-cts");
        Panel.GivenUser(workspaceId, "slug-refusal@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.61", "slug-refusal@example.com");
        var token = await client.AntiforgeryTokenFrom("/apps");

        var response = await client.PostFormAsync("/Apps/Create", token,
            ("Name", "My Api"), ("Slug", "api-slug-refusal-cts"),
            ("SourceType", nameof(AppSourceType.PrebuiltImage)), ("PrebuiltImage", "nginx:1.27"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a refusal redisplays the form (200); a redirect (302) would mean the app was created");
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("api-slug-refusal-cts", "the refusal has to name the slug that was refused");
        html.Should().Contain("Workspace",
            "the platform-wide explanation, not a generic \"that name is taken\" — this word is left " +
            "untranslated in the Persian string specifically so this assertion can find it either way");

        Panel.Read(db => db.Apps.IgnoreQueryFilters()
                .Count(a => a.WorkspaceId == workspaceId && a.Slug == "api-slug-refusal-cts"))
            .Should().Be(0, "the refused app must never have been created");
    }

    [Fact]
    public async Task Creating_an_app_with_an_available_typed_slug_still_succeeds()
    {
        var workspaceId = GivenAFreshWorkspace();
        GivenALocalServer();
        var sizeKey = GivenAFreeInstanceSize();
        GivenAFundedWallet(workspaceId);
        Panel.GivenUser(workspaceId, "slug-ok@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.62", "slug-ok@example.com");
        var token = await client.AntiforgeryTokenFrom("/apps");

        var response = await client.PostFormAsync("/Apps/Create", token,
            ("Name", "My Worker"), ("Slug", "worker-slug-ok-cts"), ("InstanceSizeKey", sizeKey),
            ("SourceType", nameof(AppSourceType.PrebuiltImage)), ("PrebuiltImage", "nginx:1.27"));

        response.StatusCode.Should().Be(HttpStatusCode.Found,
            "an available slug must still create the app the way it always did");
        Panel.Read(db => db.Apps.IgnoreQueryFilters()
                .Count(a => a.WorkspaceId == workspaceId && a.Slug == "worker-slug-ok-cts"))
            .Should().Be(1);
    }
}
