using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Proxy;
using Harbora.Infrastructure.Security;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The dynamic-config file is one file for the whole install, and Traefik reloads whatever it finds
/// there. Rendering it from one workspace's routes therefore does not "update a tenant" — it
/// withdraws every other tenant's routing, immediately, for as long as nobody else re-applies. Then
/// somebody else re-applies and the first tenant goes dark instead.
///
/// <para>
/// Every caller used to hand its own workspace's routes in, and each of them looked correct where it
/// was written. These tests hold the property the file actually has: an apply is a statement about
/// the whole platform, and nobody gets to make it about less.
/// </para>
/// </summary>
public class PlatformProxyConfigTests
{
    private const string TestKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    // ---- the outage ----

    [Fact]
    public async Task Applying_after_a_change_in_one_workspace_still_renders_the_other_workspaces_route()
    {
        using var platform = new Platform();
        platform.Route(platform.TenantA, "acme.example.com");
        platform.Route(platform.TenantB, "globex.example.com");

        await platform.Engine.ApplyAllAsync(default);

        var config = File.ReadAllText(platform.Target);
        config.Should().Contain("Host(`acme.example.com`)").And.Contain("Host(`globex.example.com`)",
            "both tenants are served by this one file, so both have to be in it");
    }

    [Fact]
    public async Task A_deployment_in_one_workspace_does_not_withdraw_another_workspaces_route()
    {
        // Driven through the real pipeline, because that is where the outage started: a deploy in
        // any workspace re-published the platform's routing from that workspace's rows alone.
        using var h = new PipelineHarness().WithDomain();
        h.Db.Routes.Add(new Route
        {
            WorkspaceId = Guid.NewGuid(), Host = "other-tenant.example.com",
            TargetService = "harbora-other", TargetPort = 8080, IsEnabled = true
        });
        h.Db.SaveChanges();

        await h.RunAsync(h.QueueDeployment(number: 1));

        h.Proxy.Applications.Select(a => a.Host).Should().Contain("other-tenant.example.com",
            "the config this deployment publishes replaces the whole platform's routing, so a route " +
            "missing from it is a tenant that stops being served");
    }

    [Fact]
    public async Task A_disabled_route_is_still_left_out()
    {
        // Platform-wide is not "everything": a route somebody switched off is off everywhere.
        using var platform = new Platform();
        platform.Route(platform.TenantA, "live.example.com");
        platform.Route(platform.TenantB, "paused.example.com", enabled: false);

        await platform.Engine.ApplyAllAsync(default);

        var config = File.ReadAllText(platform.Target);
        config.Should().Contain("live.example.com").And.NotContain("paused.example.com");
    }

    // ---- the tenant filter, which is what would make the read empty ----

    [Fact]
    public async Task A_request_that_belongs_to_one_tenant_still_publishes_every_tenants_routing()
    {
        // The apply happens on the request thread of whoever pressed the button, and the query
        // filter follows that thread into the engine's own scope. Filtered, this renders one
        // workspace — which is the bug, arriving by a second route.
        using var platform = new Platform();
        var scoped = platform.AsTenant(platform.TenantA);
        platform.Route(platform.TenantA, "acme.example.com");
        platform.Route(platform.TenantB, "globex.example.com");

        await scoped.ApplyAllAsync(default);

        File.ReadAllText(platform.Target).Should().Contain("globex.example.com",
            "the tenant whose request this is has no bearing on what the platform routes");
    }

    [Fact]
    public async Task A_caller_with_no_workspace_at_all_publishes_every_route_rather_than_none()
    {
        // A webhook and the Adminer sweeper both apply without a workspace claim. Under the filter
        // that resolves to Guid.Empty, which matches no tenant — so the read comes back empty and
        // the render is an empty config. Not one tenant down: all of them, from a sweep.
        using var platform = new Platform();
        var sessionless = platform.AsTenant(Guid.Empty);
        platform.Route(platform.TenantA, "acme.example.com");
        platform.Route(platform.TenantB, "globex.example.com");

        var result = await sessionless.ApplyAllAsync(default);

        result.Success.Should().BeTrue();
        var config = File.ReadAllText(platform.Target);
        config.Should().Contain("acme.example.com").And.Contain("globex.example.com");
    }

    // ---- two writers, one file ----

