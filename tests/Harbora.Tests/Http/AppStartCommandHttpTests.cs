using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// P2 (round-3 start-command plan) — the panel's only remedy for a buildpack that guessed a Worker's
/// process wrong, short of committing a Dockerfile to the repository. Real routes, a real cookie,
/// real Razor, the same idiom <see cref="AppReplicasHttpTests"/> already uses for the sibling
/// "write the row, apply on the next deploy" control.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppStartCommandHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>EnvironmentId is required (P2, 2026-08-17 app-environment-management design); a
    /// project and environment of the app's own keeps every app this class seeds inside a scope the
    /// signed-in owner actually has capability grants over.</summary>
    private Guid SeedApp(string slug, ServiceKind kind = ServiceKind.Web, string? startCommand = null)
    {
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            EnvironmentId = environmentId,
            Name = slug,
            Slug = slug,
            Kind = kind,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running,
            StartCommand = startCommand
        };
        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = fixture.WorkspaceId, Name = "Shop", Slug = "start-cmd-" + slug
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = fixture.WorkspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
            db.Apps.Add(app);
        });
        return app.Id;
    }

    // ------------------------------------------------------------------------------- saving it

    [Fact]
    public async Task Setting_a_start_command_writes_the_row_and_says_it_applies_next_deploy()
    {
        var appId = SeedApp("start-cmd-set");
        Panel.GivenUser(fixture.WorkspaceId, "start-cmd-set@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.220", "start-cmd-set@example.com");

        var response = await client.PostFormAsync($"/apps/{appId}/start-command",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("startCommand", "python bot.py"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().BeEquivalentTo($"/apps/details/{appId}");

        var stored = Panel.Read(db => db.Apps.First(a => a.Id == appId));
        stored.StartCommand.Should().Be("python bot.py");
    }

    [Fact]
    public async Task A_new_app_defaults_to_no_start_command()
    {
        // The column round-trips and defaults to null — proven by never posting to it at all.
        var appId = SeedApp("start-cmd-default");

        var stored = Panel.Read(db => db.Apps.First(a => a.Id == appId));
        stored.StartCommand.Should().BeNull();
    }

    [Fact]
    public async Task Whitespace_only_input_is_treated_as_unset_not_as_a_command_of_one_space()
    {
        var appId = SeedApp("start-cmd-ws", startCommand: "gunicorn app:app");
        Panel.GivenUser(fixture.WorkspaceId, "start-cmd-ws@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.221", "start-cmd-ws@example.com");

        var response = await client.PostFormAsync($"/apps/{appId}/start-command",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("startCommand", "   "));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var stored = Panel.Read(db => db.Apps.First(a => a.Id == appId));
        stored.StartCommand.Should().BeNull(
            "a whitespace-only post must clear a previously-set command, not store the spaces");
    }

    [Fact]
    public async Task Leading_and_trailing_whitespace_around_a_real_command_is_trimmed()
    {
        var appId = SeedApp("start-cmd-trim");
        Panel.GivenUser(fixture.WorkspaceId, "start-cmd-trim@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.222", "start-cmd-trim@example.com");

        var response = await client.PostFormAsync($"/apps/{appId}/start-command",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("startCommand", "  python bot.py  "));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var stored = Panel.Read(db => db.Apps.First(a => a.Id == appId));
        stored.StartCommand.Should().Be("python bot.py");
    }

    [Fact]
    public async Task Clearing_an_existing_start_command_persists_the_clear()
    {
        var appId = SeedApp("start-cmd-clear", startCommand: "python bot.py");
        Panel.GivenUser(fixture.WorkspaceId, "start-cmd-clear@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.223", "start-cmd-clear@example.com");

        var response = await client.PostFormAsync($"/apps/{appId}/start-command",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("startCommand", ""));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var stored = Panel.Read(db => db.Apps.First(a => a.Id == appId));
        stored.StartCommand.Should().BeNull();
    }

    // ------------------------------------------------------------------------- rendered, by kind

    [Fact]
    public async Task A_workers_page_offers_a_start_command_control()
    {
        var appId = SeedApp("start-cmd-worker", ServiceKind.Worker);
        Panel.GivenUser(fixture.WorkspaceId, "start-cmd-worker@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.224", "start-cmd-worker@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-start-command-input",
            "a Worker keeps running after it starts, so there is something here for the field to override");
        html.Should().Contain($"/apps/{appId}/start-command",
            "the control must post to the route that actually saves it");
    }

    [Fact]
    public async Task A_cron_apps_page_offers_no_start_command_control()
    {
        var appId = SeedApp("start-cmd-cron", ServiceKind.Cron);
        Panel.GivenUser(fixture.WorkspaceId, "start-cmd-cron@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.225", "start-cmd-cron@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().NotContain("data-start-command-input",
            "a scheduled job never starts a long-running container and already has its own Command field");
    }

    [Fact]
    public async Task A_release_tasks_page_offers_no_start_command_control()
    {
        var appId = SeedApp("start-cmd-release", ServiceKind.ReleaseTask);
        Panel.GivenUser(fixture.WorkspaceId, "start-cmd-release@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.226", "start-cmd-release@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().NotContain("data-start-command-input",
            "a release task runs once before a release and never starts a long-running container either");
    }
}
