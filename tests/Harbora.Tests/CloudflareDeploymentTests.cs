using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Guards the shipped Cloudflare path. These files are operational code but are not compiled, so a
/// typo could otherwise ship an install that works until the first certificate renewal.
/// </summary>
public class CloudflareDeploymentTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull();
        return dir!.FullName;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    [Fact]
    public void Overlay_uses_cloudflare_dns01_and_keeps_the_legacy_resolver_during_upgrade()
    {
        var overlay = Read("deploy", "cloudflare.compose.yml");

        overlay.Should().Contain("certificatesresolvers.cloudflare.acme.dnschallenge.provider=cloudflare");
        overlay.Should().Contain("CF_DNS_API_TOKEN: ${CF_DNS_API_TOKEN:?");
        overlay.Should().Contain("/acme/cloudflare.json");
        overlay.Should().Contain("certificatesresolvers.letsencrypt.acme.httpchallenge.entrypoint=web");
    }

    [Fact]
    public void Base_routes_take_the_selected_certificate_resolver()
    {
        var compose = Read("deploy", "docker-compose.yml");

        compose.Should().Contain("certresolver=${ACME_CERT_RESOLVER:-letsencrypt}");
        compose.Should().Contain("Traefik__CertResolver: ${ACME_CERT_RESOLVER:-letsencrypt}");
    }

    [Fact]
    public void Installer_activation_is_complete_and_never_prints_the_token()
    {
        var installer = Read("deploy", "install.sh");

        installer.Should().Contain("COMPOSE_FILE \"docker-compose.yml:cloudflare.compose.yml\"");
        installer.Should().Contain("ACME_CERT_RESOLVER \"cloudflare\"");
        installer.Should().Contain("TRUSTED_PROXY_HOPS \"2\"");
        installer.Should().Contain("FORWARDED_CLIENT_IP_DEPTH \"1\"");
        installer.Should().Contain("read -rsp", "the interactive token prompt must not echo input");
        installer.Should().NotContain("Cloudflare token: $token",
            "the token must only be written to .env, never logged");
    }

    [Fact]
    public void Cloudflare_proxy_ranges_cover_ipv4_and_ipv6()
    {
        var overlay = Read("deploy", "cloudflare.compose.yml");

        overlay.Should().Contain("173.245.48.0/20");
        overlay.Should().Contain("2606:4700::/32");
        overlay.Should().Contain("Harbora__TrustedProxyHops: 2");
    }

    [Fact]
    public void Node_mtls_dns_is_explicitly_checked_as_dns_only()
    {
        var installer = Read("deploy", "install.sh");
        var runbook = Read("deploy", "RUNBOOK.md");

        installer.Should().Contain("check_dns \"$node\" \"$ip\" 0");
        installer.Should().Contain("check_dns \"nodes.$PANEL_DOMAIN\" \"$SERVER_IP\" 0");
        runbook.Should().Contain("DNS-only / grey cloud");
    }

    [Fact]
    public void Doctor_detects_an_incomplete_cloudflare_configuration_without_printing_secrets()
    {
        var doctor = Read("deploy", "harbora");

        doctor.Should().Contain("CF_DNS_API_TOKEN is missing");
        doctor.Should().Contain("cloudflare.compose.yml");
        doctor.Should().Contain("API token hidden");
    }
}
