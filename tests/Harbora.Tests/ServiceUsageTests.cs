using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which apps are using a database.
///
/// Attaching one writes its host into the app's environment, and those variables are stored
/// encrypted because they carry the password. The architecture view searched the *stored* values for
/// the container name — that is, searched ciphertext — so an app with a database attached was drawn
/// with no connections at all. The delete flow never asked the question in the first place.
/// </summary>
public class ServiceUsageTests : IDisposable
{
    private readonly HarboraDbContext _db;

    /// <summary>
    /// The real protector, not a passthrough. A fake whose "ciphertext" still contains the plain
    /// text would let every test here pass without anything ever being decrypted — which is the one
    /// thing this class exists to get right.
    /// </summary>
    private readonly ISecretProtector _protector = new Harbora.Infrastructure.Security.AesGcmSecretProtector(
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
    private readonly Guid _workspaceId = Guid.NewGuid();

    public ServiceUsageTests()
    {
        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("usage-" + Guid.NewGuid()).Options);
    }

    public void Dispose() => _db.Dispose();

    private ServiceUsageService Service() => new(_db, _protector);

    private ManagedService GivenDatabase(string containerName = "harbora-svc-shop-db")
    {
        var svc = new ManagedService
        {
            WorkspaceId = _workspaceId,
            Name = "shop-db",
            Type = ManagedServiceType.PostgreSql,
            ContainerName = containerName,
            VolumeName = "harbora-vol-shop-db"
        };
        _db.ManagedServices.Add(svc);
        _db.SaveChanges();
        return svc;
    }

    private App GivenApp(string slug, params (string Key, string Value, bool Secret)[] env)
    {
        var app = new App { WorkspaceId = _workspaceId, Name = slug, Slug = slug };
        foreach (var (key, value, secret) in env)
            app.EnvironmentVariables.Add(new EnvironmentVariable
            {
                Key = key,
                Value = secret ? _protector.Protect(value) : value,
                IsSecret = secret
            });
        _db.Apps.Add(app);
        _db.SaveChanges();
        return app;
    }

    [Fact]
    public async Task An_attached_database_is_found_even_though_the_variable_is_encrypted()
    {
        // The bug in one test: attach always stores secrets, so comparing stored values against the
        // host name compared ciphertext and found nothing.
        var svc = GivenDatabase();
        GivenApp("shop", ("DATABASE_URL", "postgresql://u:p@harbora-svc-shop-db:5432/shop", true));

        var users = await Service().AppsUsingAsync(svc.Id, default);

        users.Should().ContainSingle().Which.Slug.Should().Be("shop");
    }

    [Fact]
    public async Task An_app_that_does_not_use_it_is_not_listed()
    {
        var svc = GivenDatabase();
        GivenApp("blog", ("DATABASE_URL", "postgresql://u:p@some-other-db:5432/blog", true));

        (await Service().AppsUsingAsync(svc.Id, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_plain_variable_counts_too()
    {
        // Someone can point an app at a database by hand, without ever pressing Attach.
        var svc = GivenDatabase();
        GivenApp("worker", ("PGHOST", "harbora-svc-shop-db", false));

        (await Service().AppsUsingAsync(svc.Id, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Another_workspaces_app_is_never_counted()
    {
        var svc = GivenDatabase();
        var stranger = new App { WorkspaceId = Guid.NewGuid(), Name = "theirs", Slug = "theirs" };
        stranger.EnvironmentVariables.Add(new EnvironmentVariable
        {
            Key = "PGHOST", Value = "harbora-svc-shop-db", IsSecret = false
        });
        _db.Apps.Add(stranger);
        await _db.SaveChangesAsync();

        (await Service().AppsUsingAsync(svc.Id, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_variable_flagged_secret_but_stored_in_plain_text_is_still_read()
    {
        // The two mistakes here are not equal. Missing a user is what lets a database be deleted
        // out from under a running app; a spurious one only makes a warning too cautious. So a value
        // that will not decrypt is still read rather than dropped.
        var svc = GivenDatabase();
        var app = new App { WorkspaceId = _workspaceId, Name = "shop", Slug = "shop" };
        app.EnvironmentVariables.Add(new EnvironmentVariable
        {
            Key = "DATABASE_URL",
            Value = "postgresql://u:p@harbora-svc-shop-db:5432/shop",   // never encrypted, but flagged
            IsSecret = true
        });
        _db.Apps.Add(app);
        await _db.SaveChangesAsync();

        var real = new Harbora.Infrastructure.Security.AesGcmSecretProtector(
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

        var users = await new ServiceUsageService(_db, real).AppsUsingAsync(svc.Id, default);

        users.Should().ContainSingle().Which.Slug.Should().Be("shop");
    }

    [Fact]
    public void Connections_for_a_whole_page_are_answered_in_one_pass()
    {
        // What the architecture view needs: every app's databases, without a query per row.
        var shop = GivenApp("shop", ("DATABASE_URL", "postgres://u:p@harbora-svc-shop-db:5432/s", true));
        var idle = GivenApp("idle", ("PORT", "8080", false));

        var map = Service().ConnectionsFor([shop, idle], ["harbora-svc-shop-db", "harbora-svc-cache"]);

        map[shop.Id].Should().BeEquivalentTo(["harbora-svc-shop-db"]);
        map[idle.Id].Should().BeEmpty();
    }

    [Theory]
    [InlineData("postgresql://u:p@HARBORA-SVC-SHOP-DB:5432/x", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_host_is_matched_without_regard_to_case(string? value, bool expected)
    {
        // Hostnames are case-insensitive, and a connection string copied from elsewhere may not match
        // the case Harbora generated.
        ServiceUsage.Mentions(value, "harbora-svc-shop-db").Should().Be(expected);
    }

    [Fact]
    public void A_service_with_no_container_name_matches_nothing()
    {
        // Otherwise an empty name is "contained" in every value and every app looks connected.
        ServiceUsage.Mentions("postgres://u:p@somewhere:5432/x", "").Should().BeFalse();
        ServiceUsage.Mentions("postgres://u:p@somewhere:5432/x", null).Should().BeFalse();
    }
}
