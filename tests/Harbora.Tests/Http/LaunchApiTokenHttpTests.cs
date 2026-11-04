using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Harbora.Cli;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Launch;
using Harbora.Domain.Servers;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Launch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The real <see cref="ILaunchService"/> and everything under it, driven by a bearer token through the
/// whole pipeline. Every other launch test either signs in with a cookie or hands the service a caller
/// of its own, and the services a confirm goes through (<c>AppProvisioningService</c>,
/// <c>DatabaseProvisioningService</c>, <c>DatabaseAttachmentService</c>, <c>AppConfigurationService</c>)
/// each judge the signed-in user. Nothing had ever run them with a user that came from a token, and a
/// claim that one of them looks for and the token scheme does not set would read as a 500 on the very
/// first <c>harbora launch</c>.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class LaunchApiTokenHttpTests(HarboraHttpFixture fixture) : IDisposable
{
    private readonly string _source = Path.Combine(Path.GetTempPath(), "harbora-launch-token-" + Guid.NewGuid().ToString("N"));

    private HarboraWebFactory Panel => fixture.Panel;

    public void Dispose()
    {
        try { Directory.Delete(_source, recursive: true); }
        catch (IOException) { /* a temp folder */ }
        catch (UnauthorizedAccessException) { /* a temp folder */ }
    }

    /// <summary>A workspace of its own that can pay for what it creates: the shape <c>DatabaseCreateHttpTests</c> uses.</summary>
    private (Guid WorkspaceId, string SizeKey) GivenAWorkspaceThatCanCreate(string tag)
    {
        var workspaceId = Guid.CreateVersion7();
        var sizeKey = "launch-free-" + tag;
        Panel.Seed(db =>
        {
            var planId = db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefault();
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId, Name = "Launch token " + tag,
                Slug = "launch-token-" + workspaceId.ToString("N")[..8],
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
            db.Wallets.Add(new Harbora.Domain.Billing.Wallet { WorkspaceId = workspaceId, BalanceMinor = 100_000, Currency = "IRR" });
        });
        return (workspaceId, sizeKey);
    }

    private HttpClient Bearer(Guid workspaceId, string email, SystemRole role = SystemRole.Owner, bool scoped = false, string ip = "203.0.113.190")
    {
        var user = Panel.GivenUser(workspaceId, email, role, scopedToProjects: scoped);
        return Panel.BearerClientFrom(ip, Panel.GivenApiToken(user.Id));
    }

    /// <summary>A real Node app with a real example file, packed by the real packer.</summary>
    private async Task<ByteArrayContent> PackedShopAsync()
    {
        Directory.CreateDirectory(_source);
        await File.WriteAllTextAsync(Path.Combine(_source, "package.json"),
            """{ "name": "shop", "scripts": { "start": "node index.js" }, "dependencies": { "pg": "^8.0.0", "express": "^4.0.0" } }""");
        await File.WriteAllTextAsync(Path.Combine(_source, "index.js"), "require('express')().listen(process.env.PORT);");
        await File.WriteAllTextAsync(Path.Combine(_source, ".env.example"),
            "JWT_SECRET=\nSTRIPE_SECRET_KEY=sk_live_committed_by_mistake\nLOG_LEVEL=info\n");

        var packed = await SourcePacker.PackAsync(_source);
        var bytes = await File.ReadAllBytesAsync(packed.ArchivePath);
        File.Delete(packed.ArchivePath);

        var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        return body;
    }

    /// <summary>What the job worker does: no session, in a scope of its own.</summary>
    private async Task InspectAsync(Guid id)
    {
        using var scope = Panel.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ILaunchService>().InspectAsync(id, default);
    }

    private async Task<Guid> StartedAndReadAsync(HttpClient client)
    {
        var started = await client.PostAsync("/api/v1/launch/archive?name=shop", await PackedShopAsync());
        started.StatusCode.Should().Be(HttpStatusCode.OK, await started.Content.ReadAsStringAsync());
        var id = (await started.JsonAsync()).GetProperty("id").GetGuid();

        await InspectAsync(id);
        return id;
    }

    private static StringContent AsJson(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Every_step_of_a_launch_works_for_somebody_who_came_in_with_a_token()
    {
        var (workspaceId, sizeKey) = GivenAWorkspaceThatCanCreate("ok");
        var client = Bearer(workspaceId, "launch-token-ok@example.com");

        var id = await StartedAndReadAsync(client);

        // The proposal, read from the real tree by the real inspector.
        var proposalResponse = await client.GetAsync($"/api/v1/launch/{id}/proposal");
        proposalResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var proposalText = await proposalResponse.Content.ReadAsStringAsync();
        proposalText.Should().NotContain("sk_live_committed_by_mistake");
        var proposal = JsonDocument.Parse(proposalText).RootElement;
        proposal.GetProperty("appName").GetString().Should().Be("shop", "the name in the query string reached the launch through the file's name");
        proposal.GetProperty("inspection").GetProperty("stack").GetString().Should().Be("Node");
        proposal.GetProperty("defaults").GetProperty("services").EnumerateArray()
            .Select(s => s.GetProperty("type").GetString()).Should().Equal("PostgreSql");

        var choices = JsonNode.Parse(proposal.GetProperty("defaults").GetRawText())!.AsObject();
        choices["instanceSizeKey"] = sizeKey;
        foreach (var variable in choices["envVars"]!.AsArray())
            if (variable!["key"]!.GetValue<string>() == "STRIPE_SECRET_KEY")
                variable["value"] = "sk_live_the_real_one";

        // The cost, then the button.
        var quote = await client.PostAsync($"/api/v1/launch/{id}/quote", AsJson(choices));
        quote.StatusCode.Should().Be(HttpStatusCode.OK, await quote.Content.ReadAsStringAsync());
        (await quote.JsonAsync()).GetProperty("refusal").ValueKind.Should().Be(JsonValueKind.Null);

        var confirmed = await client.PostAsync($"/api/v1/launch/{id}/confirm", AsJson(choices));

        var confirmedText = await confirmed.Content.ReadAsStringAsync();
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, confirmedText);
        confirmedText.Should().NotContain("sk_live_the_real_one");
        var view = JsonDocument.Parse(confirmedText).RootElement;
        view.GetProperty("failureMessage").ValueKind.Should().Be(JsonValueKind.Null, confirmedText);
        view.GetProperty("status").GetString().Should().Be("WaitingForServices",
            "every create was accepted, and the tick takes it from here");

        // And it all really exists, belonging to the person the token names.
        var appId = view.GetProperty("appId").GetGuid();
        var app = Panel.Read(db => db.Apps.IgnoreQueryFilters().Single(a => a.Id == appId));
        app.WorkspaceId.Should().Be(workspaceId);
        app.SourceType.Should().Be(AppSourceType.Upload);
        app.InstanceSizeKey.Should().Be(sizeKey);

        Panel.Read(db => db.ManagedServices.IgnoreQueryFilters().Count(s => s.WorkspaceId == workspaceId)).Should().Be(1);
        Panel.Read(db => db.AppManagedServices.IgnoreQueryFilters().Count(a => a.AppId == appId)).Should().Be(1);

        var variables = Panel.Read(db => db.EnvironmentVariables.IgnoreQueryFilters().Where(v => v.AppId == appId).ToList());
        variables.Select(v => v.Key).Should().BeEquivalentTo("JWT_SECRET", "STRIPE_SECRET_KEY", "LOG_LEVEL");
        variables.Single(v => v.Key == "JWT_SECRET").IsSecret.Should().BeTrue();
        variables.Single(v => v.Key == "STRIPE_SECRET_KEY").IsSecret.Should().BeTrue();

        var row = Panel.Read(db => db.AppLaunches.IgnoreQueryFilters().Single(l => l.Id == id));
        row.UserId.Should().Be(Panel.Read(db => db.Users.IgnoreQueryFilters().Single(u => u.Email == "launch-token-ok@example.com").Id));
        (row.ChoicesJson ?? "").Should().NotContain("sk_live_the_real_one");

        // A second press finds it already going, the way the page's does.
        var again = await client.PostAsync($"/api/v1/launch/{id}/confirm", AsJson(choices));
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Count(a => a.WorkspaceId == workspaceId)).Should().Be(1);
    }

    [Fact]
    public async Task A_git_token_goes_in_and_never_comes_back()
    {
        var (workspaceId, _) = GivenAWorkspaceThatCanCreate("git");
        var client = Bearer(workspaceId, "launch-token-git@example.com", ip: "203.0.113.191");

        var started = await client.PostAsync("/api/v1/launch", HttpConversation.Json(new
        {
            cloneUrl = "https://embedded_user:embedded_secret@github.com/you/shop.git",
            gitRef = "develop",
            gitToken = "ghp_never_comes_back"
        }));

        var startedText = await started.Content.ReadAsStringAsync();
        started.StatusCode.Should().Be(HttpStatusCode.OK, startedText);
        startedText.Should().NotContain("ghp_never_comes_back");
        var id = JsonDocument.Parse(startedText).RootElement.GetProperty("id").GetGuid();

        var viewText = await (await client.GetAsync($"/api/v1/launch/{id}")).Content.ReadAsStringAsync();
        viewText.Should().NotContain("ghp_never_comes_back");
        viewText.Should().NotContain("embedded_secret");
        JsonDocument.Parse(viewText).RootElement.GetProperty("sourceLabel").GetString().Should().Be("https://github.com/you/shop.git");

        var conflict = await client.GetAsync($"/api/v1/launch/{id}/proposal");
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict, "it has not been read yet");
        var conflictBody = await conflict.JsonAsync();
        conflictBody.GetProperty("status").GetString().Should().Be("Inspecting");
    }

    [Fact]
    public async Task Somebody_who_may_not_place_an_app_anywhere_is_refused_at_the_start_not_given_a_500()
    {
        var (workspaceId, _) = GivenAWorkspaceThatCanCreate("scoped-start");
        var scoped = Bearer(workspaceId, "launch-token-scoped-start@example.com", SystemRole.Member, scoped: true, ip: "203.0.113.192");
        var uploads = Path.Combine(Panel.Resolve<Microsoft.Extensions.Options.IOptions<Harbora.Infrastructure.Deployments.HarboraRuntimeOptions>>().Value.WorkDir, "_uploads");
        var before = Directory.Exists(uploads) ? Directory.GetFiles(uploads).ToHashSet() : [];

        var response = await scoped.PostAsync("/api/v1/launch/archive?name=shop", await PackedShopAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.JsonAsync()).GetProperty("errors")[0].GetProperty("messageFa").GetString().Should().NotBeNullOrWhiteSpace();
        Panel.Read(db => db.AppLaunches.IgnoreQueryFilters().Count(l => l.WorkspaceId == workspaceId)).Should().Be(0);
        (Directory.Exists(uploads) ? Directory.GetFiles(uploads) : []).Except(before).Should().BeEmpty();
    }

    [Fact]
    public async Task Somebody_who_may_not_place_an_app_there_is_refused_at_the_confirm_with_nothing_created()
    {
        var (workspaceId, sizeKey) = GivenAWorkspaceThatCanCreate("scoped-confirm");
        var owner = Bearer(workspaceId, "launch-token-owner-of-scoped@example.com", ip: "203.0.113.193");
        var scoped = Bearer(workspaceId, "launch-token-scoped-confirm@example.com", SystemRole.Member, scoped: true, ip: "203.0.113.194");

        var id = await StartedAndReadAsync(owner);
        var proposal = await (await owner.GetAsync($"/api/v1/launch/{id}/proposal")).JsonAsync();
        var choices = JsonNode.Parse(proposal.GetProperty("defaults").GetRawText())!.AsObject();
        choices["instanceSizeKey"] = sizeKey;

        // The same workspace, so the launch is there to see: it is ConfirmAsync's services that say no.
        var response = await scoped.PostAsync($"/api/v1/launch/{id}/confirm", AsJson(choices));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.JsonAsync()).GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Count(a => a.WorkspaceId == workspaceId)).Should().Be(0);
        Panel.Read(db => db.ManagedServices.IgnoreQueryFilters().Count(s => s.WorkspaceId == workspaceId)).Should().Be(0);
        Panel.Read(db => db.AppLaunches.IgnoreQueryFilters().Single(l => l.Id == id).Status).Should().Be(LaunchStatus.AwaitingConfirmation);
    }

    [Fact]
    public async Task Abandoning_for_real_lets_go_of_the_upload()
    {
        var (workspaceId, _) = GivenAWorkspaceThatCanCreate("abandon");
        var client = Bearer(workspaceId, "launch-token-abandon@example.com", ip: "203.0.113.195");

        var id = await StartedAndReadAsync(client);
        var archive = Panel.Read(db => db.AppLaunches.IgnoreQueryFilters().Single(l => l.Id == id).ArchivePath);
        File.Exists(archive).Should().BeTrue();

        (await client.PostAsync($"/api/v1/launch/{id}/abandon", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        File.Exists(archive).Should().BeFalse();
        (await (await client.GetAsync($"/api/v1/launch/{id}")).JsonAsync()).GetProperty("status").GetString().Should().Be("Abandoned");
    }
}
