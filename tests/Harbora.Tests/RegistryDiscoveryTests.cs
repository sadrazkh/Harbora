using FluentAssertions;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Templates;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Reading a registry's tags and deciding which of them are versions worth offering.
///
/// A registry is mostly not releases. It is <c>latest</c>, <c>main</c>, commit hashes, release
/// candidates and the same release published under four names. Every one of those, turned into a
/// version a customer can pick, is either a moving image or an unfinished one presented as a
/// considered choice — and the person picking it has no way to tell.
/// </summary>
public class RegistryDiscoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static AppTemplateVersion Version(
        string version, string repository = "demo/app", string architectures = "amd64,arm64") => new()
    {
        Id = Guid.CreateVersion7(),
        AppTemplateId = Guid.Empty,
        Version = version,
        ImageRepository = repository,
        ImageTag = version,
        ImageDigest = "sha256:" + new string('a', 64),
        SupportedArchitectures = architectures,
        ManifestJson = $$"""{"image":"{{repository}}:{{version}}","port":80}"""
    };

    // ---- reading a tag ----

    [Theory]
    [InlineData("16", new[] { 16 })]
    [InlineData("16.4", new[] { 16, 4 })]
    [InlineData("16.4.1", new[] { 16, 4, 1 })]
    [InlineData("v2.7", new[] { 2, 7 })]
    public void A_version_shaped_tag_is_read_as_one(string tag, int[] expected)
    {
        RegistryTag.Parse(tag)!.Parts.Should().Equal(expected);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("LATEST")]
    [InlineData("main")]
    [InlineData("edge")]
    [InlineData("nightly")]
    [InlineData("stable")]
    public void A_moving_pointer_is_not_a_version(string tag)
    {
        // These move. Offering one as a version undoes the entire reason versions are pinned.
        RegistryTag.Parse(tag).Should().BeNull();
    }

    [Theory]
    [InlineData("1.2.3-rc1")]
    [InlineData("1.2.3-beta.2")]
    [InlineData("2.0-alpha")]
    [InlineData("3.1-preview")]
    [InlineData("4.0-SNAPSHOT")]
    public void An_unfinished_release_is_not_a_version(string tag)
    {
        // A customer offered a release candidate has no way to know it is one.
        RegistryTag.Parse(tag).Should().BeNull();
    }

    [Theory]
    [InlineData("a1b2c3d")]
    [InlineData("sha-9f8e7d6")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2")]
    [InlineData("1.+2")]
    [InlineData("16-")]
    public void Anything_that_cannot_be_read_confidently_is_not_a_version(string tag)
    {
        // Guessing is how a commit hash becomes release 5.
        RegistryTag.Parse(tag).Should().BeNull();
    }

    [Fact]
    public void A_variant_is_kept_and_is_not_a_pre_release()
    {
        // alpine is a different base image, not an unfinished release.
        var parsed = RegistryTag.Parse("16.4-alpine");

        parsed.Should().NotBeNull();
        parsed!.Variant.Should().Be("alpine");
        parsed.Parts.Should().Equal(16, 4);
    }

    [Theory]
    [InlineData("16.4", "16.3", 1)]
    [InlineData("16.4", "16.4", 0)]
    [InlineData("16.4", "17.0", -1)]
    [InlineData("16", "16.0", 0)]
    [InlineData("16.0.1", "16", 1)]
    public void Versions_compare_by_number_not_by_text(string left, string right, int expected)
    {
        // As text, "9" sorts after "10". A registry that publishes both would offer the older one as
        // the newest release, and it would look right on the page.
        Math.Sign(RegistryTag.Parse(left)!.CompareTo(RegistryTag.Parse(right)!)).Should().Be(expected);
    }

    [Fact]
    public void Nine_is_older_than_ten()
    {
        RegistryTag.Parse("9")!.CompareTo(RegistryTag.Parse("10")!).Should().BeNegative();
    }

    // ---- choosing candidates ----

    [Fact]
    public void Only_tags_newer_than_everything_stored_are_candidates()
    {
        // Backfilling an older release offers a customer a downgrade dressed as a new option.
        var existing = new[] { Version("16.4"), Version("16.3") };

        RegistryDiscovery.Candidates(existing, ["16.5", "16.2", "15.9"])
            .Should().Equal("16.5");
    }

    [Fact]
    public void A_tag_already_stored_is_not_offered_again()
    {
        var existing = new[] { Version("16.4") };

        RegistryDiscovery.Candidates(existing, ["16.4", "16.5"]).Should().Equal("16.5");
    }

    [Fact]
    public void Only_the_shape_already_in_use_is_followed()
    {
        // A repository publishes 17, 17.1 and 17.1-alpine for one release. Taking all three offers a
        // customer the same software three times under different names.
        var existing = new[] { Version("16.4") };

        RegistryDiscovery.Candidates(existing, ["17.1", "17", "17.1-alpine", "17.1.2"])
            .Should().Equal("17.1");
    }

    [Fact]
    public void A_variant_in_use_is_followed_rather_than_dropped()
    {
        var existing = new[] { Version("16.4-alpine") };

        RegistryDiscovery.Candidates(existing, ["16.5-alpine", "16.5"])
            .Should().Equal("16.5-alpine");
    }

    [Fact]
    public void Newest_first()
    {
        var existing = new[] { Version("1.0") };

        RegistryDiscovery.Candidates(existing, ["1.2", "1.9", "1.3"])
            .Should().Equal("1.9", "1.3", "1.2");
    }

    [Fact]
    public void A_run_adds_no_more_than_its_limit()
    {
        // A repository with two hundred releases since the catalogue was written must not produce
        // two hundred draft rows. That is a job somebody turns off, and then nothing is ever
        // discovered again.
        var existing = new[] { Version("1.0") };
        var tags = Enumerable.Range(1, 50).Select(i => $"1.{i}").ToList();

        RegistryDiscovery.Candidates(existing, tags).Should().HaveCount(RegistryDiscovery.MaximumPerRun);
    }

    [Fact]
    public void Nothing_is_discovered_for_a_template_with_no_versions()
    {
        // With nothing to compare against, a shape would have to be guessed, and every tag the
        // repository ever had would qualify.
        RegistryDiscovery.Candidates([], ["1.0", "2.0"]).Should().BeEmpty();
    }

    [Fact]
    public void Nothing_is_discovered_when_no_stored_version_can_be_read()
    {
        var existing = new[] { Version("edge") };

        RegistryDiscovery.Candidates(existing, ["1.0", "2.0"]).Should().BeEmpty();
    }

    // ---- what a discovered version looks like ----

    [Fact]
    public void A_discovered_version_arrives_as_a_draft()
    {
        // The whole point. A registry gaining a tag is not an operator deciding their customers
        // should run it.
        var built = RegistryDiscovery.Build(Version("16.4"), "16.5", "sha256:" + new string('b', 64), Now);

        built.Publication.Should().Be(VersionPublication.Draft);
    }

    [Fact]
    public void A_discovered_version_never_claims_to_be_the_recommended_one()
    {
        // Exactly one version per template may be recommended, and which one is a decision about
        // customers, not about tags.
        var built = RegistryDiscovery.Build(Version("16.4"), "16.5", "sha256:" + new string('b', 64), Now);

        built.Lifecycle.Should().Be(VersionLifecycle.Stable);
    }

    [Fact]
    public void A_discovered_version_is_pinned_and_stamped()
    {
        var digest = "sha256:" + new string('b', 64);
        var built = RegistryDiscovery.Build(Version("16.4"), "16.5", digest, Now);

        built.ImageDigest.Should().Be(digest);
        built.ImageTag.Should().Be("16.5");
        built.DiscoveredAt.Should().Be(Now);
        built.SupportedArchitectures.Should().Be("amd64,arm64");
    }

    [Fact]
    public void A_discovered_versions_manifest_names_its_own_image()
    {
        // The deploy path pins by digest and would ignore this, so a stale tag here changes nothing
        // that runs — and leaves every discovered version describing the release it was copied from
        // on every page that reads a manifest.
        var built = RegistryDiscovery.Build(Version("16.4"), "16.5", "sha256:" + new string('b', 64), Now);

        TemplateManifest.TryParse(built.ManifestJson, out var manifest, out _).Should().BeTrue();
        manifest!.Image.Should().Be("demo/app:16.5");
    }

    [Fact]
    public void A_manifest_that_cannot_be_read_is_copied_unchanged_rather_than_mangled()
    {
        var basedOn = Version("16.4");
        basedOn.ManifestJson = "not json at all";

        var built = RegistryDiscovery.Build(basedOn, "16.5", "sha256:" + new string('b', 64), Now);

        built.ManifestJson.Should().Be("not json at all");
    }

    // ---- which registry gets asked ----

    [Theory]
    [InlineData("postgres", "registry-1.docker.io", "library/postgres")]
    [InlineData("myorg/myapp", "registry-1.docker.io", "myorg/myapp")]
    [InlineData("docker.io/library/postgres", "registry-1.docker.io", "library/postgres")]
    [InlineData("docker.io/postgres", "registry-1.docker.io", "library/postgres")]
    [InlineData("ghcr.io/n8n-io/n8n", "ghcr.io", "n8n-io/n8n")]
    [InlineData("quay.io/keycloak/keycloak", "quay.io", "keycloak/keycloak")]
    public void A_repository_resolves_to_the_registry_that_serves_it(string repository, string host, string path)
    {
        var reference = RegistryReferences.Parse(repository);

        reference.Should().NotBeNull();
        reference!.Host.Should().Be(host);
        reference.Path.Should().Be(path);
    }

    [Theory]
    [InlineData("169.254.169.254/foo/bar")]
    [InlineData("localhost:5000/app")]
    [InlineData("evil.example.com/app")]
    [InlineData("10.0.0.5/app")]
    public void A_registry_we_do_not_talk_to_is_refused(string repository)
    {
        // The host here comes from a stored template field and is then called by our own server.
        // Whatever can write that field would otherwise choose who we talk to.
        RegistryReferences.Parse(repository).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ghcr.io")]
    [InlineData("ghcr.io/")]
    [InlineData("postgres@sha256:abc")]
    [InlineData("postgres with a space")]
    public void A_repository_that_makes_no_sense_is_refused(string repository)
    {
        RegistryReferences.Parse(repository).Should().BeNull();
    }
}
