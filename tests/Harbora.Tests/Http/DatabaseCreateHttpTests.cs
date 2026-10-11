using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Servers;
using Harbora.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The database create form through the booted panel, after round six moved its logic into
/// <c>DatabaseProvisioningService</c>. Nothing posted to <c>/databases/create</c> before this file —
/// every other HTTP test seeds its databases directly — so this is what proves the move kept the
/// form's own contract: the service is resolved from DI, a created database lands on its own page and
/// leaves the audit entry creation never used to write, and a refusal still re-renders the form in
/// the reader's language.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class DatabaseCreateHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>Razor writes every non-ASCII character as a numeric reference; decode before asserting.</summary>
    private static async Task<string> RenderedTextAsync(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    /// <summary>
    /// A workspace of its own — not the shared provider one, whose plan every other test in the
    /// collection fills with seeded databases — with a server to place on, a priced (free) tier and a
    /// balance, because billing is on in the booted panel and a tenant must prepay the first hour.
    /// </summary>
    private (Guid WorkspaceId, string SizeKey) GivenAWorkspaceThatCanCreate(string tag)
    {
        var workspaceId = Guid.CreateVersion7();
        var sizeKey = "free-db-" + tag;
        Panel.Seed(db =>
        {
            var planId = db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefault();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId, Name = "Database create " + tag,
                Slug = "db-create-" + workspaceId.ToString("N")[..8],
                PlanId = planId == Guid.Empty ? null : planId
            });
            var project = new Harbora.Domain.Projects.Project { WorkspaceId = workspaceId, Name = "Shop", Slug = "shop" };
            db.Projects.Add(project);
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                WorkspaceId = workspaceId, ProjectId = project.Id, Name = "Production", Slug = "production", IsDefault = true
            });
            db.Servers.Add(new Server
            {
                Id = Guid.NewGuid(), Name = "local-" + tag, Hostname = "localhost", IsLocal = true,
                Status = ServerStatus.Online, TotalMemoryBytes = 8L << 30, CpuCores = 4
            });
            db.InstanceSizes.Add(new InstanceSize
            {
                Key = sizeKey, Name = "Free", NameFa = "رایگان",
                CpuCores = 0.1, MemoryBytes = 128L << 20, DiskBytes = 1L << 30, RunningRatePerHourMinor = 0
            });
            db.Wallets.Add(new Harbora.Domain.Billing.Wallet
            {
                WorkspaceId = workspaceId, BalanceMinor = 100_000, Currency = "IRR"
            });
        });
        return (workspaceId, sizeKey);
    }

    private async Task<HttpClient> OwnerOf(Guid workspaceId, string email, string ip, bool english)
    {
        Panel.GivenUser(workspaceId, email, SystemRole.Owner);
        if (!english) return await Panel.SignedInAs(ip, email);

        // The panel's default culture is Persian; an English request has to ask for it before signing in.
        var client = Panel.ClientFrom(ip);
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        var token = await client.AntiforgeryTokenFrom("/account/login");
        await client.PostFormAsync("/account/login", token,
            ("Email", email), ("Password", HarboraWebFactory.TestPassword));
        return client;
    }

    [Fact]
    public async Task Creating_a_database_lands_on_its_own_page_and_leaves_an_audit_entry()
    {
        var (workspaceId, sizeKey) = GivenAWorkspaceThatCanCreate("ok");
        var client = await OwnerOf(workspaceId, "db-create-ok@example.com", "203.0.113.180", english: true);
        var token = await client.AntiforgeryTokenFrom("/databases/create");

        var response = await client.PostFormAsync("/databases/create", token,
            ("Name", "Orders DB"), ("Type", nameof(ManagedServiceType.PostgreSql)), ("InstanceSizeKey", sizeKey));

        response.StatusCode.Should().Be(HttpStatusCode.Found, "a created database redirects to its page");
        var service = Panel.Read(db => db.ManagedServices.IgnoreQueryFilters().AsNoTracking()
            .Single(s => s.WorkspaceId == workspaceId));
        response.RedirectPath().Should().Be($"/databases/{service.Id}");
        service.ContainerName.Should().Be("harbora-svc-orders-db");
        service.InstanceSizeKey.Should().Be(sizeKey);

        var audit = Panel.Read(db => db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.TargetId == service.Id.ToString()).ToList());
        audit.Should().ContainSingle().Which.Action.Should().Be("database.created");
        audit[0].WorkspaceId.Should().Be(workspaceId);
        audit[0].IpAddress.Should().Be("203.0.113.180");

        (await client.GetAsync(response.RedirectPath())).StatusCode.Should().Be(HttpStatusCode.OK,
            "the page the create redirects to has to render");
    }

    [Fact]
    public async Task A_name_already_taken_re_renders_the_form_with_the_refusal_and_creates_nothing()
    {
        var (workspaceId, sizeKey) = GivenAWorkspaceThatCanCreate("dup");
        var client = await OwnerOf(workspaceId, "db-create-dup@example.com", "203.0.113.181", english: true);
        var first = await client.AntiforgeryTokenFrom("/databases/create");
        await client.PostFormAsync("/databases/create", first,
            ("Name", "inventory"), ("Type", nameof(ManagedServiceType.PostgreSql)), ("InstanceSizeKey", sizeKey));

        var token = await client.AntiforgeryTokenFrom("/databases/create");
        var response = await client.PostFormAsync("/databases/create", token,
            ("Name", "Inventory"), ("Type", nameof(ManagedServiceType.PostgreSql)), ("InstanceSizeKey", sizeKey));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a refused create re-renders the form");
        (await RenderedTextAsync(response)).Should().Contain("A service with this name already exists.");
        Panel.Read(db => db.ManagedServices.IgnoreQueryFilters().Count(s => s.WorkspaceId == workspaceId))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_refusal_the_form_only_ever_showed_in_English_now_reaches_a_Persian_reader_in_Persian()
    {
        var (workspaceId, sizeKey) = GivenAWorkspaceThatCanCreate("fa");
        var client = await OwnerOf(workspaceId, "db-create-fa@example.com", "203.0.113.182", english: false);
        var token = await client.AntiforgeryTokenFrom("/databases/create");

        var response = await client.PostFormAsync("/databases/create", token,
            ("Name", "reports"), ("Type", nameof(ManagedServiceType.PostgreSql)), ("Version", "9.6"),
            ("InstanceSizeKey", sizeKey));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RenderedTextAsync(response)).Should().Contain("نسخهٔ انتخاب‌شدهٔ دیتابیس پشتیبانی نمی‌شود.");
        Panel.Read(db => db.ManagedServices.IgnoreQueryFilters().Any(s => s.WorkspaceId == workspaceId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_name_is_said_once_not_twice()
    {
        var (workspaceId, sizeKey) = GivenAWorkspaceThatCanCreate("empty");
        var client = await OwnerOf(workspaceId, "db-create-empty@example.com", "203.0.113.183", english: true);
        var token = await client.AntiforgeryTokenFrom("/databases/create");

        var response = await client.PostFormAsync("/databases/create", token,
            ("Name", ""), ("Type", nameof(ManagedServiceType.PostgreSql)), ("InstanceSizeKey", sizeKey));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await RenderedTextAsync(response);
        html.Split("<li>A name is required.</li>").Length.Should().Be(2,
            "the view model's [Required] already put it in the summary; the service's own copy of the "
            + "same refusal must not list it a second time");
        Panel.Read(db => db.ManagedServices.IgnoreQueryFilters().Any(s => s.WorkspaceId == workspaceId))
            .Should().BeFalse();
    }
}
