using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Templates;
using Harbora.Web.Data;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F8 (2026-08-21 functions-and-services plan): the Kavenegar SMS starter — an app that sends
/// OTP/SMS through Kavenegar's own REST API, no SDK, no platform-side SMS service. Read here from
/// <see cref="DbSeeder.BuiltInTemplates"/> itself — the real seeded data, not a copy that could
/// drift from it — the same discipline <c>ReadyAppCatalogTests</c> holds the other, versioned
/// catalogue to.
///
/// A template that does not actually deploy is worse than no template: these assertions are the
/// closest thing to proof available without Docker on this machine (no live server here — see
/// <c>TemplateKindAndRequiredSecretDeploymentTests</c>, added with F7, for the deploy-path proof
/// of the same "required secret" mechanism against a faked DB).
/// </summary>
public class KavenegarSmsTemplateTests
{
    private static AppTemplate Find() =>
        DbSeeder.BuiltInTemplates().Should().ContainSingle(t => t.Key == "kavenegar-sms").Subject;

    private static TemplateManifest Manifest()
    {
        var template = Find();
        TemplateManifest.TryParse(template.ManifestJson, out var manifest, out var errors)
            .Should().BeTrue($"{template.Key}: {string.Join(" ", errors)}");
        return manifest!;
    }

    [Fact]
    public void The_template_is_in_the_built_in_catalogue()
    {
        DbSeeder.BuiltInTemplates().Select(t => t.Key).Should().Contain("kavenegar-sms");
    }

    [Fact]
    public void The_manifest_actually_parses()
    {
        Manifest();
    }

    [Fact]
    public void It_is_enabled_and_named_in_both_languages()
    {
        var template = Find();

        template.IsEnabled.Should().BeTrue();
        template.IsBuiltIn.Should().BeTrue();
        template.Name.Should().NotBeNullOrWhiteSpace();
        template.NameFa.Should().NotBeNullOrWhiteSpace();
        template.Description.Should().NotBeNullOrWhiteSpace();
        template.DescriptionFa.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void It_is_a_plain_web_app_that_can_receive_http()
    {
        // Unlike the Telegram bot (F7), this one serves HTTP on purpose — it is the OTP-sending
        // demo, and the guide's delivery-status callback receiver is a separate Harbora Function
        // (F1), not this app.
        var manifest = Manifest();

        manifest.Kind.Should().BeNull("no \"kind\" means Web, same as every template before \"kind\" existed");
        manifest.Source.Should().Be("git");
        manifest.Port.Should().NotBeNull();
    }

    [Fact]
    public void The_api_key_is_required_and_masked_not_generated()
    {
        // Before "required" existed (F7), a secret with no default was always auto-generated —
        // right for an application key, silently wrong for a credential Kavenegar itself issued
        // that Harbora cannot invent.
        var key = Manifest().Variables.Should().ContainSingle(v => v.Key == "KAVENEGAR_API_KEY").Subject;

        key.Secret.Should().BeTrue();
        key.Required.Should().BeTrue();
        key.Default.Should().BeNullOrEmpty();
    }

    [Fact]
    public void It_links_to_its_own_learning_centre_guide()
    {
        Manifest().DocumentationUrl.Should().Be("/learn/11-kavenegar-sms");
    }

    [Fact]
    public void Setup_resolves_the_web_kind_it_deploys_as()
    {
        // The seam TemplateDeploymentService actually reads (TemplateSetup.Prepare), proven against
        // the real seeded manifest rather than a hand-written duplicate of its JSON.
        TemplateSetup.Prepare(Manifest(), () => "unused").Kind.Should().Be(ServiceKind.Web);
    }

    [Fact]
    public void The_manifest_does_not_mount_the_host_docker_socket()
    {
        // The single most dangerous thing a template can do (ReadyAppCatalogTests holds the other
        // catalogue to the same rule).
        Find().ManifestJson.Should().NotContain("/var/run/docker.sock");
    }
}
