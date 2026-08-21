using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Templates;
using Harbora.Web.Data;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F7 (2026-08-21 functions-and-services plan): the long-polling Telegram bot worker template.
/// Read here from <see cref="DbSeeder.BuiltInTemplates"/> itself — the real seeded data, not a copy
/// that could drift from it — the same discipline <c>ReadyAppCatalogTests</c> holds the other,
/// versioned catalogue to.
///
/// A template that does not actually deploy is worse than no template: these assertions are the
/// closest thing to proof available without Docker on this machine (no live server here — see
/// <c>TemplateKindAndRequiredSecretDeploymentTests</c> for the deploy-path proof against a faked DB).
/// </summary>
public class TelegramBotTemplateTests
{
    private static AppTemplate Find() =>
        DbSeeder.BuiltInTemplates().Should().ContainSingle(t => t.Key == "telegram-bot").Subject;

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
        DbSeeder.BuiltInTemplates().Select(t => t.Key).Should().Contain("telegram-bot");
    }

    [Fact]
    public void The_manifest_actually_parses()
    {
        // The catalogue page silently drops any template whose manifest does not parse, and
        // TemplateDeploymentService refuses one that does not — both look identical to "the
        // template does not exist" on screen.
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
    public void It_is_a_worker_so_it_gets_no_domain()
    {
        // The whole point of "works with zero public exposure at all": a long-polling bot answers
        // no HTTP, so it must not be given a domain nobody will ever route real traffic to.
        var manifest = Manifest();

        manifest.Kind.Should().Be("worker");
        manifest.Source.Should().Be("git");
    }

    [Fact]
    public void The_bot_token_is_required_and_masked_not_generated()
    {
        // Before "required" existed, a secret with no default was always auto-generated — right
        // for an application key, silently wrong for a token Telegram itself issued that Harbora
        // cannot invent.
        var token = Manifest().Variables.Should().ContainSingle(v => v.Key == "TELEGRAM_BOT_TOKEN").Subject;

        token.Secret.Should().BeTrue();
        token.Required.Should().BeTrue();
        token.Default.Should().BeNullOrEmpty();
    }

    [Fact]
    public void It_links_to_its_own_learning_centre_guide()
    {
        Manifest().DocumentationUrl.Should().Be("/learn/10-telegram-bot");
    }

    [Fact]
    public void Setup_resolves_the_worker_kind_it_deploys_as()
    {
        // The seam TemplateDeploymentService actually reads (TemplateSetup.Prepare), proven against
        // the real seeded manifest rather than a hand-written duplicate of its JSON.
        TemplateSetup.Prepare(Manifest(), () => "unused").Kind.Should().Be(ServiceKind.Worker);
    }

    [Fact]
    public void The_manifest_does_not_mount_the_host_docker_socket()
    {
        // The single most dangerous thing a template can do (ReadyAppCatalogTests holds the other
        // catalogue to the same rule).
        Find().ManifestJson.Should().NotContain("/var/run/docker.sock");
    }
}
