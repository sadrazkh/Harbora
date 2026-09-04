using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <c>GET /api/v1/apps/{slug}/env</c> — the CLI-facing endpoint behind <c>harbora env pull</c> and
/// <c>harbora run</c> (4.1, 2026-09-04 local-dev-parity plan).
///
/// <para>
/// Precedence and merge correctness are already proven where the merge itself lives
/// (<c>ConfigGroupMergeTests</c>) and where the deploy pipeline assembles it
/// (<c>ConfigGroupPipelineTests</c> and friends). What is specific to this endpoint, and covered here,
/// is the HTTP surface: a real bearer token reaches it, a secret comes back decrypted and marked, a
/// viewer's token is refused the same way editing an env var already is, and an app that is not the
/// caller's answers exactly like every other endpoint in this controller does. The byte-for-byte
/// "the CLI would pull exactly what a deploy would inject" guarantee is proven at the shared assembly
/// point itself by <c>EffectiveEnvironmentBuilderParityTests</c>, since only <see cref="PipelineHarness"/>
/// -style tests actually run a deployment — this factory's <c>IDeploymentEngine</c> is a recorder.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ApiV1EnvHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private (Guid AppId, string Slug) SeedApp(string slug)
    {
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, ServerId = Guid.CreateVersion7(), EnvironmentId = environmentId,
            Name = slug, Slug = slug, SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0", Status = AppStatus.Running
        };
        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = fixture.WorkspaceId, Name = "Env", Slug = "env-" + slug
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = fixture.WorkspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
            db.Apps.Add(app);
        });
        return (app.Id, app.Slug);
    }

    [Fact]
    public async Task An_owners_token_pulls_the_effective_environment_decrypted_and_marked()
    {
        var (appId, slug) = SeedApp("env-pull-basic");
        var protector = Panel.Resolve<ISecretProtector>();
        var group = new ConfigGroup { WorkspaceId = fixture.WorkspaceId, Name = "shared" };
        Panel.Seed(db =>
        {
            db.EnvironmentVariables.Add(new EnvironmentVariable
            {
                AppId = appId, Key = "API_BASE", Value = "https://api.example.com", IsSecret = false
            });
            db.EnvironmentVariables.Add(new EnvironmentVariable
            {
                AppId = appId, Key = "APP_SECRET", Value = protector.Protect("own-secret-value"), IsSecret = true
            });
            db.ConfigGroups.Add(group);
            db.ConfigGroupEntries.Add(new ConfigGroupEntry
            {
                ConfigGroupId = group.Id, Key = "LOG_LEVEL", Value = "debug", IsSecret = false
            });
            db.AppConfigGroups.Add(new AppConfigGroup { AppId = appId, ConfigGroupId = group.Id, AttachOrder = 1 });
        });
        var owner = Panel.GivenUser(fixture.WorkspaceId, "env-pull-owner@example.com", SystemRole.Owner);
        var token = Panel.GivenApiToken(owner.Id);

        var response = await Panel.BearerClientFrom("203.0.113.60", token).GetAsync($"/api/v1/apps/{slug}/env");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.JsonAsync();
        var entries = body.EnumerateArray().ToDictionary(e => e.GetProperty("key").GetString()!);

        entries["API_BASE"].GetProperty("value").GetString().Should().Be("https://api.example.com");
        entries["API_BASE"].GetProperty("isSecret").GetBoolean().Should().BeFalse();
        entries["API_BASE"].GetProperty("source").GetString().Should().Be("App");

        entries["LOG_LEVEL"].GetProperty("value").GetString().Should().Be("debug");
        entries["LOG_LEVEL"].GetProperty("source").GetString().Should().Be("Group");

        entries["APP_SECRET"].GetProperty("isSecret").GetBoolean().Should().BeTrue(
            "the point of `harbora env pull` is that a developer stops copying a credential by hand — " +
            "the CLI has to be told which values are secret");
        entries["APP_SECRET"].GetProperty("value").GetString().Should().Be("own-secret-value",
            "unlike the panel's own env page, this endpoint hands back real plaintext — a developer " +
            "needs the actual value to run the app locally");
    }

    [Fact]
    public async Task The_apps_own_variable_beats_a_group_here_too()
    {
        var (appId, slug) = SeedApp("env-pull-precedence");
        var group = new ConfigGroup { WorkspaceId = fixture.WorkspaceId, Name = "shared" };
        Panel.Seed(db =>
        {
            db.EnvironmentVariables.Add(new EnvironmentVariable
            {
                AppId = appId, Key = "PORT", Value = "9000", IsSecret = false
            });
            db.ConfigGroups.Add(group);
            db.ConfigGroupEntries.Add(new ConfigGroupEntry
            {
                ConfigGroupId = group.Id, Key = "PORT", Value = "8080", IsSecret = false
            });
            db.AppConfigGroups.Add(new AppConfigGroup { AppId = appId, ConfigGroupId = group.Id, AttachOrder = 1 });
        });
        var owner = Panel.GivenUser(fixture.WorkspaceId, "env-pull-precedence@example.com", SystemRole.Owner);
        var token = Panel.GivenApiToken(owner.Id);

        var response = await Panel.BearerClientFrom("203.0.113.61", token).GetAsync($"/api/v1/apps/{slug}/env");

        var body = await response.JsonAsync();
        var port = body.EnumerateArray().Single(e => e.GetProperty("key").GetString() == "PORT");
        port.GetProperty("value").GetString().Should().Be("9000",
            "the same precedence ConfigGroupMerge/BuildEnv give the container must hold for a pull too");
    }

    [Fact]
    public async Task A_viewers_token_authenticates_but_cannot_pull_env()
    {
        var (_, slug) = SeedApp("env-pull-viewer");
        var viewer = Panel.GivenUser(fixture.WorkspaceId, "env-pull-viewer-tok@example.com", SystemRole.Viewer);
        var token = Panel.GivenApiToken(viewer.Id);

        var response = await Panel.BearerClientFrom("203.0.113.62", token).GetAsync($"/api/v1/apps/{slug}/env");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "handing back a secret's plaintext is at least as sensitive as editing one, which a viewer " +
            "already cannot do");
    }

    [Fact]
    public async Task No_token_is_a_401()
    {
        var (_, slug) = SeedApp("env-pull-anon");

        var response = await Panel.ClientFrom("203.0.113.63").GetAsync($"/api/v1/apps/{slug}/env");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_app_that_is_not_there_and_an_app_that_is_not_yours_answer_the_same()
    {
        var (_, _) = SeedApp("env-pull-not-mine");
        var owner = Panel.GivenUser(fixture.WorkspaceId, "env-pull-scope@example.com", SystemRole.Owner);
        var token = Panel.GivenApiToken(owner.Id);
        var otherWorkspace = Guid.CreateVersion7();
        Panel.Seed(db => db.Apps.Add(new App
        {
            WorkspaceId = otherWorkspace, Name = "stranger", Slug = "env-pull-strangers-app",
            SourceType = AppSourceType.Upload
        }));
        var client = Panel.BearerClientFrom("203.0.113.64", token);

        var missing = await client.GetAsync("/api/v1/apps/env-pull-no-such-app/env");
        var theirs = await client.GetAsync("/api/v1/apps/env-pull-strangers-app/env");

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        theirs.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another workspace's app must be indistinguishable from one that does not exist");
        (await missing.DocumentedErrorAsync()).Should().Be("App not found.");
        (await theirs.DocumentedErrorAsync()).Should().Be("App not found.");
    }
}
