using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Proxy;
using Harbora.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Characterization tests for the Traefik dynamic-config renderer + validator. The rendered YAML is
/// the contract Traefik consumes; these tests pin it before the overhaul extends it with weighted
/// services for rollback/preview (ADR-003/006). Validation tests protect the atomic-apply gate.
/// </summary>
public class TraefikProxyEngineTests
{
    private const string TestKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    private static TraefikProxyEngine Engine() => Engine(new TraefikOptions());

    private static TraefikProxyEngine Engine(TraefikOptions options, params Route[] platformRoutes) =>
        new(Options.Create(options), new AesGcmSecretProtector(TestKey),
            new StubRouteCatalog(platformRoutes), NullLogger<TraefikProxyEngine>.Instance);

    /// <summary>The routes the platform is routing, without a database in the way.</summary>
    private sealed class StubRouteCatalog(IReadOnlyList<Route> routes) : IRouteCatalog
    {
        public Task<IReadOnlyList<Route>> AllEnabledAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Route>>(routes.Where(r => r.IsEnabled).ToList());
    }

    private static Route HostRoute(string host = "app.example.com", string svc = "harbora-app", int port = 80)
        => new() { Host = host, TargetService = svc, TargetPort = port, Type = RouteType.HostBased, IsEnabled = true };

    [Fact]
    public void Preview_renders_router_and_service_for_a_host_route()
    {
        var preview = Engine().Preview(new[] { HostRoute() });

        preview.Format.Should().Be("yaml");
        preview.Content.Should().Contain("http:");
        preview.Content.Should().Contain("routers:");
        preview.Content.Should().Contain("Host(`app.example.com`)");
        preview.Content.Should().Contain("services:");
        preview.Content.Should().Contain("http://harbora-app:80");
    }

    // ---- replicas: one loadBalancer, several servers (confirms the multi-server assumption) ----

    [Fact]
    public void A_route_with_extra_upstreams_renders_a_server_line_for_each_one()
    {
        var route = HostRoute(svc: "harbora-app-1", port: 8080);
        route.ExtraUpstreamsJson = RouteUpstreams.Serialize(
        [
            new RouteUpstreams.Upstream("harbora-app-2", 8080),
            new RouteUpstreams.Upstream("harbora-app-3", 8080)
        ]);

        var content = Engine().Preview([route]).Content;

        content.Should().Contain("http://harbora-app-1:8080");
        content.Should().Contain("http://harbora-app-2:8080");
        content.Should().Contain("http://harbora-app-3:8080");
        // One loadBalancer, three servers under it — not three separate services, which is what
        // would actually spread traffic across replicas instead of only ever hitting the first.
        content.Should().Contain("loadBalancer:");
    }

    [Fact]
    public void A_route_with_no_extra_upstreams_renders_exactly_as_it_always_has()
    {
        // The single-replica case, and the safety argument for the whole feature: a route the
        // designer created by hand, or a deploy of an app running one replica, must produce
        // byte-for-byte the same loadBalancer as before ExtraUpstreamsJson existed.
        var route = HostRoute();

        var content = Engine().Preview([route]).Content;

        content.Should().Contain("http://harbora-app:80");
        content.Should().NotContain("healthCheck:",
            "a single-server loadBalancer gets no active health check unless the app asked for more than one");
    }

    [Fact]
    public void A_replicated_routes_active_health_check_polls_the_apps_own_path()
    {
        var route = HostRoute();
        route.ExtraUpstreamsJson = RouteUpstreams.Serialize([new RouteUpstreams.Upstream("harbora-app-2", 80)]);
        route.LoadBalancerHealthCheckPath = "/healthz";

        var content = Engine().Preview([route]).Content;

        content.Should().Contain("healthCheck:");
        content.Should().Contain("path: \"/healthz\"");
    }

    [Fact]
    public void Preview_includes_cert_resolver_when_ssl_enabled()
    {
        var route = HostRoute();
        route.SslEnabled = true;
        Engine().Preview(new[] { route }).Content.Should().Contain("certResolver: letsencrypt");
    }

    [Fact]
    public void Cloudflare_mode_uses_the_forwarded_visitor_for_ip_allowlists()
    {
        var route = HostRoute();
        route.IpAllowlist = "203.0.113.0/24";

        var content = Engine(new TraefikOptions { ForwardedClientIpDepth = 1 }).Preview([route]).Content;

        content.Should().Contain("ipStrategy:").And.Contain("depth: 1");
        content.Should().Contain("203.0.113.0/24");
    }

