using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Networking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Giving an app its address, against a real database.
///
/// The half of the rule that cannot be pure: whether a name is already taken is a question only the
/// database can answer, and what to do when it is taken is the behaviour this project got wrong —
/// AppsController skipped the insert and said nothing, so the app was created with no address and no
/// explanation.
/// </summary>
public class AppAddressAssignerTests
{
    private static HarboraDbContext NewDb() => new(
        new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("addr-" + Guid.NewGuid()).Options);

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection([]).Build();

    private static async Task<HarboraDbContext> DbWithRootDomain(string root)
    {
        var db = NewDb();
        db.Settings.Add(new Setting { Key = SettingKeys.PlatformRootDomain, Value = root });
        await db.SaveChangesAsync();
        return db;
    }

    private static App WebApp(string slug) => new()
    {
        Id = Guid.NewGuid(), Name = slug, Slug = slug, Kind = ServiceKind.Web, WorkspaceId = Guid.NewGuid()
    };

    [Fact]
    public async Task An_app_is_given_its_address_and_the_address_is_primary()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        var app = WebApp("shop");

        var decision = await new AppAddressAssigner(db, EmptyConfig())
            .AssignAsync(app, requested: null, AppAddressRequestOrigin.Derived, suffix: null, CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.Assigned);
        decision.Host.Should().Be("shop.apps.example.com");

        var added = app.Domains.Should().ContainSingle().Subject;
        added.Host.Should().Be("shop.apps.example.com");
        added.IsPrimary.Should().BeTrue("an app's own address is the one its links point at");
        added.SslEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task A_taken_name_produces_a_working_address_and_says_it_was_taken()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        db.Domains.Add(new DomainName { AppId = Guid.NewGuid(), Host = "shop.apps.example.com" });
        await db.SaveChangesAsync();

