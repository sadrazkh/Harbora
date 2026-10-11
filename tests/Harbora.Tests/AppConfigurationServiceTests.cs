using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Storage;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="AppConfigurationService"/>: env vars, domains and volumes, moved out of
/// <c>AppsController</c> so the AI agent's tools run the same code the app pages do. The HTTP tests
/// (<c>BuildArgCheckboxHttpTests</c>, <c>AppAddressHttpTests</c>, <c>StatusPageDomainHttpTests</c>,
/// <c>VolumeProtectionHttpTests</c>, <c>AppDetailTabsHttpTests</c>) are the parity net for the pages;
/// these pin the service's own contract — refusals, validate changing nothing, audit entries that never
/// carry a value, and the per-app access check made by the service itself.
/// </summary>
public sealed class AppConfigurationServiceTests
{
    private static async Task<App> SeedAppAsync(AppServiceHarness h, string slug = "shop")
    {
        var app = new App
        {
            WorkspaceId = h.WorkspaceId, EnvironmentId = h.Environment!.Id, ServerId = h.Server.Id,
            Name = slug, Slug = slug, SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/shop:1"
        };
        h.Db.Apps.Add(app);
        await h.Db.SaveChangesAsync();
        return app;
    }

    // ---- environment variables ----

    [Fact]
    public async Task Setting_variables_creates_and_overwrites_them_encrypts_a_secret_and_audits_keys_only()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        h.Db.EnvironmentVariables.Add(new EnvironmentVariable { AppId = app.Id, Key = "MODE", Value = "dev" });
        await h.Db.SaveChangesAsync();

        var outcome = await h.Configuration().SetEnvAsync(h.Actor, app.Id,
        [
            new AppEnvVarInput("MODE", "production"),
            new AppEnvVarInput("DB_PASSWORD", "hunter2-correct-horse", IsSecret: true, AvailableAtBuild: true)
        ], default);

        outcome.Ok.Should().BeTrue();
        outcome.Value.Should().Equal(
            new AppEnvVarChange("MODE", Created: false, IsSecret: false, AvailableAtBuild: false),
            new AppEnvVarChange("DB_PASSWORD", Created: true, IsSecret: true, AvailableAtBuild: true));

        var rows = await h.Db.EnvironmentVariables.AsNoTracking().Where(e => e.AppId == app.Id).ToListAsync();
        rows.Single(e => e.Key == "MODE").Value.Should().Be("production");
        var secret = rows.Single(e => e.Key == "DB_PASSWORD");
        secret.IsSecret.Should().BeTrue();
        secret.AvailableAtBuild.Should().BeTrue();
        secret.Value.Should().NotBe("hunter2-correct-horse");
        new PassthroughProtector().Unprotect(secret.Value).Should().Be("hunter2-correct-horse");

