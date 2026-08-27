using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Proxy;
using Harbora.Infrastructure.Security;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// C3 (2026-08-27 what's-left plan): a redeploy must not silently drop an app's rate limit — the
/// exact twin of the defect <c>DeploymentPipelineCutoverTests</c>'s own maintenance-mode tests were
/// written to close. Proven against the REAL <see cref="TraefikProxyEngine"/> writing to a temporary
/// file (the same seam <c>PlatformProxyConfigTests</c> uses via <c>PipelineHarness.ProxyOverride</c>),
/// so what is asserted is the rendered YAML Traefik would actually read — not a database flag, which
/// is exactly what still reads "true" while a bug of this shape is live.
/// </summary>
public class DeploymentPipelineRateLimitTests
{
    private const string TestKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    /// <summary>The real engine, reading the harness's own rows — the same shape
    /// <c>PlatformProxyConfigTests.RealEngine</c> uses.</summary>
    private static TraefikProxyEngine RealEngine(PipelineHarness h, string target) => new(
        Options.Create(new TraefikOptions { DynamicConfigPath = target }),
        new AesGcmSecretProtector(TestKey),
        new HarnessCatalog(h.Db),
        NullLogger<TraefikProxyEngine>.Instance);

    private sealed class HarnessCatalog(Harbora.Data.HarboraDbContext db) : IRouteCatalog
    {
        public async Task<IReadOnlyList<Route>> AllEnabledAsync(CancellationToken ct) =>
            await db.Routes.IgnoreQueryFilters().Where(r => r.IsEnabled)
                .OrderBy(r => r.Id).AsNoTracking().ToListAsync(ct);
    }

    private sealed class TempTarget : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "harbora-ratelimit-deploy", Guid.NewGuid().ToString("N"));

        public string Target => Path.Combine(_root, "dynamic", "harbora.yml");

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* temp dir — best effort */ }
        }
    }

    [Fact]
    public async Task A_redeploy_of_a_rate_limited_app_keeps_the_limit_in_the_rendered_config()
    {
        using var h = new PipelineHarness().WithDomain();
        using var config = new TempTarget();
        h.ProxyOverride = RealEngine(h, config.Target);
        await h.RunAsync(h.QueueDeployment(number: 1));   // ships for real, so the Route row is genuine

        // Mirrors exactly what AppOperationsService.SetRateLimitAsync does when it turns the limit
        // on: every route the app owns gets the numbers, and so does the app's own declarative flag.
        var route = await h.Db.Routes.FirstAsync(r => r.AppId == h.App.Id);
        route.RateLimitEnabled = true;
        route.RateLimitAverage = 300;
        route.RateLimitBurst = 150;
        h.App.RateLimitEnabled = true;
        h.App.RateLimitAverage = 300;
        h.App.RateLimitBurst = 150;
        h.Db.SaveChanges();

        var result = await h.RunAsync(h.QueueDeployment(number: 2));

        result.Status.Should().Be(DeploymentStatus.Succeeded, "a redeploy of a rate-limited app still ships");

        var yaml = File.ReadAllText(config.Target);
        yaml.Should().Contain("rateLimit:", "the redeploy must not have dropped the middleware entirely");
        yaml.Should().Contain("average: 300");
        yaml.Should().Contain("burst: 150");

        var reloadedRoute = await h.Db.Routes.AsNoTracking().FirstAsync(r => r.AppId == h.App.Id);
        reloadedRoute.RateLimitEnabled.Should().BeTrue();
        reloadedRoute.RateLimitAverage.Should().Be(300);
        reloadedRoute.RateLimitBurst.Should().Be(150);
    }

    [Fact]
    public async Task A_second_domain_added_to_an_already_limited_app_starts_protected_too()
    {
        // The other half of "a redeploy must not silently drop it": a brand-new route (this app's
        // second domain, wired for the first time by THIS deployment) must not start open while its
        // sibling route already enforces a limit.
        using var h = new PipelineHarness().WithDomain("first.example.com");
        using var config = new TempTarget();
        h.ProxyOverride = RealEngine(h, config.Target);
        await h.RunAsync(h.QueueDeployment(number: 1));

        var route = await h.Db.Routes.FirstAsync(r => r.AppId == h.App.Id);
        route.RateLimitEnabled = true;
        route.RateLimitAverage = 300;
        route.RateLimitBurst = 150;
        h.App.RateLimitEnabled = true;
        h.App.RateLimitAverage = 300;
        h.App.RateLimitBurst = 150;
        h.Db.SaveChanges();

        h.WithDomain("second.example.com");
        var result = await h.RunAsync(h.QueueDeployment(number: 2));

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var newRoute = await h.Db.Routes.AsNoTracking()
            .SingleAsync(r => r.AppId == h.App.Id && r.Host == "second.example.com");
        newRoute.RateLimitEnabled.Should().BeTrue(
            "a route created for a domain on an already-limited app must not start unprotected");
        newRoute.RateLimitAverage.Should().Be(300);
        newRoute.RateLimitBurst.Should().Be(150);

        var yaml = File.ReadAllText(config.Target);
        yaml.Should().Contain("second.example.com");
        // Two routers, each with their own rate-limit middleware block.
        System.Text.RegularExpressions.Regex.Matches(yaml, "rateLimit:").Count.Should().Be(2);
    }

    [Fact]
    public async Task A_route_created_while_the_app_carries_no_limit_stays_unlimited()
    {
        // The negative case beside the one above: WireProxyAsync must not turn a limit on for
        // everybody just because the app happens to have the RateLimit* columns at all.
        using var h = new PipelineHarness().WithDomain();
        using var config = new TempTarget();
        h.ProxyOverride = RealEngine(h, config.Target);

        await h.RunAsync(h.QueueDeployment(number: 1));

        var route = await h.Db.Routes.AsNoTracking().SingleAsync(r => r.AppId == h.App.Id);
        route.RateLimitEnabled.Should().BeFalse();
        File.ReadAllText(config.Target).Should().NotContain("rateLimit:");
    }
}