        var app = WebApp("shop");
        var decision = await new AppAddressAssigner(db, EmptyConfig())
            .AssignAsync(app, requested: null, AppAddressRequestOrigin.Derived, suffix: () => "k3f", CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.Discriminated,
            "the outcome is what the caller shows the person — silence here is the defect this replaces");
        decision.Host.Should().Be("shop-k3f.apps.example.com");
        app.Domains.Should().ContainSingle().Which.Host.Should().Be("shop-k3f.apps.example.com");
    }

    [Fact]
    public async Task A_worker_is_given_no_address_and_no_domain_row()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        var app = WebApp("mailer");
        app.Kind = ServiceKind.Worker;

        var decision = await new AppAddressAssigner(db, EmptyConfig())
            .AssignAsync(app, requested: null, AppAddressRequestOrigin.Derived, suffix: null, CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.KindTakesNoTraffic);
        app.Domains.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_root_domain_set_nothing_is_invented()
    {
        await using var db = NewDb();
        var app = WebApp("shop");

        var decision = await new AppAddressAssigner(db, EmptyConfig())
            .AssignAsync(app, requested: null, AppAddressRequestOrigin.Derived, suffix: null, CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.NoRootDomain);
        app.Domains.Should().BeEmpty();
    }

    [Fact]
    public async Task When_every_attempt_is_taken_it_says_so_rather_than_adding_nothing_quietly()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        db.Domains.Add(new DomainName { AppId = Guid.NewGuid(), Host = "shop.apps.example.com" });
        db.Domains.Add(new DomainName { AppId = Guid.NewGuid(), Host = "shop-same.apps.example.com" });
        await db.SaveChangesAsync();

        var app = WebApp("shop");
        var decision = await new AppAddressAssigner(db, EmptyConfig())
            .AssignAsync(app, requested: null, AppAddressRequestOrigin.Derived, suffix: () => "same", CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.Exhausted);
        decision.Host.Should().BeNull();
        app.Domains.Should().BeEmpty();
    }

    /// <summary>
    /// A hostname held by another tenant is taken, because DNS is not multi-tenant.
    ///
    /// <para>
    /// Worth being honest about what this proves today. <c>DomainName</c> carries no tenant query
    /// filter — <c>HarboraDbContext</c> says so deliberately, because a domain is only ever reached
    /// through its parent and the parent is filtered. So the collision read sees every workspace's
    /// hostnames without needing to ask for that, and this test passes for that reason rather than
    /// because the assigner does anything clever.
    /// </para>
    /// <para>
    /// It is still worth keeping, and this is the part that matters: it is the test that goes red on
    /// the day somebody gives <c>DomainName</c> a filter. On that day the collision read starts
    /// missing other tenants' names silently, and two apps get routed to one hostname with nothing
    /// anywhere reporting a problem.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_name_taken_in_another_workspace_still_counts_as_taken()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        db.Domains.Add(new DomainName { AppId = Guid.NewGuid(), Host = "shop.apps.example.com" });
        await db.SaveChangesAsync();

        // A different workspace from the one the app below belongs to — WebApp() gives each app its
        // own — so this is genuinely a cross-tenant name and not the same tenant's.
        var app = WebApp("shop");
        var decision = await new AppAddressAssigner(db, EmptyConfig())
            .AssignAsync(app, requested: null, AppAddressRequestOrigin.Derived, suffix: () => "k3f", CancellationToken.None);

        decision.Host.Should().Be("shop-k3f.apps.example.com",
            "DNS is not multi-tenant — two workspaces cannot both answer on one hostname");
    }

    /// <summary>
    /// Pins the behaviour four callers are about to start depending on. This is not new behaviour —
    /// <see cref="AssignAsync"/> has always run every <paramref name="requested"/> name through the
    /// same collision check as a derived one, which is exactly what a branch preview's own
    /// <c>PreviewNaming.Host</c> needs: it brings its own name, but not its own exemption from being
    /// checked against everybody else's. A branch preview's name is derived by the platform, not typed
    /// by a person, so a collision on it is discriminated rather than refused.
    /// </summary>
    [Fact]
    public async Task A_derived_caller_supplied_name_still_goes_through_the_collision_check()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        db.Domains.Add(new DomainName { AppId = Guid.NewGuid(), Host = "shop-main.apps.example.com" });
        await db.SaveChangesAsync();

        var app = WebApp("shop");
        var decision = await new AppAddressAssigner(db, EmptyConfig()).AssignAsync(
            app, requested: "shop-main.apps.example.com", AppAddressRequestOrigin.Derived,
            suffix: () => "k3f", CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.Discriminated,
            "branch previews bring their own name and must still not be given one that is taken");
        decision.Host.Should().Be("shop-main-k3f.apps.example.com");
    }

    /// <summary>
    /// The typed half of finding 3: <c>shop.mycompany.com</c> discriminated into
    /// <c>shop-k3f.mycompany.com</c> would be "reachable" only in the sense that a request to it lands
    /// somewhere — no DNS record points at that name and the platform's wildcard certificate covers
    /// only names under the platform's own root domain, not a customer's zone. A typed name that
    /// collides is refused instead, the way <c>AddDomain</c> has always refused it.
    /// </summary>
    [Fact]
    public async Task A_typed_name_that_is_taken_is_refused_rather_than_discriminated_onto_the_customers_own_zone()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        db.Domains.Add(new DomainName { AppId = Guid.NewGuid(), Host = "shop.mycompany.com" });
        await db.SaveChangesAsync();

        var app = WebApp("shop");
        var decision = await new AppAddressAssigner(db, EmptyConfig()).AssignAsync(
            app, requested: "shop.mycompany.com", AppAddressRequestOrigin.Typed,
            suffix: () => "k3f", CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.Taken,
            "a typed name is a promise made to the person who typed it, not something to mangle onto a " +
            "zone with no DNS record for the mangled name and no wildcard certificate to cover it");
        decision.Host.Should().BeNull();
        app.Domains.Should().BeEmpty();
    }
}
