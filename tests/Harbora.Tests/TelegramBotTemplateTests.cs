using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Learning;
using Harbora.Infrastructure.Templates;
using Harbora.Web.Data;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F7 (2026-08-21 functions-and-services plan): the long-polling Telegram bot worker templates.
/// Read here from <see cref="DbSeeder.BuiltInTemplates"/> itself — the real seeded data, not a copy
/// that could drift from it — the same discipline <c>ReadyAppCatalogTests</c> holds the other,
/// versioned catalogue to.
///
/// <para>
/// There are two of them, Node.js and Python, and everything below holds for both. They are separate
/// templates rather than one language-neutral entry because they differ in the only place a first
/// deploy actually goes wrong: which file gets started. Node names its own entry point in
/// <c>package.json</c> via <c>npm start</c>; Python has no such file, so <c>Buildpacks</c> picks from
/// a fixed candidate list.
/// </para>
///
/// <para>
/// A template that does not actually deploy is worse than no template: these assertions are the
/// closest thing to proof available without Docker on this machine (no live server here — see
/// <c>TemplateKindAndRequiredSecretDeploymentTests</c> for the deploy-path proof against a faked DB).
/// </para>
/// </summary>
public class TelegramBotTemplateTests
{
    /// <summary>Both bot templates, with the guide each one claims to be documented by.</summary>
    public static TheoryData<string, string> BotTemplates() => new()
    {
        { "telegram-bot", "/learn/10-telegram-bot" },
        { "telegram-bot-python", "/learn/14-python-bot" }
    };

    private static AppTemplate Find(string key) =>
        DbSeeder.BuiltInTemplates().Should().ContainSingle(t => t.Key == key).Subject;

    private static TemplateManifest Manifest(string key)
    {
        var template = Find(key);
        TemplateManifest.TryParse(template.ManifestJson, out var manifest, out var errors)
            .Should().BeTrue($"{template.Key}: {string.Join(" ", errors)}");
        return manifest!;
    }

    [Theory]
    [MemberData(nameof(BotTemplates))]
    public void The_template_is_in_the_built_in_catalogue(string key, string _)
    {
        DbSeeder.BuiltInTemplates().Select(t => t.Key).Should().Contain(key);
    }

    [Theory]
    [MemberData(nameof(BotTemplates))]
    public void The_manifest_actually_parses(string key, string _)
    {
        // The catalogue page silently drops any template whose manifest does not parse, and
        // TemplateDeploymentService refuses one that does not — both look identical to "the
        // template does not exist" on screen.
        Manifest(key);
    }

    [Theory]
    [MemberData(nameof(BotTemplates))]
    public void It_is_enabled_and_named_in_both_languages(string key, string _)
    {
        var template = Find(key);

        template.IsEnabled.Should().BeTrue();
        template.IsBuiltIn.Should().BeTrue();
        template.Name.Should().NotBeNullOrWhiteSpace();
        template.NameFa.Should().NotBeNullOrWhiteSpace();
        template.Description.Should().NotBeNullOrWhiteSpace();
        template.DescriptionFa.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(BotTemplates))]
    public void It_is_a_worker_so_it_gets_no_domain(string key, string _)
    {
        // The whole point of "works with zero public exposure at all": a long-polling bot answers
        // no HTTP, so it must not be given a domain nobody will ever route real traffic to.
        var manifest = Manifest(key);

        manifest.Kind.Should().Be("worker");
        manifest.Source.Should().Be("git");
    }

    [Theory]
    [MemberData(nameof(BotTemplates))]
    public void A_worker_declares_no_port_and_no_health_path(string key, string _)
    {
        // Declaring either would hand the deploy a health check waiting for an answer a long-polling
        // bot never sends — the failure this template's "kind":"worker" exists to prevent, undone by
        // one stray field.
        var manifest = Manifest(key);

        manifest.Port.Should().BeNull();
        manifest.HealthPath.Should().BeNullOrEmpty();
    }

    [Theory]
    [MemberData(nameof(BotTemplates))]
    public void The_bot_token_is_required_and_masked_not_generated(string key, string _)
    {
        // Before "required" existed, a secret with no default was always auto-generated — right
        // for an application key, silently wrong for a token Telegram itself issued that Harbora
        // cannot invent.
        var token = Manifest(key).Variables.Should().ContainSingle(v => v.Key == "TELEGRAM_BOT_TOKEN").Subject;

        token.Secret.Should().BeTrue();
        token.Required.Should().BeTrue();
        token.Default.Should().BeNullOrEmpty();
    }

    [Theory]
    [MemberData(nameof(BotTemplates))]
    public void It_links_to_its_own_learning_centre_guide(string key, string guide)
    {
        Manifest(key).DocumentationUrl.Should().Be(guide);
    }

    [Theory]
    [MemberData(nameof(BotTemplates))]
    public void Setup_resolves_the_worker_kind_it_deploys_as(string key, string _)
    {
        // The seam TemplateDeploymentService actually reads (TemplateSetup.Prepare), proven against
        // the real seeded manifest rather than a hand-written duplicate of its JSON.
        TemplateSetup.Prepare(Manifest(key), () => "unused").Kind.Should().Be(ServiceKind.Worker);
    }

    [Theory]
    [MemberData(nameof(BotTemplates))]
    public void The_manifest_does_not_mount_the_host_docker_socket(string key, string _)
    {
        // The single most dangerous thing a template can do (ReadyAppCatalogTests holds the other
        // catalogue to the same rule).
        Find(key).ManifestJson.Should().NotContain("/var/run/docker.sock");
    }

    /// <summary>
    /// Every built-in template that claims a Learning Centre guide must name one that exists.
    ///
    /// <para>
    /// Until this ran, the only assertion on a template's documentation link was that the string
    /// equalled a string written beside it — which proves the two copies agree and nothing about
    /// whether the chapter is there. A renamed or misspelled chapter left the template's "read the
    /// guide" link serving the 404 page, with every test still green. Reads the real chapters from
    /// <see cref="LearningLibrary"/> for the reason <c>LearningCensusTests</c> gives for the Help
    /// map: a list of slugs written here is the thing that goes stale.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_built_in_template_linking_into_the_learning_centre_names_a_chapter_that_exists()
    {
        var onDisk = new LearningLibrary(TestPaths.DocsRoot).Chapters().Select(c => c.Slug).ToHashSet();
        onDisk.Should().NotBeEmpty("the census only means something if there are real chapters to check against");

        var claimed = DbSeeder.BuiltInTemplates()
            .Select(t => (t.Key, Manifest: TemplateManifest.TryParse(t.ManifestJson, out var m, out _) ? m : null))
            .Where(t => t.Manifest?.DocumentationUrl is { } url && url.StartsWith("/learn/", StringComparison.Ordinal))
            .Select(t => (t.Key, Slug: t.Manifest!.DocumentationUrl!["/learn/".Length..]))
            .ToList();

        claimed.Should().NotBeEmpty("at least the two bot templates point into the Learning Centre");

        claimed.Should().OnlyContain(t => onDisk.Contains(t.Slug),
            "a template's documentation link must resolve to a chapter LearningLibrary actually finds " +
            "on disk — otherwise 'read the guide' lands on a 404");
    }
}
