using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Proxy;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// HARBORA-0060: nothing republished the proxy's dynamic config at boot, so a route row left over
/// from a crash between a write and its apply named a container that was gone, and nothing healed it
/// until somebody next touched routing in that workspace — which could be never.
/// <see cref="ProxyBootRepublisher"/> is the fix: a boot-time hosted service that calls the platform's
/// one existing apply rule (<see cref="IProxyEngine.ApplyAllAsync"/>) with no workspace of its own,
/// rather than inventing a second notion of which routes are valid.
/// </summary>
public class ProxyBootRepublisherTests
{
    private const string TestKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    [Fact]
    public async Task Boot_republishes_routing_for_rows_across_more_than_one_workspace()
    {
        using var platform = new Platform();
        platform.Route(Guid.NewGuid(), "acme.example.com");
        platform.Route(Guid.NewGuid(), "globex.example.com");

        await platform.Republisher.StartAsync(default);

        var config = File.ReadAllText(platform.Target);
        config.Should().Contain("Host(`acme.example.com`)").And.Contain("Host(`globex.example.com`)",
            "a boot pass is a statement about the whole platform, exactly like an ordinary apply — " +
            "no session, no single workspace, to leave out of the render");
    }

    [Fact]
    public async Task A_row_an_ordinary_apply_would_skip_is_skipped_by_the_boot_pass_too()
    {
        using var platform = new Platform();
        platform.Route(Guid.NewGuid(), "live.example.com");
        // Out-of-range target port: the same validation TraefikProxyEngine already runs for every
        // caller. Proving the boot pass drops it too is proving it calls that rule rather than a
        // second, boot-only notion of "valid route".
        platform.Route(Guid.NewGuid(), "broken.example.com", port: 99999);

        await platform.Republisher.StartAsync(default);

        var config = File.ReadAllText(platform.Target);
        config.Should().Contain("Host(`live.example.com`)")
            .And.NotContain("Host(`broken.example.com`)",
                "a route that fails the platform's one validation rule was never going to serve, " +
                "boot pass or not");
    }

    [Fact]
    public async Task A_thrown_failure_is_logged_and_does_not_stop_the_host_starting()
    {
        var logger = new RecordingLogger<ProxyBootRepublisher>();
        var republisher = new ProxyBootRepublisher(new ThrowingEngine(), logger);

        var act = async () => await republisher.StartAsync(default);

        await act.Should().NotThrowAsync(
            "a boot pass that cannot republish routing must never take the panel down with it — a " +
            "panel that refuses to start turns one stale workspace into an outage for every workspace");
        logger.Errors.Should().ContainSingle(m => m.Contains("Startup proxy republish failed"));
    }

    [Fact]
    public async Task A_successful_republish_is_logged_for_an_operator_watching_boot_output()
    {
        using var platform = new Platform();
        platform.Route(Guid.NewGuid(), "acme.example.com");
        var logger = new RecordingLogger<ProxyBootRepublisher>();
        var republisher = new ProxyBootRepublisher(platform.Engine, logger);

        await republisher.StartAsync(default);

        logger.Infos.Should().Contain(m => m.Contains("Republished the platform's routing on startup"));
    }

    // ---- helpers ----

    /// <summary>Stands in for a proxy backend that cannot be reached at all — the engine itself
    /// throwing, rather than reporting a validation failure it already handles.</summary>
    private sealed class ThrowingEngine : IProxyEngine
    {
        public ProxyConfigPreview Preview(IReadOnlyList<Route> routes) => throw new NotSupportedException();
        public ProxyValidationResult Validate(IReadOnlyList<Route> routes) => throw new NotSupportedException();

        public Task<ProxyApplyResult> ApplyAllAsync(Guid? callerWorkspaceId, CancellationToken ct) =>
            throw new InvalidOperationException("the proxy dynamic-config directory is unreachable");
    }

    /// <summary>A platform: one database, one dynamic-config file, the real catalog and the real
    /// engine — the same combination <c>ProxyBootRepublisher</c> runs against in production, minus
    /// the request that would otherwise be scoping the read.</summary>
    private sealed class Platform : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "harbora-proxy-boot-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _provider;

        public HarboraDbContext Db { get; }
        public TraefikProxyEngine Engine { get; }
        public ProxyBootRepublisher Republisher { get; }
        public string Target => Path.Combine(_root, "dynamic", "harbora.yml");

        public Platform()
        {
            var dbName = "proxy-boot-" + Guid.NewGuid();
            var store = new InMemoryDatabaseRoot();

            // One store behind two contexts: the one this test arranges rows through, and the one
            // RouteCatalog builds for itself from its own scope factory — exactly as the real
            // singleton catalog does relative to whatever context a request or a boot pass is using.
            var services = new ServiceCollection();
            services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(dbName, store));
            _provider = services.BuildServiceProvider();

            Db = new HarboraDbContext(
                new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(dbName, store).Options);

            var catalog = new RouteCatalog(_provider.GetRequiredService<IServiceScopeFactory>());
            Engine = new TraefikProxyEngine(
                Options.Create(new TraefikOptions { DynamicConfigPath = Target }),
                new AesGcmSecretProtector(TestKey), catalog, NullLogger<TraefikProxyEngine>.Instance);
            Republisher = new ProxyBootRepublisher(Engine, NullLogger<ProxyBootRepublisher>.Instance);
        }

        public Route Route(Guid workspaceId, string host, int port = 8080)
        {
            var route = new Route
            {
                WorkspaceId = workspaceId, Host = host,
                TargetService = "harbora-" + host.Split('.')[0], TargetPort = port, IsEnabled = true
            };
            Db.Routes.Add(route);
            Db.SaveChanges();
            return route;
        }

        public void Dispose()
        {
            Db.Dispose();
            _provider.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* temp dir — best effort */ }
        }
    }

    /// <summary>Captures every formatted log line by level, so a test can assert on what an operator
    /// watching boot output would actually see.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Infos { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var bucket = logLevel switch
            {
                LogLevel.Error or LogLevel.Critical => Errors,
                LogLevel.Warning => Warnings,
                _ => Infos
            };
            bucket.Add(message);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
