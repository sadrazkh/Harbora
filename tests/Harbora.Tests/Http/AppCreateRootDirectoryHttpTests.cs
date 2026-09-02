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
/// 1.2 (2026-09 market-gaps round two): the create form's own half of "deploy an app from a
/// sub-directory of a repository" — the panel is the only place an app is ever created (the CLI only
/// deploys to one that already exists), so this is where a bad root directory has to be refused, not
/// discovered later as a confusing build failure. <see cref="DeploymentPipelineRootDirectoryTests"/>
/// covers the build-time half (a missing root directory, and the fake engine's context path); this
/// covers the entry-time half — a path that is wrong by construction is refused by name before an app
/// row even exists.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppCreateRootDirectoryHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private Guid GivenAFreshWorkspace(string slugSuffix)
    {
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            var planId = db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefault();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId, Name = "Root directory test",
                Slug = "root-dir-" + slugSuffix,
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

    private string GivenAFreeInstanceSize(string key)
    {
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

    [Theory]
    [InlineData("../secrets", "..")]
    [InlineData("/etc/passwd", "absolute")]
    public async Task A_root_directory_that_breaks_containment_is_refused_by_name(string rootDirectory, string why)
    {
        var workspaceId = GivenAFreshWorkspace("refuse-" + why.GetHashCode().ToString("x"));
        GivenALocalServer();
        Panel.GivenUser(workspaceId, $"root-dir-refuse-{why}@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.40", $"root-dir-refuse-{why}@example.com");
        var token = await client.AntiforgeryTokenFrom("/apps");

        var response = await client.PostFormAsync("/Apps/Create", token,
            ("Name", "Root Dir Refusal App"), ("Slug", "root-dir-refusal-" + why.GetHashCode().ToString("x")),
            ("SourceType", nameof(AppSourceType.Upload)), ("RootDirectory", rootDirectory),
            ("DeployNow", "false"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a refusal redisplays the form (200); a redirect (302) would mean the app was created");
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain(
            System.Net.WebUtility.HtmlEncode(rootDirectory).Replace("&#x27;", "'"),
            "the refusal has to name the exact value that was refused, not a generic message");

        Panel.Read(db => db.Apps.IgnoreQueryFilters()
                .Count(a => a.WorkspaceId == workspaceId && a.Name == "Root Dir Refusal App"))
            .Should().Be(0, "a root directory that breaks containment must never reach an app row");
    }

    [Fact]
    public async Task A_valid_sub_directory_root_is_persisted_on_the_created_app()
    {
        var workspaceId = GivenAFreshWorkspace("ok");
        GivenALocalServer();
        var sizeKey = GivenAFreeInstanceSize("free-root-dir-ok");
        GivenAFundedWallet(workspaceId);
        Panel.GivenUser(workspaceId, "root-dir-ok@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.41", "root-dir-ok@example.com");
        var token = await client.AntiforgeryTokenFrom("/apps");

        var response = await client.PostFormAsync("/Apps/Create", token,
            ("Name", "Monorepo Api"), ("Slug", "monorepo-api-root-dir"),
            ("InstanceSizeKey", sizeKey), ("SourceType", nameof(AppSourceType.Upload)),
            ("RootDirectory", "services/api"), ("DeployNow", "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Found,
            "a root directory that names a real sub-path must not block creation");

        var app = Panel.Read(db => db.Apps.IgnoreQueryFilters()
            .Single(a => a.WorkspaceId == workspaceId && a.Slug == "monorepo-api-root-dir"));
        app.BuildContextPath.Should().Be("services/api",
            "the normalised sub-path — forward slashes, no leading './' — is what the build stage reads");
    }

    [Fact]
    public async Task A_blank_root_directory_still_means_the_repository_root_exactly_as_before()
    {
        var workspaceId = GivenAFreshWorkspace("blank");
        GivenALocalServer();
        var sizeKey = GivenAFreeInstanceSize("free-root-dir-blank");
        GivenAFundedWallet(workspaceId);
        Panel.GivenUser(workspaceId, "root-dir-blank@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.42", "root-dir-blank@example.com");
        var token = await client.AntiforgeryTokenFrom("/apps");

        var response = await client.PostFormAsync("/Apps/Create", token,
            ("Name", "No Root Dir App"), ("Slug", "no-root-dir-app"),
            ("InstanceSizeKey", sizeKey), ("SourceType", nameof(AppSourceType.Upload)),
            ("DeployNow", "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var app = Panel.Read(db => db.Apps.IgnoreQueryFilters()
            .Single(a => a.WorkspaceId == workspaceId && a.Slug == "no-root-dir-app"));
        app.BuildContextPath.Should().Be(".", "an app created with no root directory must still mean the " +
            "repository root, the same value every app before this feature was given");
    }
}
