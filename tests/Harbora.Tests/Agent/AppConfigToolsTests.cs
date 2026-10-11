using System.Text.Json;
using FluentAssertions;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// The configuration tools — environment variables, domains, volumes — over
/// <c>AppConfigurationService</c>: card text, each refusal the service or the tool makes, the
/// planned-app path, execution, and a refusal at execution after the world changed under the card.
/// </summary>
public class AppConfigToolsTests
{
    private const string Secret = "sk_live_TOPSECRET_42";

    private static string Everything(AgentValidation v) =>
        string.Join("\n", new[] { v.TitleEn, v.TitleFa, v.DetailEn, v.DetailFa }
            .Concat(v.WarningsEn ?? []).Concat(v.WarningsFa ?? []));

    private static string Everything(AgentToolResult r) =>
        string.Join("\n", r.ResultJson, r.SummaryEn, r.SummaryFa, r.Error, r.ErrorFa,
            string.Join(",", (r.Links ?? []).Select(l => l.Label + l.Url)));

    // ---- set_env_vars ---------------------------------------------------------------------------

    private static object TwoVariables(string app = "web") => new
    {
        app,
        variables = new object[]
        {
            new { key = "DATABASE_URL", value = "postgres://db/shop" },
            new { key = "STRIPE_KEY", value = Secret, secret = true }
        }
    };

