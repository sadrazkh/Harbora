using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
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

        await platform.Engine.ApplyAllAsync(platform.TenantA, default);

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

        await platform.Engine.ApplyAllAsync(platform.TenantA, default);

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

        await scoped.ApplyAllAsync(platform.TenantA, default);

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

        var result = await sessionless.ApplyAllAsync(null, default);

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
            .Select(_ => Task.Run(() => engine.ApplyAllAsync(null, default))));

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
            .Select(_ => Task.Run(() => engine.ApplyAllAsync(null, default))));

        var config = File.ReadAllText(platform.Target);
        config.Should().StartWith("# Managed by Harbora");
        config.Should().Contain("Host(`acme.example.com`)").And.Contain("Host(`globex.example.com`)");
        platform.LeftoverRenders().Should().BeEmpty(
            "a temp file per attempt is only safe if each attempt takes its own away again");
    }

    [Fact]
    public async Task A_slower_attempts_own_failure_does_not_erase_a_faster_attempts_success()
    {
        // RendezvousCatalog above proves two applies are never both inside the read. It cannot prove
        // the gate also spans the write: the read sits inside the apply gate whether or not the gate
        // covers the write too, so a mutation that narrows the gate to "wait; read; release; write"
        // is invisible to it (both concurrency tests above stay green under that mutation — each
        // attempt still has its own tmp file, and File.Move is atomic, so the survivor is always a
        // complete render).
        //
        // This test watches the write instead, by forcing an order rather than counting arrivals:
        // attempt A is held — deterministically, not by timing — until attempt B has completed its
        // own apply. Gated correctly, B cannot even acquire the semaphore while A holds it, so B
        // cannot finish during A's wait at all; A's wait always exhausts, A fails alone having seen
        // nothing of B, and B applies cleanly afterwards. Narrowed to the read alone, B runs to
        // completion while A waits, and when A's own write is then forced to fail, A's rollback
        // restores whatever backup happens to be on disk at that moment — B's — silently undoing a
        // config B already reported as applied.
        //
        // A ".bak" only exists once there is something to back up, so the race needs a config
        // already live before it starts (an apply against an empty target never takes a backup at
        // all, which would make this pass for the wrong reason). The seed apply runs before the hook
        // is attached, so it is not itself part of the race.
        using var platform = new Platform();
        platform.Route(platform.TenantA, "predecessor.example.com");
        await platform.Engine.ApplyAllAsync(platform.TenantA, default);

        var arrivedFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimed = 0;
        using var ctsA = new CancellationTokenSource();

        var engine = platform.Engine;
        engine.TestOnlyBeforeWrite = async () =>
        {
            if (Interlocked.Exchange(ref claimed, 1) != 0) return; // attempt B: run straight through

            arrivedFirst.TrySetResult();
            // Bounded, not decisive: gated correctly, B cannot reach "finished" during this window
            // at all, so the wait always runs out and A proceeds having observed nothing of B. The
            // assertions below turn on that structural fact, not on how long this wait lasts.
            await Task.WhenAny(bFinished.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
            await ctsA.CancelAsync(); // force this attempt's own write to fail, deterministically
        };

        var taskA = engine.ApplyAllAsync(platform.TenantA, ctsA.Token);
        await arrivedFirst.Task;
        platform.Route(platform.TenantB, "bravo.example.com");
        var taskB = engine.ApplyAllAsync(platform.TenantB, default);
        _ = taskB.ContinueWith(_ => bFinished.TrySetResult());

        var resultA = await taskA;
        var resultB = await taskB;

        resultA.Success.Should().BeFalse("its own write was cancelled after being held");
        resultB.Success.Should().BeTrue("workspace B was told its apply succeeded");
        File.ReadAllText(platform.Target).Should().Contain("bravo.example.com",
            "a slower attempt's own failure must not silently erase a faster attempt's " +
            "already-reported success");
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

    // ---- a validation failure must not leak another tenant's routes ----
    //
    // Validating platform-wide (above) means a route that fails validation can belong to a
    // workspace that has nothing to do with whoever triggered the apply. Before this, the engine
    // joined every error — including the offending host — into ProxyApplyResult.Error, which every
    // caller hands straight back to the caller (RoutesController's JSON, AppsController's TempData,
    // AdminerService's refusal message) or into the deployment log (ProxyDiagnosis). These tests hold
    // the fix: a caller can still act on their own invalid route, and cannot learn anything about
    // anyone else's.

    [Fact]
    public async Task A_callers_own_invalid_route_is_named_so_they_can_fix_it()
    {
        using var platform = new Platform();
        platform.InvalidRoute(platform.TenantA, "broken.example.com");

        var result = await platform.Engine.ApplyAllAsync(platform.TenantA, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("broken.example.com",
            "workspace A owns this route and has to be able to act on it");
    }

    [Fact]
    public async Task Another_tenants_invalid_route_neither_fails_nor_is_named_in_the_callers_apply()
    {
        using var platform = new Platform();
        platform.Route(platform.TenantA, "fine.example.com");
        platform.InvalidRoute(platform.TenantB, "broken-elsewhere.example.com");

        var result = await platform.Engine.ApplyAllAsync(platform.TenantA, default);

        result.Success.Should().BeTrue(
            "workspace A did not create this route and cannot fix it, and every apply is somebody's " +
            "deployment — refusing A's would make one tenant's bad row the whole platform's outage");
        result.Error.Should().BeNull(
            "naming it would tell A that workspace B exists, has a route, and that the route is " +
            "misconfigured; and there is nothing here for A to act on anyway");
        File.ReadAllText(platform.Target).Should()
            .Contain("fine.example.com").And.NotContain("Host(`broken-elsewhere.example.com`)");
    }

    [Fact]
    public async Task A_sessionless_caller_is_never_failed_by_a_route_it_does_not_own()
    {
        // A webhook, the Adminer sweeper: no workspace, so no row on the platform is theirs. There
        // is no answer they could act on, which is exactly why they must not be handed a failure.
        using var platform = new Platform();
        platform.InvalidRoute(platform.TenantA, "broken.example.com");

        var result = await platform.Engine.ApplyAllAsync(null, default);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull("a caller with no workspace of its own owns nothing on the platform");
    }

    [Fact]
    public async Task A_validation_failure_still_logs_every_hosts_own_detail_for_an_operator()
    {
        using var platform = new Platform();
        var logs = new RecordingLogger<TraefikProxyEngine>();
        var engine = new TraefikProxyEngine(
            Options.Create(new TraefikOptions { DynamicConfigPath = platform.Target }),
            new AesGcmSecretProtector(TestKey), platform.Catalog, logs);
        platform.InvalidRoute(platform.TenantB, "only-in-the-log.example.com");

        await engine.ApplyAllAsync(platform.TenantA, default);

        logs.Messages.Should().Contain(m => m.Contains("only-in-the-log.example.com"),
            "the detail a caller cannot be shown still has to reach an operator somewhere");
    }

    // ---- one tenant's invalid route must not stop another tenant deploying ----
    //
    // Validating platform-wide (above) and failing a deployment on a refused apply (Task 2) meet
    // here. A route saved while DISABLED was never validated — the designer's save gate only looked
    // at enabled rows — and the deployment that owns its host switches it on without re-checking the
    // fields it does not write. One such row anywhere then refused every apply on the install, and
    // every apply is somebody's deployment. These tests hold the boundary: a route that would not
    // have served is left out of the render, and only the workspace that owns it is told its apply
    // did not do everything it asked for.

    [Fact]
    public async Task A_deployment_is_not_failed_by_another_tenants_invalid_route()
    {
        // The reachable path, all through the shipped UI: workspace B saves a route with the Enabled
        // box cleared and a redirect with no target — nothing validates a disabled row — and B's own
        // deployment for that host turns it on, setting the upstream and IsEnabled and leaving Type
        // and RedirectTo exactly as they were. That row is what this test puts on the platform.
        using var h = new PipelineHarness().WithDomain();
        using var config = new TempTarget();
        h.Db.Routes.Add(new Route
        {
            WorkspaceId = Guid.NewGuid(), Host = "promoted.example.com",
            Type = RouteType.Redirect, RedirectTo = "",
            TargetService = "harbora-other", TargetPort = 8080, IsEnabled = true
        });
        h.Db.SaveChanges();
        h.ProxyOverride = RealEngine(h, config.Target);

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Succeeded,
            "a stranger's misconfigured row is not this deployment's to fix, and refusing every " +
            "apply on the install until an operator finds it is the outage this phase exists to end");
        File.ReadAllText(config.Target).Should().Contain("blog.example.com",
            "the deployment that succeeded has to be the one that is routed");
    }

    [Fact]
    public async Task The_invalid_route_is_left_out_of_the_config_rather_than_published()
    {
        using var h = new PipelineHarness().WithDomain();
        using var config = new TempTarget();
        h.Db.Routes.Add(new Route
        {
            WorkspaceId = Guid.NewGuid(), Host = "promoted.example.com",
            Type = RouteType.Redirect, RedirectTo = "",
            TargetService = "harbora-other", TargetPort = 8080, IsEnabled = true
        });
        h.Db.SaveChanges();
        h.ProxyOverride = RealEngine(h, config.Target);

        await h.RunAsync(h.QueueDeployment(number: 1));

        File.ReadAllText(config.Target).Should().NotContain("Host(`promoted.example.com`)",
            "a route that fails validation was never going to serve; rendering it anyway would hand " +
            "Traefik the document it would have rejected, which is the whole file");
    }

    [Fact]
    public async Task A_deployment_whose_own_route_is_invalid_still_fails_and_puts_the_row_back()
    {
        // The other side of the boundary, and why dropping is not the same as silence: the workspace
        // that owns the row is told, in the only way a deployment can be told — it failed — and the
        // row goes back to the disabled state the deployment found it in.
        using var h = new PipelineHarness().WithDomain();
        using var config = new TempTarget();
        h.Db.Routes.Add(new Route
        {
            WorkspaceId = h.Workspace.Id, AppId = h.App.Id, Host = "blog.example.com",
            Type = RouteType.Redirect, RedirectTo = "", IsEnabled = false
        });
        h.Db.SaveChanges();
        h.ProxyOverride = RealEngine(h, config.Target);

        var result = await h.RunAsync(h.QueueDeployment(number: 1));

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("blog.example.com",
            "this workspace owns the row and is the one that can fix it");
        var route = await h.Db.Routes.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        route.IsEnabled.Should().BeFalse("the deployment that switched it on did not survive");
    }

    [Fact]
    public async Task A_dropped_route_is_named_in_the_file_an_operator_reads()
    {
        // The log is where the detail goes, and the log is not the first place anyone looks when a
        // domain stops answering — the config file Traefik is serving from is. So the render says
        // what it left out and why, in comments Traefik ignores and an operator cannot miss.
        using var platform = new Platform();
        platform.Route(platform.TenantA, "live.example.com");
        platform.InvalidRoute(platform.TenantB, "dropped.example.com");

        await platform.Engine.ApplyAllAsync(platform.TenantA, default);

        var config = File.ReadAllText(platform.Target);
        config.Should().Contain("# ").And.Contain("dropped.example.com",
            "the row that is missing from the routers has to be accounted for in the same file");
        config.Should().Contain("Host(`live.example.com`)");
    }

    // ---- helpers ----

    /// <summary>The real engine, reading the harness's own rows the way <c>RouteCatalog</c> does.</summary>
    private static TraefikProxyEngine RealEngine(PipelineHarness h, string target) => new(
        Options.Create(new TraefikOptions { DynamicConfigPath = target }),
        new AesGcmSecretProtector(TestKey),
        new HarnessCatalog(h.Db),
        NullLogger<TraefikProxyEngine>.Instance);

    /// <summary>
    /// Reads every enabled route on the platform out of the harness's context — unfiltered and
    /// untracked, exactly as <see cref="RouteCatalog"/> does from its own scope. A scope factory is
    /// not worth building here: what these tests watch is the engine's decision, and the catalog's
    /// own tenancy behaviour is held by the tests above.
    /// </summary>
    private sealed class HarnessCatalog(HarboraDbContext db) : IRouteCatalog
    {
        public async Task<IReadOnlyList<Route>> AllEnabledAsync(CancellationToken ct) =>
            await db.Routes.IgnoreQueryFilters().Where(r => r.IsEnabled)
                .OrderBy(r => r.Id).AsNoTracking().ToListAsync(ct);
    }

    /// <summary>A throwaway dynamic-config path for a test that drives a real deployment.</summary>
    private sealed class TempTarget : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "harbora-platform-deploy", Guid.NewGuid().ToString("N"));

        public string Target => Path.Combine(_root, "dynamic", "harbora.yml");

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* temp dir — best effort */ }
        }
    }

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

        /// <summary>
        /// A route that fails validation (port out of range), tied to a tenant, so a test can prove
        /// what a caller who does — or does not — own it is told about the failure.
        /// </summary>
        public Route InvalidRoute(Guid workspaceId, string host)
        {
            var route = new Route
            {
                WorkspaceId = workspaceId, Host = host, TargetService = "harbora-" + host.Split('.')[0],
                TargetPort = 99999, IsEnabled = true
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

    /// <summary>Captures every formatted log line, so a test can assert on what an operator would see.</summary>
    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
