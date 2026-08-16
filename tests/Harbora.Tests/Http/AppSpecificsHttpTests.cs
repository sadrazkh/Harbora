using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Servers;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Deployments;
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

        // The attribute above is only the raw key; the figures below come from the resolved
        // InstanceSize row, which the seed above exists to exercise. Deleting that seed used to
        // leave this whole test green, because nothing asserted past the key.
        //
        // Persian text renders through the razor view's HtmlEncoder as numeric character
        // references (&#xNNNN;) rather than raw UTF-8 bytes — the same convention
        // BillingPageHttpTests already asserts against — so these are the encoded forms of
        // "کوچک" (NameFa), "هسته" (vCPU), "حافظه" (memory) and "دیسک" (disk).
        html.Should().Contain("data-instance-size-state=\"known\"");
        html.Should().Contain("&#x6A9;&#x648;&#x686;&#x6A9;",
            "the Persian name, since the panel renders fa by default in tests and InstanceSize " +
            "carries its own NameFa for exactly this reader");
        html.Should().Contain("0&#x66B;5 &#x647;&#x633;&#x62A;&#x647;",
            "the CPU figure off the resolved row, not an invented default");
        html.Should().Contain("512 MB &#x62D;&#x627;&#x641;&#x638;&#x647;",
            "the memory figure computed from the row's MemoryBytes");
        html.Should().Contain("5 GB &#x62F;&#x6CC;&#x633;&#x6A9;",
            "the disk figure computed from the row's DiskBytes");
    }

    [Fact]
    public async Task An_apps_page_with_a_dangling_instance_size_key_shows_unknown_rather_than_zeroes()
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "spec-ghost-sized",
            Slug = "spec-ghost-sized",
            Kind = ServiceKind.Web,
            // No InstanceSize row carries this key — it was resized off of it, or the row was
            // deleted since. Either way the panel does not know this app's limits any more.
            InstanceSizeKey = "spec-deleted-size",
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        Panel.GivenUser(fixture.WorkspaceId, "spec-ghost-sized@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.225", "spec-ghost-sized@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-size=\"spec-deleted-size\"", "the raw key survives even though it resolves nowhere");
        html.Should().Contain("data-instance-size-state=\"unknown\"",
            "a dangling key is unknown limits, not a row of zeroes");
        html.Should().NotContain("data-instance-size-state=\"known\"");
    }

    [Fact]
    public async Task An_apps_page_names_the_container_and_the_place_it_runs()
    {
        var serverId = Guid.CreateVersion7();
        var server = new Server { Id = serverId, Name = "spec-host-alpha", IsLocal = true };
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
        Panel.Seed(db =>
        {
            db.Servers.Add(server);
            db.Apps.Add(app);
        });
        Panel.GivenUser(fixture.WorkspaceId, "spec-placed@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.221", "spec-placed@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        // This app has never deployed, so the container it would run as is the legacy name —
        // computed here the same way the controller computes it, not just any non-empty string.
        var expectedContainerName = DeploymentPlanning.LegacyContainerName(app.Slug);
        html.Should().Contain($"data-spec-container=\"{expectedContainerName}\"",
            "the container name is how somebody finds it on the host, not just that the attribute is present");
        html.Should().Contain($"data-spec-server=\"{server.Name}\"",
            "the place it runs, which the test's own name promises");
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

    /// <summary>An app with one succeeded deployment, so it has a container to ask about.</summary>
    private (Guid AppId, string ContainerName) SeedDeployedApp(string slug)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            Kind = ServiceKind.Web,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db =>
        {
            db.Apps.Add(app);
            db.Deployments.Add(new Deployment
            {
                AppId = app.Id,
                WorkspaceId = fixture.WorkspaceId,
                Number = 7,
                Status = DeploymentStatus.Succeeded,
                ImageTag = "harbora/seeded:build-7"
            });
        });
        return (app.Id, DeploymentPlanning.ContainerName(app.WorkspaceId, slug, 7));
    }

    [Fact]
    public async Task An_apps_page_shows_how_long_it_has_been_up_and_what_is_running()
    {
        const string digest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        var (appId, containerName) = SeedDeployedApp("spec-live");

        Panel.Docker.SeedDetail(containerName, new ContainerDetail(
            Id: "live123", Name: containerName, Image: "harbora/seeded:build-7",
            ImageDigest: digest, State: "running", Status: "Up 3 hours",
            Healthy: true, RestartCount: 2,
            StartedAt: new DateTimeOffset(2026, 8, 15, 6, 0, 0, TimeSpan.Zero)));

        Panel.GivenUser(fixture.WorkspaceId, "spec-live@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.223", "spec-live@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-restarts=\"2\"", "the count the engine reported, not a guess");
        // Scoped to the element meant to carry the digest, not a page-wide search — a page-wide
        // Contain would also pass if the digest were rendered somewhere else on the page entirely.
        html.Should().Contain($"data-spec-digest=\"{digest}\"",
            "the digest of what is actually running, straight from the engine");
    }

    [Fact]
    public async Task An_apps_page_says_not_checked_rather_than_healthy_when_no_health_check_is_configured()
    {
        var (appId, containerName) = SeedDeployedApp("spec-unchecked");

        // The image declares no HEALTHCHECK — the common case, and the one this whole sub-project
        // exists to get right: it must not read as an affirmative "healthy" verdict.
        Panel.Docker.SeedDetail(containerName, new ContainerDetail(
            Id: "unchecked123", Name: containerName, Image: "harbora/seeded:build-7",
            ImageDigest: null, State: "running", Status: "running",
            Healthy: null, RestartCount: 0,
            StartedAt: new DateTimeOffset(2026, 8, 15, 6, 0, 0, TimeSpan.Zero)));

        Panel.GivenUser(fixture.WorkspaceId, "spec-unchecked@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.226", "spec-unchecked@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-health=\"not-checked\"");
        // The Persian "not checked" label, encoded the same way BillingPageHttpTests already
        // asserts Persian text — see the size test above for why it is not the raw UTF-8 word.
        html.Should().Contain("&#x628;&#x62F;&#x648;&#x646; &#x628;&#x631;&#x631;&#x633;&#x6CC; &#x633;&#x644;&#x627;&#x645;&#x62A;",
            "the Persian 'not checked' label, since the panel renders fa by default");
        html.Should().NotContain("data-spec-health=\"healthy\"",
            "no health check configured must never read as a verdict that it passed one");
    }

    [Fact]
    public async Task When_the_engine_cannot_answer_the_page_says_it_does_not_know()
    {
        var (appId, _) = SeedDeployedApp("spec-silent");
        // Nothing seeded for this container, so InspectAsync returns null.

        Panel.GivenUser(fixture.WorkspaceId, "spec-silent@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.224", "spec-silent@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-health=\"unknown\"");
        html.Should().NotContain("data-spec-restarts=\"0\"",
            "a zero here is a specific, reassuring claim — 'it has never restarted' — that nobody " +
            "made. This is the assertion this task exists for");
    }

    [Fact]
    public async Task An_apps_page_shows_the_thirty_day_uptime_percent_and_restart_count_from_the_collected_series()
    {
        // The controller asks for a fixed 30-day window, and MetricRollups.BestSourceFor(30 days)
        // answers with hourly rollups (30 days sits inside the 31-day hourly retention) — raw samples
        // are never what this page reads, even for an app collected five minutes ago. So this seeds a
        // completed hour's rollup directly, the same shape the real rollup service would have already
        // produced, rather than raw MonitoringMetric rows the page would not look at.
        var (appId, containerName) = SeedDeployedApp("spec-history");
        var app = Panel.Read(db => db.Apps.First(a => a.Id == appId));
        var hour = Harbora.Infrastructure.Monitoring.MetricRollups.HourOf(DateTimeOffset.UtcNow.AddHours(-2));
        Panel.Seed(db =>
        {
            db.MetricRollups.Add(new Harbora.Domain.Monitoring.MetricRollup
            {
                ServerId = app.ServerId, Name = "app.up", ResourceRef = containerName,
                Period = Harbora.Domain.Monitoring.RollupPeriod.Hour, PeriodStart = hour,
                Minimum = 0, Maximum = 1, Average = 2.0 / 3, SampleCount = 3
            });
            db.MetricRollups.Add(new Harbora.Domain.Monitoring.MetricRollup
            {
                ServerId = app.ServerId, Name = "app.restarts", ResourceRef = containerName,
                Period = Harbora.Domain.Monitoring.RollupPeriod.Hour, PeriodStart = hour,
                Minimum = 0, Maximum = 1, Average = 1.0 / 3, SampleCount = 3
            });
        });

        Panel.GivenUser(fixture.WorkspaceId, "spec-history@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.228", "spec-history@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        // Two of three ticks were up: 66.7%. The Persian panel still formats the number itself with
        // invariant digits — MetricDisplay is deliberate about that — so this is not an encoded string.
        html.Should().Contain("data-spec-uptime-percent=\"66.7\"",
            "two of three collected ticks were up, and this is a real window, not a live-only figure");
        // Average x SampleCount recovers the true total (1.0/3 x 3 = 1), not the bare Average (0.33,
        // which would round to 0 and silently hide the one restart that actually happened).
        html.Should().Contain("data-spec-restart-count-30d=\"1\"",
            "the rollup's Average recombined with its SampleCount, not the Average alone");
        // The Persian "in the last 30 days" trailer, since the panel renders fa by default in tests
        // (encoded the same way the rest of this file's Persian assertions are — see the size test).
        html.Should().Contain("&#x62F;&#x631; &#x6F3;&#x6F0; &#x631;&#x648;&#x632; &#x627;&#x62E;&#x6CC;&#x631;",
            "the Persian trailer confirms this rendered fa, the panel's test default");
    }

    [Fact]
    public async Task An_apps_page_with_nothing_collected_shows_unknown_uptime_rather_than_a_fabricated_hundred_percent()
    {
        var (appId, _) = SeedDeployedApp("spec-history-silent");
        // No MonitoringMetric rows seeded for this container at all.

        Panel.GivenUser(fixture.WorkspaceId, "spec-history-silent@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.229", "spec-history-silent@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-uptime-percent=\"unknown\"");
        html.Should().NotContain("data-spec-uptime-percent=\"100\"",
            "nothing was ever collected for this container — that is not the same as a perfect record");
    }

    [Fact]
    public async Task A_cron_apps_page_says_there_is_no_container_to_check_rather_than_blaming_the_engine()
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "spec-cron",
            Slug = "spec-cron",
            Kind = ServiceKind.Cron,
            CronExpression = "0 * * * *",
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        // Nothing seeded on Panel.Docker for this container either — a cron app between runs has
        // none, so this must not be confused with the "engine did not answer" case.
        Panel.Seed(db => db.Apps.Add(app));
        Panel.GivenUser(fixture.WorkspaceId, "spec-cron@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.227", "spec-cron@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-health=\"not-applicable\"");
        // Encoded the same way Persian text renders throughout this page (see the size test above)
        // — this is "موتور کانتینر پاسخی نداد" ("the container engine did not answer"), the opening
        // of the copy a remote-node app gets. A cron app must not be told that.
        html.Should().NotContain(
            "&#x645;&#x648;&#x62A;&#x648;&#x631; &#x6A9;&#x627;&#x646;&#x62A;&#x6CC;&#x646;&#x631; &#x67E;&#x627;&#x633;&#x62E;&#x6CC; &#x646;&#x62F;&#x627;&#x62F;",
            "a cron app has no long-running container to ask between runs — that is not the same " +
            "reason a remote node with no inspect verb gives, and the page must not blame the engine");
    }
}