    [Fact]
    public async Task Applies_that_race_each_other_never_overlap()
    {
        // Deterministic in both directions, and not by timing. Each apply announces itself from
        // inside the engine's critical section and waits for the others to arrive: if they were
        // allowed in together they would all arrive, all be counted, and MostAtOnce would be five.
        // Serialised, the first waits alone, the wait times out, and the count can never exceed one
        // however the thread pool happens to schedule them — the delay bounds the test's runtime
        // rather than deciding its verdict.
        using var platform = new Platform();
        platform.Route(platform.TenantA, "acme.example.com");
        platform.Route(platform.TenantB, "globex.example.com");
        var watcher = new RendezvousCatalog(platform.Catalog, expected: 5);
        var engine = platform.EngineOver(watcher);

        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => engine.ApplyAllAsync(default))));

        watcher.MostAtOnce.Should().Be(1, "one file has room for one writer");
        results.Should().OnlyContain(r => r.Success, "every caller is owed an answer, not a corrupted file");
    }

    [Fact]
    public async Task A_file_that_five_applies_raced_for_is_a_complete_render()
    {
        using var platform = new Platform();
        platform.Route(platform.TenantA, "acme.example.com");
        platform.Route(platform.TenantB, "globex.example.com");
        var engine = platform.EngineOver(new RendezvousCatalog(platform.Catalog, expected: 5));

        await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => engine.ApplyAllAsync(default))));

        var config = File.ReadAllText(platform.Target);
        config.Should().StartWith("# Managed by Harbora");
        config.Should().Contain("Host(`acme.example.com`)").And.Contain("Host(`globex.example.com`)");
        platform.LeftoverRenders().Should().BeEmpty(
            "a temp file per attempt is only safe if each attempt takes its own away again");
    }

    // ---- what platform-wide rendering makes reachable for the first time ----

    [Fact]
    public void Two_workspaces_claiming_one_host_render_as_two_routers_rather_than_one_broken_one()
    {
        // Duplicate host+path across tenants was unreachable while each workspace got its own file,
        // and it is only a validation warning. Rendered together the two routers still have distinct
        // names — they are derived from the route id — so the document stays well-formed and Traefik
        // decides between them by priority. Recorded here rather than changed: which of two tenants
        // owns a hostname is a question for domain verification, not for the renderer.
        using var platform = new Platform();
        var a = platform.Route(platform.TenantA, "contested.example.com");
        var b = platform.Route(platform.TenantB, "contested.example.com");

        var engine = platform.Engine;
        var content = engine.Preview([a, b]).Content;

        content.Should().Contain("r-" + a.Id.ToString("N")[..12]);
        content.Should().Contain("r-" + b.Id.ToString("N")[..12]);
        engine.Validate([a, b]).Warnings.Should()
            .Contain(w => w.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
        engine.Validate([a, b]).IsValid.Should().BeTrue("a contested hostname is a warning, not a refusal");
    }

    // ---- helpers ----

    /// <summary>
    /// A platform: two tenants, one database, one dynamic-config file, and the real engine over the
    /// real catalog. Nothing about the tenancy is faked, because the tenancy is what broke.
    /// </summary>
    private sealed class Platform : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "harbora-platform-proxy", Guid.NewGuid().ToString("N"));
        private readonly string _dbName = "platform-proxy-" + Guid.NewGuid();
        // One store behind every context in this test, however each one was built: the arranging
        // context and the engine's own scoped one have to be looking at the same platform.
        private readonly InMemoryDatabaseRoot _store = new();
        private readonly List<ServiceProvider> _providers = [];

        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public HarboraDbContext Db { get; }
        public IRouteCatalog Catalog { get; }
        public TraefikProxyEngine Engine { get; }

        public string Target => Path.Combine(_root, "dynamic", "harbora.yml");

        public Platform()
        {
            Db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase(_dbName, _store).Options);
            Catalog = CatalogFor(scope: null);
            Engine = EngineOver(Catalog);
        }

        /// <summary>Adds a route to a tenant and hands it back, so a test can name its router.</summary>
        public Route Route(Guid workspaceId, string host, bool enabled = true)
        {
            var route = new Route
            {
                WorkspaceId = workspaceId, Host = host, TargetService = "harbora-" + host.Split('.')[0],
                TargetPort = 8080, IsEnabled = enabled
            };
            Db.Routes.Add(route);
            Db.SaveChanges();
            return route;
        }

        /// <summary>An engine whose catalog reads the database as that tenant's request thread would.</summary>
        public TraefikProxyEngine AsTenant(Guid workspaceId) =>
            EngineOver(CatalogFor(new FixedWorkspaceScope(workspaceId)));

        public TraefikProxyEngine EngineOver(IRouteCatalog catalog) => new(
            Options.Create(new TraefikOptions { DynamicConfigPath = Target }),
            new AesGcmSecretProtector(TestKey),
            catalog,
            NullLogger<TraefikProxyEngine>.Instance);

        /// <summary>Renders started and never finished — litter in the directory Traefik watches.</summary>
        public IReadOnlyList<string> LeftoverRenders()
        {
            var staging = Path.Combine(
                Path.GetDirectoryName(Target)!, TraefikProxyEngine.StagingDirectoryName);
            return Directory.Exists(staging) ? Directory.GetFiles(staging) : [];
        }

        private IRouteCatalog CatalogFor(IWorkspaceScope? scope)
        {
            var services = new ServiceCollection();
            services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(_dbName, _store));
            if (scope is not null) services.AddScoped<IWorkspaceScope>(_ => scope);
            var provider = services.BuildServiceProvider();
            _providers.Add(provider);
            return new RouteCatalog(provider.GetRequiredService<IServiceScopeFactory>());
        }

        public void Dispose()
        {
            Db.Dispose();
            foreach (var provider in _providers) provider.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* temp dir — best effort */ }
        }
    }

    /// <summary>
    /// Counts how many applies were inside the engine's critical section at once, and makes the
    /// answer independent of scheduling: every arrival waits for all of the others, so overlap — if
    /// the engine permitted any — is certain rather than likely.
    /// </summary>
    private sealed class RendezvousCatalog(IRouteCatalog inner, int expected) : IRouteCatalog
    {
        private readonly TaskCompletionSource _everyoneArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private int _inside;
        private int _arrived;

        public int MostAtOnce { get; private set; }

        public async Task<IReadOnlyList<Route>> AllEnabledAsync(CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inside);
            lock (_gate) MostAtOnce = Math.Max(MostAtOnce, now);

            if (Interlocked.Increment(ref _arrived) == expected) _everyoneArrived.TrySetResult();
            await Task.WhenAny(_everyoneArrived.Task, Task.Delay(TimeSpan.FromMilliseconds(50), ct));

            var routes = await inner.AllEnabledAsync(ct);
            Interlocked.Decrement(ref _inside);
            return routes;
        }
    }
}