    [Fact]
    public async Task Setting_variables_shows_plain_values_hides_the_secret_and_stores_it_encrypted()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.SetEnvVars(), TwoVariables());
        var result = await h.ExecuteAsync(h.SetEnvVars(), TwoVariables());

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Set 2 environment variables on web");
        verdict.TitleFa.Should().Be("تنظیم 2 متغیر محیطی روی web");
        verdict.DetailEn.Should().Contain("DATABASE_URL = postgres://db/shop").And.Contain("STRIPE_KEY (secret — value hidden)");
        verdict.DetailFa.Should().Contain("STRIPE_KEY (محرمانه — مقدار نمایش داده نمی‌شود)");
        verdict.WarningsEn.Should().Contain("Takes effect on the app's next deploy.");
        Everything(verdict).Should().NotContain(Secret);

        result.Ok.Should().BeTrue(result.Error);
        var stored = await h.Db.EnvironmentVariables.AsNoTracking().Where(v => v.App!.Slug == "web").ToListAsync();
        stored.Single(v => v.Key == "STRIPE_KEY").Value.Should().Be("enc:" + Secret);
        stored.Single(v => v.Key == "STRIPE_KEY").IsSecret.Should().BeTrue();
        stored.Single(v => v.Key == "DATABASE_URL").Value.Should().Be("postgres://db/shop");
        result.SummaryEn.Should().Contain("STRIPE_KEY (secret, new)").And.Contain("next deploy");
        result.SummaryFa.Should().Contain("STRIPE_KEY (محرمانه، جدید)");
        Everything(result).Should().NotContain(Secret, "a secret's value reaches neither the model nor the person");
        h.Agent.Audit.Entries.Where(e => e.Action == "app.env_set").Should().HaveCount(2)
            .And.OnlyContain(e => !e.MetadataJson!.Contains(Secret));
        result.Links.Should().ContainSingle().Which.Kind.Should().Be("app");
    }

    [Fact]
    public async Task Overwriting_says_which_value_it_replaces_and_warns_when_a_secret_would_become_plain()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.SetEnvVars(), new { app = "web", variables = new[] { new { key = "API_KEY", value = "now-plain" } } });

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Set environment variable API_KEY on web");
        verdict.DetailEn.Should().Contain("API_KEY = now-plain, replaces the current value");
        verdict.WarningsEn.Should().Contain(w => w.Contains("API_KEY is secret now"));
        verdict.WarningsFa.Should().HaveCount(verdict.WarningsEn!.Count);
    }

    [Theory]
    [InlineData("""{"app":"web","variables":[]}""", "Give at least one variable")]
    [InlineData("""{"app":"web"}""", "Give at least one variable")]
    [InlineData("""{"app":"web","variables":[{"value":"x"}]}""", "Every variable needs a key")]
    [InlineData("""{"app":"web","variables":[{"key":"A=B","value":"x"}]}""", "'A=B' cannot be a variable name")]
    [InlineData("""{"app":"web","variables":[{"key":"MY KEY","value":"x"}]}""", "'MY KEY' cannot be a variable name")]
    [InlineData("""{"app":"web","variables":[{"key":"A"}]}""", "Give a value for A")]
    [InlineData("""{"app":"nope","variables":[{"key":"A","value":"1"}]}""", "No app named 'nope' that you can see.")]
    public async Task A_malformed_set_is_refused_in_both_languages(string json, string expected)
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        var before = h.Snapshot();

        var verdict = await h.SetEnvVars().ValidateAsync(JsonDocument.Parse(json).RootElement, h.Context, new AgentPlanScope(), default);

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain(expected);
        verdict.ErrorFa.Should().MatchRegex("[؀-ۿ]");
        h.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task Variables_for_an_app_an_earlier_step_creates_validate_against_the_planned_app()
    {
        using var h = new AppToolsHarness();
        var scope = new AgentPlanScope();
        scope.Add(new AgentPlannedResource(AgentResourceKinds.App, "shop", h.ProjectSlug, "production"));

        var verdict = await h.ValidateAsync(h.SetEnvVars(), TwoVariables("shop"), scope);

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Set 2 environment variables on shop");
        verdict.DetailEn.Should().Contain("created by an earlier step of this plan");
        Everything(verdict).Should().NotContain(Secret);
    }

    [Fact]
    public async Task A_set_whose_app_was_deleted_after_the_card_fails_and_changes_nothing()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        (await h.ValidateAsync(h.SetEnvVars(), TwoVariables())).Ok.Should().BeTrue();
        h.Db.Apps.Remove(await h.Db.Apps.SingleAsync(a => a.Id == app.Id));
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        var before = h.Snapshot();

        var result = await h.ExecuteAsync(h.SetEnvVars(), TwoVariables());

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("No app named 'web' that you can see.");
        h.Snapshot().Should().Be(before);
    }

    // ---- delete_env_var -------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_variable_names_it_warns_about_the_next_deploy_and_removes_it()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.DeleteEnvVar(), new { app = "web", key = "API_KEY" });
        var result = await h.ExecuteAsync(h.DeleteEnvVar(), new { app = "web", key = "API_KEY" });

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Delete environment variable API_KEY from web");
        verdict.TitleFa.Should().Be("حذف متغیر محیطی API_KEY از web");
        verdict.DetailEn.Should().Contain("API_KEY (secret)");
        verdict.WarningsEn.Should().Contain("Removing configuration can break the app on its next deploy.");
        result.Ok.Should().BeTrue(result.Error);
        (await h.Db.EnvironmentVariables.AnyAsync(v => v.Key == "API_KEY")).Should().BeFalse();
        result.SummaryEn.Should().Be("Deleted API_KEY from 'web'. It takes effect on the app's next deploy.");
        Everything(result).Should().NotContain("sk-live-0000");
        h.Agent.Audit.Entries.Should().Contain(e => e.Action == "app.env_deleted");
    }

    [Fact]
    public async Task A_variable_that_is_not_set_is_refused_and_one_removed_after_the_card_fails()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var missing = await h.ValidateAsync(h.DeleteEnvVar(), new { app = "web", key = "NOPE" });
        (await h.ValidateAsync(h.DeleteEnvVar(), new { app = "web", key = "PORT" })).Ok.Should().BeTrue();
        await h.ExecuteAsync(h.DeleteEnvVar(), new { app = "web", key = "PORT" });
        var before = h.Snapshot();
        var again = await h.ExecuteAsync(h.DeleteEnvVar(), new { app = "web", key = "PORT" });

        missing.Ok.Should().BeFalse();
        missing.Error.Should().Be("That variable is not set on this app.");
        missing.ErrorFa.Should().Be("این متغیر روی این اپ تنظیم نشده است.");
        again.Ok.Should().BeFalse();
        again.Error.Should().Be("That variable is not set on this app.");
        h.Snapshot().Should().Be(before);
    }

    // ---- add_domain -----------------------------------------------------------------------------

    [Fact]
    public async Task Adding_a_domain_validates_with_its_card_and_attaches_it()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.AddDomain(), new { app = "web", host = "Shop.Example.com " });
        var result = await h.ExecuteAsync(h.AddDomain(), new { app = "web", host = "Shop.Example.com " });

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Add domain shop.example.com to web");
        verdict.TitleFa.Should().Be("افزودن دامنهٔ shop.example.com به web");
        verdict.DetailEn.Should().Contain("HTTPS on").And.NotContain("primary");
        verdict.WarningsEn.Should().ContainSingle().Which.Should().Contain("next deploy");
        result.Ok.Should().BeTrue(result.Error);
        var domain = await h.Db.Domains.AsNoTracking().SingleAsync(d => d.Host == "shop.example.com");
        domain.SslEnabled.Should().BeTrue();
        domain.IsPrimary.Should().BeFalse();
        result.SummaryEn.Should().Be("Added shop.example.com to 'web'. It is routed on the app's next deploy.");
        result.SummaryFa.Should().Contain("مسیردهی");
        h.Agent.Audit.Entries.Should().Contain(e => e.Action == "app.domain_added");
    }

    [Fact]
    public async Task The_first_domain_of_an_app_becomes_its_primary_address()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        h.Db.Domains.RemoveRange(h.Db.Domains.Where(d => d.AppId == app.Id));
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var verdict = await h.ValidateAsync(h.AddDomain(), new { app = "web", host = "web.example.com", ssl = false });
        var result = await h.ExecuteAsync(h.AddDomain(), new { app = "web", host = "web.example.com", ssl = false });

        verdict.DetailEn.Should().Contain("HTTPS off").And.Contain("becomes the app's primary address");
        result.SummaryEn.Should().Contain("as its primary address");
        (await h.Db.Domains.AsNoTracking().SingleAsync(d => d.Host == "web.example.com")).IsPrimary.Should().BeTrue();
    }

    [Theory]
    [InlineData("other.example.org", "This domain is already in use.")]
    [InlineData(AppToolsHarness.PanelDomain, "one of the platform's own host names")]
    [InlineData("", "Host is required.")]
    public async Task A_host_the_service_refuses_is_refused_in_its_words(string host, string expected)
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        h.SeedApp("other");

        var verdict = await h.ValidateAsync(h.AddDomain(), new { app = "web", host });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain(expected);
        verdict.ErrorFa.Should().MatchRegex("[؀-ۿ]");
    }

    [Fact]
    public async Task A_domain_for_a_planned_app_is_checked_for_everything_that_needs_no_app_row()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("other");
        var scope = new AgentPlanScope();
        scope.Add(new AgentPlannedResource(AgentResourceKinds.App, "web", h.ProjectSlug, "production"));

        var free = await h.ValidateAsync(h.AddDomain(), new { app = "web", host = "web.example.com" }, scope);
        var taken = await h.ValidateAsync(h.AddDomain(), new { app = "web", host = "other.example.org" }, scope);
        var blank = await h.ValidateAsync(h.AddDomain(), new { app = "web", host = " " }, scope);

        free.Ok.Should().BeTrue(free.Error);
        free.DetailEn.Should().Contain("created by an earlier step of this plan");
        taken.Error.Should().Be("This domain is already in use.");
        blank.Error.Should().Be("Host is required.");
    }

    [Fact]
    public async Task A_host_claimed_after_the_card_fails_at_execution_and_nothing_is_added()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        (await h.ValidateAsync(h.AddDomain(), new { app = "web", host = "shop.example.com" })).Ok.Should().BeTrue();
        var rival = h.SeedForeignApp("rival");
        h.Db.Domains.Add(new Harbora.Domain.Networking.DomainName { AppId = rival.Id, Host = "shop.example.com" });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        var before = h.Snapshot();

        var result = await h.ExecuteAsync(h.AddDomain(), new { app = "web", host = "shop.example.com" });

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("This domain is already in use.");
        h.Snapshot().Should().Be(before);
    }

    // ---- remove_domain --------------------------------------------------------------------------

    [Fact]
    public async Task Removing_the_primary_names_the_domain_that_takes_over_and_it_does()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        h.Db.Domains.Add(new Harbora.Domain.Networking.DomainName { AppId = app.Id, Host = "www.example.com", CreatedAt = AgentHarness.Start.AddHours(1) });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var verdict = await h.ValidateAsync(h.RemoveDomain(), new { app = "web", host = "web.example.org" });
        var result = await h.ExecuteAsync(h.RemoveDomain(), new { app = "web", host = "web.example.org" });

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Remove domain web.example.org from web");
        verdict.DetailEn.Should().Contain("It is the primary address; www.example.com becomes the primary address.");
        verdict.DetailFa.Should().Contain("www.example.com نشانی اصلی می‌شود");
        verdict.WarningsEn.Should().Contain("Visitors using web.example.org can no longer reach the app.");
        result.Ok.Should().BeTrue(result.Error);
        result.SummaryEn.Should().Be("Removed web.example.org from 'web'. www.example.com is now its primary address.");
        (await h.Db.Domains.AsNoTracking().SingleAsync(d => d.Host == "www.example.com")).IsPrimary.Should().BeTrue();
        h.Agent.Audit.Entries.Should().Contain(e => e.Action == "app.domain_removed");
    }

    [Fact]
    public async Task Removing_the_only_domain_says_the_app_is_left_with_no_address()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.RemoveDomain(), new { app = "web", host = "web.example.org" });

        verdict.DetailEn.Should().Contain("It is the app's only domain: web is left with no address.");
        verdict.DetailFa.Should().Contain("بدون هیچ نشانی می‌ماند");
    }

    [Fact]
    public async Task Removing_a_secondary_domain_keeps_the_primary()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        h.Db.Domains.Add(new Harbora.Domain.Networking.DomainName { AppId = app.Id, Host = "www.example.com" });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var verdict = await h.ValidateAsync(h.RemoveDomain(), new { app = "web", host = "www.example.com" });

        verdict.DetailEn.Should().Contain("web.example.org stays the primary address.");
    }

    [Fact]
    public async Task A_domain_that_is_not_attached_is_refused_and_one_gone_after_the_card_fails()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var missing = await h.ValidateAsync(h.RemoveDomain(), new { app = "web", host = "nope.example.com" });
        await h.ExecuteAsync(h.RemoveDomain(), new { app = "web", host = "web.example.org" });
        var before = h.Snapshot();
        var again = await h.ExecuteAsync(h.RemoveDomain(), new { app = "web", host = "web.example.org" });

        missing.Error.Should().Be("That domain is not attached to this app.");
        missing.ErrorFa.Should().Be("این دامنه به این اپ وصل نیست.");
        again.Ok.Should().BeFalse();
        h.Snapshot().Should().Be(before);
    }

    // ---- add_volume -----------------------------------------------------------------------------

    [Fact]
    public async Task Adding_a_volume_validates_with_its_card_and_says_it_is_attached_on_the_next_deployment()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.AddVolume(), new { app = "web", mountPath = "/data/" });
        var result = await h.ExecuteAsync(h.AddVolume(), new { app = "web", mountPath = "/data/" });

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.TitleEn.Should().Be("Add a volume at /data to web");
        verdict.TitleFa.Should().Be("افزودن والیوم در /data به web");
        verdict.WarningsEn.Should().Contain("It is attached to the container on the app's next deployment.");
        result.Ok.Should().BeTrue(result.Error);
        (await h.Db.Volumes.AsNoTracking().AnyAsync(v => v.AppId == app.Id && v.MountPath == "/data")).Should().BeTrue();
        result.SummaryEn.Should().Be("/data was added to 'web'. It is attached to the container on the next deployment.");
        result.Links.Should().ContainSingle().Which.Url.Should().Be($"/apps/{app.Id}/volumes");
    }

    [Theory]
    [InlineData("data", "must start with /")]
    [InlineData("/files", "/files is already mounted.")]
    [InlineData("", "Type a path")]
    public async Task A_mount_path_the_service_refuses_is_refused_in_its_words(string path, string expected)
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.AddVolume(), new { app = "web", mountPath = path });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Contain(expected);
        verdict.ErrorFa.Should().MatchRegex("[؀-ۿ]");
    }

    [Fact]
    public async Task A_volume_for_a_planned_app_checks_the_path_and_the_plan()
    {
        using var h = new AppToolsHarness();
        var scope = new AgentPlanScope();
        scope.Add(new AgentPlannedResource(AgentResourceKinds.App, "web", h.ProjectSlug, "production"));

        var fine = await h.ValidateAsync(h.AddVolume(), new { app = "web", mountPath = "/data" }, scope);
        var relative = await h.ValidateAsync(h.AddVolume(), new { app = "web", mountPath = "data" }, scope);

        fine.Ok.Should().BeTrue(fine.Error);
        fine.DetailEn.Should().Contain("created by an earlier step of this plan");
        relative.Ok.Should().BeFalse();
        relative.Error.Should().Contain("must start with /");
    }

    [Fact]
    public async Task A_path_mounted_by_someone_else_after_the_card_fails_at_execution()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        (await h.ValidateAsync(h.AddVolume(), new { app = "web", mountPath = "/data" })).Ok.Should().BeTrue();
        h.Db.Volumes.Add(new Harbora.Domain.Apps.Volume { AppId = app.Id, Name = "harbora-vol-web-data", MountPath = "/data" });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        var before = h.Snapshot();

        var result = await h.ExecuteAsync(h.AddVolume(), new { app = "web", mountPath = "/data" });

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("/data is already mounted.");
        h.Snapshot().Should().Be(before);
    }

    // ---- remove_volume --------------------------------------------------------------------------

    [Fact]
    public async Task Removing_a_volume_keeps_its_data_by_default_and_is_confirmed_with_the_slug()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.RemoveVolume(), new { app = "web", mountPath = "/files" });
        var result = await h.ExecuteAsync(h.RemoveVolume(), new { app = "web", mountPath = "/files" });

        verdict.Ok.Should().BeTrue(verdict.Error);
        verdict.ConfirmationPhrase.Should().Be("web");
        verdict.TitleEn.Should().Be("Remove the volume at /files from web");
        verdict.DetailEn.Should().Be("web stops mounting /files; the data stays on the server in volume harbora-vol-web-files.");
        verdict.WarningsEn.Should().Contain("From its next deployment the app sees an empty /files.");
        result.Ok.Should().BeTrue(result.Error);
        (await h.Db.Volumes.AnyAsync(v => v.MountPath == "/files")).Should().BeFalse();
        h.Docker.CountOf("RemoveVolumeAsync").Should().Be(0);
        result.SummaryEn.Should().Contain("no longer mounted").And.Contain("still on the server");
        h.Agent.Audit.Entries.Should().Contain(e => e.Action == "app.volume_detached");
    }

    [Fact]
    public async Task Removing_a_volume_with_its_data_says_so_and_deletes_it()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.RemoveVolume(), new { app = "web", mountPath = "/files", deleteData = true });
        var result = await h.ExecuteAsync(h.RemoveVolume(), new { app = "web", mountPath = "/files", deleteData = true });

        verdict.TitleEn.Should().Be("Remove the volume at /files from web and delete its data");
        verdict.TitleFa.Should().Contain("حذف داده‌هایش");
        verdict.DetailEn.Should().Contain("is deleted from the server and cannot be recovered");
        verdict.WarningsEn.Should().Contain("This cannot be undone.");
        result.Ok.Should().BeTrue(result.Error);
        h.Docker.OperationsOn("harbora-vol-web-files").Should().Contain("RemoveVolumeAsync");
        result.SummaryEn.Should().Be("/files and its data were deleted from 'web'.");
        h.Agent.Audit.Entries.Should().Contain(e => e.Action == "app.volume_deleted");
    }

    [Fact]
    public async Task A_protected_volumes_data_cannot_be_deleted_but_it_can_be_unmounted()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        (await h.Db.Volumes.SingleAsync(v => v.AppId == app.Id)).Protected = true;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var deleting = await h.ValidateAsync(h.RemoveVolume(), new { app = "web", mountPath = "/files", deleteData = true });
        var unmounting = await h.ValidateAsync(h.RemoveVolume(), new { app = "web", mountPath = "/files" });

        deleting.Ok.Should().BeFalse();
        deleting.Error.Should().Contain("Protected");
        deleting.ErrorFa.Should().MatchRegex("[؀-ۿ]");
        unmounting.Ok.Should().BeTrue(unmounting.Error);
    }

    [Fact]
    public async Task A_volume_made_protected_after_the_card_keeps_its_data_and_its_mount()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        (await h.ValidateAsync(h.RemoveVolume(), new { app = "web", mountPath = "/files", deleteData = true })).Ok.Should().BeTrue();
        (await h.Db.Volumes.SingleAsync(v => v.AppId == app.Id)).Protected = true;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        var before = h.Snapshot();

        var result = await h.ExecuteAsync(h.RemoveVolume(), new { app = "web", mountPath = "/files", deleteData = true });

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("Protected");
        h.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task Data_docker_did_not_delete_fails_the_step_and_says_where_it_still_is()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        h.Docker.UnremovableVolumes.Add("harbora-vol-web-files");

        var result = await h.ExecuteAsync(h.RemoveVolume(), new { app = "web", mountPath = "/files", deleteData = true });

        result.Ok.Should().BeFalse("the person asked for the data to go, and it did not");
        result.Error.Should().Contain("was unmounted").And.Contain("deleting its data failed").And.Contain("harbora-vol-web-files");
        result.ErrorFa.Should().Contain("ناموفق");
        result.ResultJson.Should().Contain("\"dataDeleted\":false");
    }

    [Fact]
    public async Task A_volume_that_is_not_mounted_is_refused()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");

        var verdict = await h.ValidateAsync(h.RemoveVolume(), new { app = "web", mountPath = "/nope" });

        verdict.Ok.Should().BeFalse();
        verdict.Error.Should().Be("That volume is not mounted on this app.");
    }
}
