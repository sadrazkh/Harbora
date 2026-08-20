using System.Net;
using System.Text;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Networking;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

public class CloudflarePlatformServiceTests
{
    private const string Key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Enabling_from_the_panel_encrypts_the_token_and_hot_applies_routes()
    {
        var root = Path.Combine(Path.GetTempPath(), "harbora-cf-panel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
            var proxy = new Proxy();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PANEL_DOMAIN"] = "panel.example.com",
                ["ROOT_DOMAIN"] = "apps.example.com",
                ["S3_DOMAIN"] = "s3.example.com",
                ["Cloudflare:TokenFilePath"] = Path.Combine(root, "secrets", "token"),
                ["Cloudflare:DynamicConfigPath"] = Path.Combine(root, "cloudflare.yml"),
                ["Cloudflare:EnabledMarkerPath"] = Path.Combine(root, "cloudflare.enabled")
            }).Build();
            var service = new CloudflarePlatformService(
                db, new AesGcmSecretProtector(Key), new CloudflareApiClient(new Factory(new CloudflareHandler())),
                proxy, config, new Clock(), NullLogger<CloudflarePlatformService>.Instance);

            var result = await service.EnableAsync("cf-secret-token", "example.com", proxyRecords: false, default);

            result.Success.Should().BeTrue();
            proxy.ApplyCount.Should().Be(1, "all managed app routes switch resolver immediately");
            File.ReadAllText(Path.Combine(root, "secrets", "token")).Should().Contain("cf-secret-token");
            var yaml = File.ReadAllText(Path.Combine(root, "cloudflare.yml"));
            yaml.Should().Contain("certResolver: cloudflare");
            yaml.Should().Contain("Host(`panel.example.com`)");
            yaml.Should().NotContain("cf-secret-token", "dynamic routing config is not a secret store");
            File.Exists(Path.Combine(root, "cloudflare.enabled")).Should().BeTrue();

            var stored = await db.Settings.SingleAsync(s => s.Key == SettingKeys.CloudflareToken);
            stored.IsSecret.Should().BeTrue();
            stored.Value.Should().NotContain("cf-secret-token");
            new AesGcmSecretProtector(Key).Unprotect(stored.Value).Should().Be("cf-secret-token");
            (await service.GetStateAsync(default)).Enabled.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_token_that_cannot_read_the_zone_changes_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "harbora-cf-panel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PANEL_DOMAIN"] = "panel.example.com",
                ["ROOT_DOMAIN"] = "apps.example.com",
                ["Cloudflare:TokenFilePath"] = Path.Combine(root, "token"),
                ["Cloudflare:DynamicConfigPath"] = Path.Combine(root, "cloudflare.yml"),
                ["Cloudflare:EnabledMarkerPath"] = Path.Combine(root, "cloudflare.enabled")
            }).Build();
            var service = new CloudflarePlatformService(
                db, new AesGcmSecretProtector(Key), new CloudflareApiClient(new Factory(new MissingZoneHandler())),
                new Proxy(), config, new Clock(), NullLogger<CloudflarePlatformService>.Instance);

            var result = await service.EnableAsync("valid-but-too-narrow", "example.com", false, default);

            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Zone:Read");
            Directory.GetFiles(root, "*", SearchOption.AllDirectories).Should().BeEmpty();
            db.Settings.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Clock : ISystemClock { public DateTimeOffset UtcNow => Now; }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CloudflareHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/client/v4/user/tokens/verify" => "{\"success\":true,\"result\":{\"status\":\"active\"}}",
                "/client/v4/zones" => "{\"success\":true,\"result\":[{\"id\":\"zone-123456789\",\"name\":\"example.com\"}]}",
                "/client/v4/zones/zone-123456789/settings/ssl" => "{\"success\":true,\"result\":{\"value\":\"strict\"}}",
                _ => "{\"success\":false,\"errors\":[{\"message\":\"unexpected test request\"}]}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class MissingZoneHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/user/tokens/verify", StringComparison.Ordinal)
                ? "{\"success\":true,\"result\":{\"status\":\"active\"}}"
                : "{\"success\":true,\"result\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Proxy : IProxyEngine
    {
        public int ApplyCount { get; private set; }
        public ProxyConfigPreview Preview(IReadOnlyList<Route> routes) => new("yaml", "");
        public ProxyValidationResult Validate(IReadOnlyList<Route> routes) => new(true, [], []);
        public Task<ProxyApplyResult> ApplyAllAsync(Guid? callerWorkspaceId, CancellationToken ct)
        {
            ApplyCount++;
            return Task.FromResult(new ProxyApplyResult(true, null, false));
        }
    }
}