        h.Audit.Entries.Select(e => (e.Action, e.TargetId)).Should().Equal(
            ("app.env_set", "shop:MODE"), ("app.env_set", "shop:DB_PASSWORD"));
        h.Audit.Entries.Should().OnlyContain(e => e.WorkspaceId == h.WorkspaceId && e.IpAddress == "203.0.113.9");
        h.Audit.Entries.Should().OnlyContain(e =>
            !e.MetadataJson!.Contains("hunter2") && !e.MetadataJson.Contains("production") && !e.TargetId!.Contains("hunter2"),
            "an audit log must never become a second copy of a variable's value");
    }

    [Fact]
    public void An_input_never_prints_its_value() =>
        new AppEnvVarInput("TOKEN", "s3cr3t", IsSecret: true).ToString().Should().NotContain("s3cr3t");

    [Fact]
    public async Task A_blank_key_or_no_variables_at_all_is_refused_and_nothing_is_written()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        var before = h.Snapshot();

        var blank = await h.Configuration().SetEnvAsync(h.Actor, app.Id, [new AppEnvVarInput(" ", "x")], default);
        var none = await h.Configuration().SetEnvAsync(h.Actor, app.Id, [], default);

        blank.Refusals.Should().Equal(new ServiceRefusal("Key is required.", "نام متغیر (Key) لازم است.", "Key"));
        none.Refusals.Should().ContainSingle().Which.MessageFa.Should().NotBeNullOrWhiteSpace();
        h.Snapshot().Should().Be(before);
        h.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Validating_variables_writes_nothing_and_answers_what_the_set_would()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        var before = h.Snapshot();
        var service = h.Configuration();

        (await service.ValidateSetEnvAsync(h.Actor, app.Id, [new AppEnvVarInput("A", "1", IsSecret: true)], default)).Should().BeEmpty();
        (await service.ValidateSetEnvAsync(h.Actor, app.Id, [new AppEnvVarInput("", "1")], default))
            .Should().ContainSingle().Which.Field.Should().Be("Key");
        (await service.ValidateDeleteEnvAsync(h.Actor, app.Id, AppItemRef.ByName("MISSING"), default))
            .Should().Equal(new ServiceRefusal("That variable is not set on this app.", "این متغیر روی این اپ تنظیم نشده است.", "Key"));

        h.Snapshot().Should().Be(before);
        h.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_variable_by_id_or_by_key_removes_exactly_it_and_audits_the_key()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        var first = new EnvironmentVariable { AppId = app.Id, Key = "FIRST", Value = "1" };
        var second = new EnvironmentVariable { AppId = app.Id, Key = "SECOND", Value = "secret-two", IsSecret = true };
        h.Db.EnvironmentVariables.AddRange(first, second);
        await h.Db.SaveChangesAsync();

        (await h.Configuration().DeleteEnvAsync(h.Actor, app.Id, AppItemRef.ById(first.Id), default)).Value.Should().Be("FIRST");
        (await h.Configuration().DeleteEnvAsync(h.Actor, app.Id, AppItemRef.ByName("SECOND"), default)).Value.Should().Be("SECOND");

        (await h.Db.EnvironmentVariables.CountAsync(e => e.AppId == app.Id)).Should().Be(0);
        h.Audit.Entries.Select(e => (e.Action, e.TargetId)).Should().Equal(("app.env_deleted", "shop:FIRST"), ("app.env_deleted", "shop:SECOND"));
        h.Audit.Entries.Should().OnlyContain(e => !e.MetadataJson!.Contains("secret-two"));
    }

    [Fact]
    public async Task Deleting_a_variable_that_is_not_there_says_so_instead_of_reporting_a_removal()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        var elsewhere = await SeedAppAsync(h, "other");
        var theirs = new EnvironmentVariable { AppId = elsewhere.Id, Key = "KEEP", Value = "1" };
        h.Db.EnvironmentVariables.Add(theirs);
        await h.Db.SaveChangesAsync();

        var outcome = await h.Configuration().DeleteEnvAsync(h.Actor, app.Id, AppItemRef.ById(theirs.Id), default);

        outcome.Ok.Should().BeFalse("a variable of another app is not this app's to delete");
        (await h.Db.EnvironmentVariables.AnyAsync(e => e.Id == theirs.Id)).Should().BeTrue();
        h.Audit.Entries.Should().BeEmpty("a removal that did not happen is not audited as one");
    }

    // ---- domains ----

    [Fact]
    public async Task Adding_domains_normalises_the_host_makes_the_first_primary_and_audits_each()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);

        var first = await h.Configuration().AddDomainAsync(h.Actor, app.Id, "  Shop.MyCompany.com ", ssl: true, default);
        var second = await h.Configuration().AddDomainAsync(h.Actor, app.Id, "www.mycompany.com", ssl: false, default);

        first.Value.Should().BeEquivalentTo(new { Host = "shop.mycompany.com", SslEnabled = true, IsPrimary = true });
        second.Value!.IsPrimary.Should().BeFalse();
        var stored = await h.Db.Domains.AsNoTracking().SingleAsync(d => d.Host == "shop.mycompany.com");
        stored.ForceHttps.Should().BeTrue();
        stored.AppId.Should().Be(app.Id);
        h.Audit.Entries.Select(e => (e.Action, e.TargetId)).Should().Equal(
            ("app.domain_added", "shop:shop.mycompany.com"), ("app.domain_added", "shop:www.mycompany.com"));
        h.Audit.Entries.Should().OnlyContain(e => e.WorkspaceId == h.WorkspaceId);
    }

    public static TheoryData<string, string, string> DomainRefusals => new()
    {
        { "a blank host", "   ", "Host is required." },
        { "a host another tenant already routes", "taken.example.org", "This domain is already in use." },
        { "the panel's own host", AppServiceHarness.PanelDomain, $"'{AppServiceHarness.PanelDomain}' is one of the platform's own host names and cannot be routed to an app." },
        { "a status page's reserved prefix under the root domain", $"status-acme.{AppServiceHarness.RootDomain}", $"'status-acme.{AppServiceHarness.RootDomain}' is one of the platform's own host names and cannot be routed to an app." }
    };

    [Theory]
    [MemberData(nameof(DomainRefusals))]
    public async Task A_domain_the_app_may_not_have_is_refused_by_validate_and_add_alike_and_nothing_is_written(
        string because, string host, string message)
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        h.Db.Apps.Add(new App
        {
            WorkspaceId = Guid.CreateVersion7(), Name = "theirs", Slug = "theirs", SourceType = AppSourceType.PrebuiltImage,
            Domains = { new DomainName { Host = "taken.example.org", IsPrimary = true } }
        });
        await h.Db.SaveChangesAsync();
        var before = h.Snapshot();

        var validated = await h.Configuration().ValidateAddDomainAsync(h.Actor, app.Id, host, default);
        var added = await h.Configuration().AddDomainAsync(h.Actor, app.Id, host, ssl: true, default);

        validated.Should().ContainSingle(because).Which.Message.Should().Be(message);
        validated.Single().MessageFa.Should().NotBeNullOrWhiteSpace().And.NotBe(message);
        added.Refusals.Should().Equal(validated);
        h.Snapshot().Should().Be(before);
        h.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task The_plans_domain_ceiling_is_refused_in_both_languages()
    {
        using var h = new AppServiceHarness();
        h.Plan.MaxDomains = 1;
        var app = await SeedAppAsync(h);
        h.Db.Domains.Add(new DomainName { AppId = app.Id, Host = "first.example.org", IsPrimary = true });
        await h.Db.SaveChangesAsync();

        var added = await h.Configuration().AddDomainAsync(h.Actor, app.Id, "second.example.org", ssl: true, default);

        added.Refusals.Should().ContainSingle().Which.MessageFa.Should().Contain("دامنه");
        (await h.Db.Domains.CountAsync(d => d.AppId == app.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Removing_the_primary_domain_takes_its_routes_with_it_and_promotes_the_oldest_survivor()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        var primary = new DomainName { AppId = app.Id, Host = "shop.mycompany.com", IsPrimary = true, CreatedAt = AppServiceHarness.Now };
        var older = new DomainName { AppId = app.Id, Host = "old.mycompany.com", CreatedAt = AppServiceHarness.Now.AddDays(-2) };
        var newer = new DomainName { AppId = app.Id, Host = "new.mycompany.com", CreatedAt = AppServiceHarness.Now.AddDays(-1) };
        h.Db.Domains.AddRange(primary, older, newer);
        h.Db.Routes.Add(new Route { WorkspaceId = h.WorkspaceId, AppId = app.Id, Host = "shop.mycompany.com", TargetService = "shop" });
        await h.Db.SaveChangesAsync();

        var outcome = await h.Configuration().RemoveDomainAsync(h.Actor, app.Id, AppItemRef.ByName("SHOP.mycompany.com"), default);

        outcome.Value.Should().Be(new AppDomainRemoved("shop.mycompany.com", "old.mycompany.com"));
        var left = await h.Db.Domains.AsNoTracking().Where(d => d.AppId == app.Id).ToListAsync();
        left.Select(d => d.Host).Should().BeEquivalentTo("old.mycompany.com", "new.mycompany.com");
        left.Single(d => d.IsPrimary).Host.Should().Be("old.mycompany.com");
        (await h.Db.Routes.AnyAsync(r => r.Host == "shop.mycompany.com")).Should().BeFalse();
        h.Audit.Entries.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Action = "app.domain_removed", TargetId = "shop:shop.mycompany.com", WorkspaceId = (Guid?)h.WorkspaceId
        });
    }

    [Fact]
    public async Task Removing_a_domain_the_app_does_not_have_is_refused_and_validate_agrees()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        var before = h.Snapshot();

        var validated = await h.Configuration().ValidateRemoveDomainAsync(h.Actor, app.Id, AppItemRef.ById(Guid.CreateVersion7()), default);
        var removed = await h.Configuration().RemoveDomainAsync(h.Actor, app.Id, AppItemRef.ByName("nope.example.org"), default);

        validated.Should().Equal(new ServiceRefusal("That domain is not attached to this app.", "این دامنه به این اپ وصل نیست.", "Host"));
        removed.Refusals.Should().Equal(validated);
        h.Snapshot().Should().Be(before);
    }

    // ---- volumes ----

    [Fact]
    public async Task Adding_a_volume_stores_the_normalised_path_under_the_apps_volume_name_and_audits_it()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);

        var outcome = await h.Configuration().AddVolumeAsync(h.Actor, app.Id, "/data/", default);

        outcome.Value!.MountPath.Should().Be("/data");
        outcome.Value.VolumeName.Should().Be(MountPath.VolumeNameFor("shop", "/data"));
        (await h.Db.Volumes.SingleAsync(v => v.AppId == app.Id)).MountPath.Should().Be("/data");
        h.Audit.Entries.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Action = "app.volume_added", TargetId = "shop:/data", IpAddress = "203.0.113.9", WorkspaceId = (Guid?)h.WorkspaceId
        });
    }

    [Fact]
    public async Task A_relative_or_duplicate_mount_path_is_refused_with_the_pages_own_wording_and_nothing_is_written()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        h.Db.Volumes.Add(new Volume { AppId = app.Id, Name = "shop-data", MountPath = "/data" });
        await h.Db.SaveChangesAsync();
        var before = h.Snapshot();

        var relative = await h.Configuration().AddVolumeAsync(h.Actor, app.Id, "relative-path", default);
        var duplicate = await h.Configuration().ValidateAddVolumeAsync(h.Actor, app.Id, "/data/", default);

        relative.Refusals.Should().ContainSingle().Which.Message.Should().StartWith("The path must start with /");
        relative.Refusals.Single().MessageFa.Should().StartWith("مسیر باید با / شروع شود");
        duplicate.Should().Equal(new ServiceRefusal("/data is already mounted.", "«/data» از قبل هست.", "MountPath"));
        h.Snapshot().Should().Be(before);
        h.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task The_plans_volume_ceiling_is_refused_by_validate_and_add()
    {
        using var h = new AppServiceHarness();
        h.Plan.MaxVolumes = 1;
        var app = await SeedAppAsync(h);
        h.Db.Volumes.Add(new Volume { AppId = app.Id, Name = "shop-data", MountPath = "/data" });
        await h.Db.SaveChangesAsync();

        var validated = await h.Configuration().ValidateAddVolumeAsync(h.Actor, app.Id, "/uploads", default);
        var added = await h.Configuration().AddVolumeAsync(h.Actor, app.Id, "/uploads", default);

        validated.Should().ContainSingle().Which.MessageFa.Should().Contain("والیوم");
        added.Refusals.Should().Equal(validated);
        (await h.Db.Volumes.CountAsync(v => v.AppId == app.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_the_data_of_a_protected_volume_is_refused_before_anything_is_touched()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        var volume = new Volume { AppId = app.Id, Name = "shop-data", MountPath = "/data", Protected = true };
        h.Db.Volumes.Add(volume);
        await h.Db.SaveChangesAsync();
        var before = h.Snapshot();

        var validated = await h.Configuration().ValidateRemoveVolumeAsync(h.Actor, app.Id, AppItemRef.ById(volume.Id), deleteData: true, default);
        var removed = await h.Configuration().RemoveVolumeAsync(h.Actor, app.Id, AppItemRef.ById(volume.Id), deleteData: true, default);

        validated.Should().ContainSingle().Which.Message.Should().Contain("\"/data\"").And.Contain("Protected");
        removed.Refusals.Should().Equal(validated);
        h.Docker.OperationsOn("shop-data").Should().BeEmpty();
        h.Snapshot().Should().Be(before);
        h.Audit.Entries.Should().BeEmpty();

        // Protected is about destroying data, not about detaching: the same volume still unmounts.
        (await h.Configuration().ValidateRemoveVolumeAsync(h.Actor, app.Id, AppItemRef.ByName("/data"), deleteData: false, default))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_volumes_data_reports_and_audits_what_docker_actually_did()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        var gone = new Volume { AppId = app.Id, Name = "shop-data", MountPath = "/data" };
        var stuck = new Volume { AppId = app.Id, Name = "shop-uploads", MountPath = "/uploads" };
        h.Db.Volumes.AddRange(gone, stuck);
        await h.Db.SaveChangesAsync();
        h.Docker.UnremovableVolumes.Add("shop-uploads");

        var deleted = await h.Configuration().RemoveVolumeAsync(h.Actor, app.Id, AppItemRef.ById(gone.Id), deleteData: true, default);
        var failed = await h.Configuration().RemoveVolumeAsync(h.Actor, app.Id, AppItemRef.ByName("/uploads/"), deleteData: true, default);

        deleted.Value.Should().Be(new AppVolumeRemoved("/data", "shop-data", DataDeletionRequested: true, DataDeleted: true));
        failed.Value.Should().Be(new AppVolumeRemoved("/uploads", "shop-uploads", DataDeletionRequested: true, DataDeleted: false));
        (await h.Db.Volumes.CountAsync(v => v.AppId == app.Id)).Should().Be(0, "both are unmounted either way");
        h.Audit.Entries.Select(e => (e.Action, e.TargetId)).Should().Equal(
            ("app.volume_deleted", "shop:/data"), ("app.volume_detached", "shop:/uploads"));
    }

    // ---- every check runs again inside the creation lock ----

    [Fact]
    public async Task A_volume_another_request_adds_while_this_one_waits_for_the_lock_is_seen_and_refused()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        (await h.Configuration().ValidateAddVolumeAsync(h.Actor, app.Id, "/data", default)).Should().BeEmpty();

        // There is no unique index on (AppId, MountPath): only a duplicate check made inside the lock
        // can see a row another request committed while this one waited for it.
        h.OnCreationLock = other => other.Volumes.Add(new Volume { AppId = app.Id, Name = "shop-data", MountPath = "/data" });
        var added = await h.Configuration().AddVolumeAsync(h.Actor, app.Id, "/data", default);

        added.Refusals.Should().Equal(new ServiceRefusal("/data is already mounted.", "«/data» از قبل هست.", "MountPath"));
        (await h.Db.Volumes.AsNoTracking().CountAsync(v => v.AppId == app.Id)).Should().Be(1);
        h.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_another_request_claims_while_this_one_waits_for_the_lock_is_refused_as_in_use()
    {
        using var h = new AppServiceHarness();
        var app = await SeedAppAsync(h);
        var other = await SeedAppAsync(h, "other");
        (await h.Configuration().ValidateAddDomainAsync(h.Actor, app.Id, "shop.mycompany.com", default)).Should().BeEmpty();

        h.OnCreationLock = db => db.Domains.Add(new DomainName { AppId = other.Id, Host = "shop.mycompany.com", IsPrimary = true });
        var added = await h.Configuration().AddDomainAsync(h.Actor, app.Id, "shop.mycompany.com", ssl: true, default);

        added.Refusals.Should().Equal(new ServiceRefusal("This domain is already in use.", "این دامنه پیش‌تر استفاده شده است.", "Host"));
        (await h.Db.Domains.AsNoTracking().CountAsync(d => d.Host == "shop.mycompany.com")).Should().Be(1);
    }

    // ---- access, checked by the service itself ----

    [Fact]
    public async Task Another_workspaces_app_reads_as_unreachable_to_every_method()
    {
        using var h = new AppServiceHarness();
        var stranger = new App
        {
            WorkspaceId = Guid.CreateVersion7(), Name = "theirs", Slug = "theirs", SourceType = AppSourceType.PrebuiltImage
        };
        h.Db.Apps.Add(stranger);
        await h.Db.SaveChangesAsync();
        var service = h.Configuration();
        var unreachable = new ServiceRefusal(
            "That app does not exist, or you are not allowed to change it.", "این اپ وجود ندارد یا اجازهٔ تغییر آن را ندارید.");

        (await service.SetEnvAsync(h.Actor, stranger.Id, [new AppEnvVarInput("A", "1")], default)).Refusals.Should().Equal(unreachable);
        (await service.DeleteEnvAsync(h.Actor, stranger.Id, AppItemRef.ByName("A"), default)).Refusals.Should().Equal(unreachable);
        (await service.AddDomainAsync(h.Actor, stranger.Id, "x.example.org", true, default)).Refusals.Should().Equal(unreachable);
        (await service.RemoveDomainAsync(h.Actor, stranger.Id, AppItemRef.ByName("x.example.org"), default)).Refusals.Should().Equal(unreachable);
        (await service.AddVolumeAsync(h.Actor, stranger.Id, "/data", default)).Refusals.Should().Equal(unreachable);
        (await service.RemoveVolumeAsync(h.Actor, stranger.Id, AppItemRef.ByName("/data"), false, default)).Refusals.Should().Equal(unreachable);
        (await service.ValidateSetEnvAsync(h.Actor, stranger.Id, [new AppEnvVarInput("A", "1")], default)).Should().Equal(unreachable);
        (await service.ValidateAddVolumeAsync(h.Actor, stranger.Id, "/data", default)).Should().Equal(unreachable);
    }

    [Fact]
    public async Task A_viewer_or_an_ungranted_scoped_member_cannot_change_an_app_but_a_granted_member_can()
    {
        foreach (var (role, scoped, grant, allowed) in new[]
                 {
                     (WorkspaceRole.Viewer, false, false, false),
                     (WorkspaceRole.Member, true, false, false),
                     (WorkspaceRole.Member, true, true, true)
                 })
        {
            using var h = new AppServiceHarness(role, scoped);
            if (grant) h.Grant(SystemRole.Member);
            var app = await SeedAppAsync(h);

            var outcome = await h.Configuration().SetEnvAsync(h.Actor, app.Id, [new AppEnvVarInput("A", "1")], default);
            var domain = await h.Configuration().ValidateAddDomainAsync(h.Actor, app.Id, "ok.example.org", default);

            outcome.Ok.Should().Be(allowed, $"{role} scoped={scoped} grant={grant}");
            domain.Should().HaveCount(allowed ? 0 : 1);
            (await h.Db.EnvironmentVariables.AnyAsync(e => e.AppId == app.Id)).Should().Be(allowed);
        }
    }
}
