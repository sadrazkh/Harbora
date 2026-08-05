using FluentAssertions;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Templates;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// An operator putting a version into the dropdown by hand.
///
/// Versions could only be published or withdrawn: what was in the list came from the shipped
/// manifests and from discovery, which follows the shape already in the catalogue and only ever
/// looks forward. A template that shipped without versions had an empty dropdown permanently, and
/// an older release could not be offered at all.
/// </summary>
public class TemplateVersionEntryTests
{
    private static readonly string[] Existing = ["1.70.1", "1.63.4"];

    [Fact]
    public void A_tag_on_a_known_repository_can_be_added()
    {
        var plan = TemplateVersionEntry.Plan("1.71.0", "ghcr.io/n8n-io/n8n:1.70.1", Existing);

        plan.Allowed.Should().BeTrue();
        plan.Repository.Should().Be("ghcr.io/n8n-io/n8n");
        plan.Tag.Should().Be("1.71.0");
    }

    [Fact]
    public void An_older_release_can_be_added_too()
    {
        // The point of the feature. Discovery only ever looks forward, so a release older than
        // anything in the catalogue was unreachable — including the one a customer is mid-upgrade
        // from and needs to go back to.
        TemplateVersionEntry.Plan("1.40.0", "ghcr.io/n8n-io/n8n", Existing).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Nothing_typed_is_refused_as_nothing_typed()
    {
        TemplateVersionEntry.Plan("   ", "nginx", Existing)
            .Refusal.Should().Be(VersionEntryRefusal.MissingTag);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("with/slash")]
    [InlineData("../../etc")]
    [InlineData(".leading")]
    public void A_tag_no_registry_could_have_is_refused_before_anything_is_asked(string tag)
    {
        // This value goes into a registry URL and a stored image reference. Refusing it here is
        // cheaper than escaping it in two places, and it never reaches the network.
        TemplateVersionEntry.Plan(tag, "nginx", Existing)
            .Refusal.Should().Be(VersionEntryRefusal.InvalidTag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_no_repository_there_is_nothing_to_ask(string? repository)
    {
        // A template that names no image anywhere. Better said plainly than by a registry lookup
        // for an empty name coming back empty and reading as "tag not found".
        TemplateVersionEntry.Plan("1.0", repository, Existing)
            .Refusal.Should().Be(VersionEntryRefusal.UnknownRepository);
    }

    [Fact]
    public void A_version_already_offered_is_refused_rather_than_left_to_the_index()
    {
        // (AppTemplateId, Version) is unique. Reaching the database with a duplicate turns a
        // correct use of the page into a 500.
        TemplateVersionEntry.Plan("1.70.1", "ghcr.io/n8n-io/n8n", Existing)
            .Refusal.Should().Be(VersionEntryRefusal.AlreadyExists);
    }

    [Fact]
    public void Padding_around_a_duplicate_does_not_hide_it()
    {
        TemplateVersionEntry.Plan("  1.70.1  ", "ghcr.io/n8n-io/n8n", Existing)
            .Refusal.Should().Be(VersionEntryRefusal.AlreadyExists);
    }

    [Fact]
    public void Padding_on_the_stored_side_does_not_hide_it_either()
    {
        // The stored list is whatever is in the database, which includes rows written by seed data
        // and by imported catalogues rather than only by this code. A padded row would let the
        // duplicate through to the unique index, which is a 500 on a page used correctly.
        TemplateVersionEntry.Plan("1.70.1", "ghcr.io/n8n-io/n8n", [" 1.70.1 "])
            .Refusal.Should().Be(VersionEntryRefusal.AlreadyExists);
    }

    [Fact]
    public void Two_tags_differing_only_in_case_are_two_tags()
    {
        // A container tag is case-sensitive, and MinIO really does publish
        // "RELEASE.2024-10-13T13-34-11Z". Folding case would refuse a genuine tag on the grounds
        // that a different image already exists.
        TemplateVersionEntry.Plan("RELEASE.2024-10-13", "quay.io/minio/minio", ["release.2024-10-13"])
            .Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_registry_port_is_not_taken_for_a_tag_of_the_repository()
    {
        var plan = TemplateVersionEntry.Plan("2.1", "registry.internal:5000/app:2.0", []);

        plan.Repository.Should().Be("registry.internal:5000/app");
    }

    [Fact]
    public void What_is_stored_is_offered_immediately()
    {
        // A draft would mean the button reported success and changed nothing anybody could see —
        // the operator typed this tag precisely to put it in the dropdown.
        var plan = TemplateVersionEntry.Plan("1.71.0", "ghcr.io/n8n-io/n8n", Existing);

        var built = TemplateVersionEntry.Build(Guid.NewGuid(), plan, "sha256:abc", null, null);

        built.Publication.Should().Be(VersionPublication.Published);
        built.Lifecycle.Should().Be(VersionLifecycle.Stable);
        built.ImageDigest.Should().Be("sha256:abc");
    }

    [Fact]
    public void What_is_stored_is_never_the_recommended_one_by_itself()
    {
        // Exactly one version per template may be recommended. Adding one that claimed the slot
        // would silently change what every new deployment of that template gets.
        var plan = TemplateVersionEntry.Plan("1.71.0", "ghcr.io/n8n-io/n8n", Existing);

        TemplateVersionEntry.Build(Guid.NewGuid(), plan, "sha256:abc", null, null)
            .Lifecycle.Should().NotBe(VersionLifecycle.Recommended);
    }

    [Fact]
    public void It_is_not_passed_off_as_something_a_registry_check_found()
    {
        // The admin page shows "found <date>" for discovered versions. A hand-typed one wearing
        // that label misattributes a decision somebody made to a job.
        var plan = TemplateVersionEntry.Plan("1.71.0", "ghcr.io/n8n-io/n8n", Existing);

        TemplateVersionEntry.Build(Guid.NewGuid(), plan, "sha256:abc", null, null)
            .DiscoveredAt.Should().BeNull();
    }

    [Fact]
    public void The_first_version_of_a_template_takes_the_templates_own_manifest()
    {
        // The case that matters most: a template shipped with no versions at all had an empty
        // dropdown and no way to fill it.
        var plan = TemplateVersionEntry.Plan("1.0", "demo/app", []);

        var built = TemplateVersionEntry.Build(
            Guid.NewGuid(), plan, "sha256:abc", null,
            """{"image":"demo/app:0.9","ports":[8080]}""");

        built.ManifestJson.Should().Contain("\"ports\"");
        built.ManifestJson.Should().Contain("demo/app:1.0");
        built.ManifestJson.Should().NotContain("0.9");
    }

    [Fact]
    public void A_later_version_copies_the_one_it_joins()
    {
        var basedOn = new AppTemplateVersion
        {
            SupportedArchitectures = "amd64,arm64",
            ManifestJson = """{"image":"demo/app:1.0","env":{"TZ":"UTC"}}"""
        };
        var plan = TemplateVersionEntry.Plan("1.1", "demo/app", ["1.0"]);

        var built = TemplateVersionEntry.Build(Guid.NewGuid(), plan, "sha256:abc", basedOn, "{}");

        built.SupportedArchitectures.Should().Be("amd64,arm64");
        built.ManifestJson.Should().Contain("TZ");
        built.ManifestJson.Should().Contain("demo/app:1.1");
    }

    [Fact]
    public void A_manifest_that_does_not_parse_is_carried_across_unchanged()
    {
        // Broken is a problem for the version it came from. Writing a rewritten copy of it would
        // make that harder to find, not easier.
        var plan = TemplateVersionEntry.Plan("1.1", "demo/app", []);

        TemplateVersionEntry.Build(Guid.NewGuid(), plan, "sha256:abc", null, "not json at all")
            .ManifestJson.Should().Be("not json at all");
    }

    [Fact]
    public void Architecture_falls_back_rather_than_being_left_empty()
    {
        // An empty SupportedArchitectures makes RunsOn refuse on every node, so the version would
        // be stored, listed, and undeployable everywhere.
        var basedOn = new AppTemplateVersion { SupportedArchitectures = "" };
        var plan = TemplateVersionEntry.Plan("1.1", "demo/app", []);

        TemplateVersionEntry.Build(Guid.NewGuid(), plan, "sha256:abc", basedOn, null)
            .SupportedArchitectures.Should().Be("amd64");
    }

    [Fact]
    public void A_refused_plan_cannot_be_built()
    {
        var plan = TemplateVersionEntry.Plan(null, "demo/app", []);

        var build = () => TemplateVersionEntry.Build(Guid.NewGuid(), plan, "sha256:abc", null, null);

        build.Should().Throw<InvalidOperationException>();
    }
}
