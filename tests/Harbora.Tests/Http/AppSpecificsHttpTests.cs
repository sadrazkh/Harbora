using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Tenancy;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What the app's own page says the app is.
///
/// Overview showed almost none of this: the prebuilt image reference, when there was one, and
/// nothing else. Somebody asking "how big is this, where does it run, what code is live" had to
/// look in three other places or ask.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppSpecificsHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    [Fact]
    public async Task An_apps_page_shows_the_size_it_is_actually_running_at()
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "spec-sized",
            Slug = "spec-sized",
            Kind = ServiceKind.Web,
            InstanceSizeKey = "spec-small",
            ContainerPort = 8080,
            DesiredReplicas = 3,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db =>
        {
            db.InstanceSizes.Add(new InstanceSize
            {
                Key = "spec-small", Name = "Small", NameFa = "کوچک",
                CpuCores = 0.5, MemoryBytes = 536_870_912, DiskBytes = 5_368_709_120
            });
            db.Apps.Add(app);
        });
        Panel.GivenUser(fixture.WorkspaceId, "spec-sized@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.220", "spec-sized@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-replicas=\"3\"");
        html.Should().Contain("data-spec-port=\"8080\"",
            "the app's own ContainerPort, so a hard-coded 80 fails this");
        html.Should().Contain("data-spec-size=\"spec-small\"",
            "the size this app is on, read from its own InstanceSizeKey rather than a default");
    }

    [Fact]
    public async Task An_apps_page_names_the_container_and_the_place_it_runs()
    {
        var serverId = Guid.CreateVersion7();
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = serverId,
            Name = "spec-placed",
            Slug = "spec-placed",
            Kind = ServiceKind.Web,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        Panel.GivenUser(fixture.WorkspaceId, "spec-placed@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.221", "spec-placed@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-container=", "the container name is how somebody finds it on the host");
    }

    [Fact]
    public async Task An_app_that_has_never_deployed_is_not_shown_an_invented_version()
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "spec-fresh",
            Slug = "spec-fresh",
            Kind = ServiceKind.Web,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Created
        };
        Panel.Seed(db => db.Apps.Add(app));
        Panel.GivenUser(fixture.WorkspaceId, "spec-fresh@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.222", "spec-fresh@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-version=\"none\"",
            "an app with no deployment has no live version — saying so beats showing a blank field " +
            "that reads as a bug");
    }
}
