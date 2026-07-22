using FluentAssertions;
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

    private static TraefikProxyEngine Engine()
    {
        var opts = Options.Create(new TraefikOptions());
        return new TraefikProxyEngine(opts, new AesGcmSecretProtector(TestKey),
            NullLogger<TraefikProxyEngine>.Instance);
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

    [Fact]
    public void Preview_includes_cert_resolver_when_ssl_enabled()
    {
        var route = HostRoute();
        route.SslEnabled = true;
        Engine().Preview(new[] { route }).Content.Should().Contain("certResolver: letsencrypt");
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
    public void Higher_priority_router_is_rendered_first()
    {
        var low = HostRoute(host: "a.example.com"); low.Priority = 1;
        var high = HostRoute(host: "b.example.com"); high.Priority = 100;
        var content = Engine().Preview(new[] { low, high }).Content;
        content.IndexOf("b.example.com", StringComparison.Ordinal)
            .Should().BeLessThan(content.IndexOf("a.example.com", StringComparison.Ordinal),
                "routes are ordered by descending priority");
    }
}