    [Fact]
    public void Direct_mode_does_not_trust_a_forwarded_ip_for_allowlists()
    {
        var route = HostRoute();
        route.IpAllowlist = "203.0.113.0/24";

        Engine().Preview([route]).Content.Should().NotContain("ipStrategy:");
    }

    [Fact]
    public void Panel_marker_switches_certificates_and_ip_allowlists_without_a_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "harbora-cloudflare-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "cloudflare.enabled");
        File.WriteAllText(marker, "enabled");
        try
        {
            var route = HostRoute();
            route.IpAllowlist = "203.0.113.0/24";
            var content = Engine(new TraefikOptions
            {
                CertResolver = "letsencrypt",
                ForwardedClientIpDepth = 0,
                CloudflareEnabledMarkerPath = marker
            }).Preview([route]).Content;

            content.Should().Contain("certResolver: cloudflare");
            content.Should().Contain("depth: 1");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Validate_passes_for_a_well_formed_route()
    {
        var result = Engine().Validate(new[] { HostRoute() });
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_flags_missing_host()
    {
        var route = HostRoute(host: "");
        var result = Engine().Validate(new[] { route });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("host", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_flags_out_of_range_port()
    {
        var route = HostRoute(port: 70000);
        var result = Engine().Validate(new[] { route });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("port", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_flags_redirect_without_target()
    {
        var route = new Route
        {
            Host = "old.example.com", Type = RouteType.Redirect, RedirectTo = "", IsEnabled = true
        };
        var result = Engine().Validate(new[] { route });
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_warns_on_duplicate_host_and_path()
    {
        var result = Engine().Validate(new[] { HostRoute(), HostRoute() });
        result.Warnings.Should().Contain(w => w.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_flags_a_disabled_route_that_could_not_serve_if_it_were_switched_on()
    {
        // This is the designer's save gate, and it used to look only at enabled rows — so a route
        // saved with the Enabled box cleared could carry anything: a redirect with no target, a port
        // of 0, headers that are not JSON. Nothing was wrong until the deployment that owns the host
        // switched the row on, which it does without re-reading the fields it did not write. The row
        // is checked when it is written, or it is checked by nobody.
        var route = new Route
        {
            Host = "later.example.com", Type = RouteType.Redirect, RedirectTo = "", IsEnabled = false
        };

        var result = Engine().Validate(new[] { route });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("redirect target", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_does_not_warn_about_a_disabled_route_sharing_a_live_routes_host()
    {
        // The duplicate warning is a statement about which of two LIVE routes wins. A route that is
        // switched off wins nothing, and warning about it would teach people that the way to park a
        // route is to delete it.
        var live = HostRoute();
        var parked = HostRoute();
        parked.IsEnabled = false;

        var result = Engine().Validate(new[] { live, parked });

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Higher_priority_router_is_rendered_first()
    {
        var low = HostRoute(host: "a.example.com"); low.Priority = 1;
        var high = HostRoute(host: "b.example.com"); high.Priority = 100;
        var content = Engine().Preview(new[] { low, high }).Content;
        content.IndexOf("b.example.com", StringComparison.Ordinal)
            .Should().BeLessThan(content.IndexOf("a.example.com", StringComparison.Ordinal),
                "routes are ordered by descending priority");
    }

    // ---- ApplyAllAsync: what a deployment is now allowed to trust ----
    //
    // The pipeline fails a deployment on a non-success result from here, so what this returns — and
    // whether the file it manages survived — is a contract and not an implementation detail. It had
    // no tests of its own until that became true.

    [Fact]
    public async Task A_successful_apply_writes_the_rendered_config_to_the_target()
    {
        using var cfg = new TempConfig();

        var result = await Engine(cfg.Options, HostRoute()).ApplyAllAsync(null, default);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        result.RolledBack.Should().BeFalse();
        File.ReadAllText(cfg.Target).Should().Contain("Host(`app.example.com`)");
    }

    [Fact]
    public async Task A_route_that_fails_validation_is_refused_to_its_owner_and_left_out_of_the_file()
    {
        // The atomic-apply gate: a config Traefik would reject must never reach the file it watches,
        // because a file provider reloads whatever it finds there. What changed is where the gate
        // falls — on the route rather than on the file. The offending route is not rendered, so the
        // document Traefik gets is still one it accepts; the rest of the platform keeps its routing
        // instead of losing it to somebody else's row.
        using var cfg = new TempConfig();
        var route = HostRoute(port: 70000);
        var live = HostRoute(host: "unaffected.example.com");

        // The caller here owns the failing route, so the engine's answer names it in full and the
        // apply is a failure for them — see PlatformProxyConfigTests for what a caller who does NOT
        // own it is (and is not) told, and why their apply succeeds.
        var result = await Engine(cfg.Options, route, live).ApplyAllAsync(route.WorkspaceId, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("port");
        result.RolledBack.Should().BeFalse("the file was written, not restored");
        var config = File.ReadAllText(cfg.Target);
        config.Should().NotContain("Host(`app.example.com`)", "the route that could not serve is not in it");
        config.Should().Contain("Host(`unaffected.example.com`)", "everything that can serve still does");
    }

    [Fact]
    public async Task A_write_that_fails_restores_the_backup_and_says_it_rolled_back()
    {
        // The deployment message tells the operator whether the live routes are intact, so the flag
        // has to mean what it says: the file is back to the backup's contents, not merely unchanged.
        using var cfg = new TempConfig();
        Directory.CreateDirectory(Path.GetDirectoryName(cfg.Target)!);
        File.WriteAllText(cfg.Target, "half-written config");
        File.WriteAllText(cfg.Target + ".bak", "the config that was live");
        cfg.BlockStaging();

        var result = await Engine(cfg.Options, HostRoute()).ApplyAllAsync(null, default);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace("the deployment quotes this back to the operator");
        result.RolledBack.Should().BeTrue();
        File.ReadAllText(cfg.Target).Should().Be("the config that was live");
    }

    [Fact]
    public async Task A_write_that_fails_with_no_backup_to_restore_does_not_claim_a_rollback()
    {
        using var cfg = new TempConfig();
        Directory.CreateDirectory(Path.GetDirectoryName(cfg.Target)!);
        cfg.BlockStaging();

        var result = await Engine(cfg.Options, HostRoute()).ApplyAllAsync(null, default);

        result.Success.Should().BeFalse();
        result.RolledBack.Should().BeFalse("there was no previous version to put back");
    }

    [Fact]
    public async Task A_successful_apply_leaves_no_half_written_render_behind()
    {
        // Traefik reloads whatever appears in the directory it watches, and an operator reading it
        // has to be able to tell the config from the litter.
        using var cfg = new TempConfig();

        await Engine(cfg.Options, HostRoute()).ApplyAllAsync(null, default);

        cfg.LeftoverRenders().Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_apply_leaves_no_half_written_render_behind()
    {
        using var cfg = new TempConfig();
        Directory.CreateDirectory(Path.GetDirectoryName(cfg.Target)!);
        // A directory where the config belongs: the render is written, and the swap into place is
        // what refuses — so this is the case where litter would survive if nothing removed it.
        Directory.CreateDirectory(cfg.Target);

        var result = await Engine(cfg.Options, HostRoute()).ApplyAllAsync(null, default);

        result.Success.Should().BeFalse("the target path is not a file this engine can swap");
        cfg.LeftoverRenders().Should().BeEmpty("the attempt cleans up after itself");
    }

    /// <summary>A throwaway dynamic-config location, so an apply test writes real files and cleans up.</summary>
    private sealed class TempConfig : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "harbora-traefik-tests", Guid.NewGuid().ToString("N"));

        public string Target => Path.Combine(_root, "dynamic", "harbora.yml");
        public TraefikOptions Options => new() { DynamicConfigPath = Target };

        public string Staging => Path.Combine(
            Path.GetDirectoryName(Target)!, TraefikProxyEngine.StagingDirectoryName);

        /// <summary>
        /// Puts a file where the engine stages its render, so the staging directory cannot be
        /// created and the write fails. A real I/O failure on a real filesystem — nothing stubbed —
        /// and it works the same as any user on any platform, which a permission bit does not.
        /// </summary>
        public void BlockStaging() => File.WriteAllText(Staging, "not a directory");

        /// <summary>Renders that were started and never became the config.</summary>
        public IReadOnlyList<string> LeftoverRenders() =>
            Directory.Exists(Staging) ? Directory.GetFiles(Staging) : [];

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* temp dir — best effort */ }
        }
    }
}
