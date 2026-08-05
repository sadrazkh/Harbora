using FluentAssertions;
using Harbora.Infrastructure.Templates;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Pulling an image reference apart, so somebody can name a release tag of their own.
///
/// The awkward part is that a colon means two things: the tag separator, and the port in
/// <c>registry.example.com:5000/app</c>. Splitting on the last colon without checking for a slash
/// after it turns a private registry's port number into a tag, and the pull then asks for a
/// repository that does not exist.
/// </summary>
public class ImageReferenceTests
{
    [Theory]
    [InlineData("nginx", "nginx")]
    [InlineData("nginx:1.27", "nginx")]
    [InlineData("library/postgres:16-alpine", "library/postgres")]
    [InlineData("ghcr.io/n8n-io/n8n:1.70.1", "ghcr.io/n8n-io/n8n")]
    [InlineData("docker.n8n.io/n8nio/n8n@sha256:abc", "docker.n8n.io/n8nio/n8n")]
    [InlineData("quay.io/minio/minio:RELEASE.2024-10-13T13-34-11Z", "quay.io/minio/minio")]
    public void The_repository_is_everything_before_the_tag_or_digest(string reference, string expected)
    {
        ImageReference.RepositoryOf(reference).Should().Be(expected);
    }

    [Theory]
    [InlineData("registry.example.com:5000/app", "registry.example.com:5000/app")]
    [InlineData("registry.example.com:5000/app:2.1", "registry.example.com:5000/app")]
    public void A_registry_port_is_not_mistaken_for_a_tag(string reference, string expected)
    {
        // The colon before the last slash is a port. Treating it as a tag would leave the
        // repository as "registry.example.com" and every pull would fail on a name nobody wrote.
        ImageReference.RepositoryOf(reference).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void Nothing_usable_is_nothing(string? reference)
    {
        ImageReference.RepositoryOf(reference).Should().BeNull();
    }

    [Theory]
    [InlineData("nginx:1.27", "1.27")]
    [InlineData("ghcr.io/n8n-io/n8n:1.70.1", "1.70.1")]
    [InlineData("registry.example.com:5000/app:2.1", "2.1")]
    public void The_tag_is_read_back(string reference, string expected)
    {
        ImageReference.TagOf(reference).Should().Be(expected);
    }

    [Theory]
    [InlineData("nginx")]
    [InlineData("registry.example.com:5000/app")]
    [InlineData("demo/app@sha256:abc")]
    public void A_reference_with_no_tag_has_no_tag(string reference)
    {
        // A digest is not a tag. Returning one would put a digest into a field the person is meant
        // to type a release number into.
        ImageReference.TagOf(reference).Should().BeNull();
    }

    [Theory]
    [InlineData("1.70.1")]
    [InlineData("16-alpine")]
    [InlineData("RELEASE.2024-10-13T13-34-11Z")]
    [InlineData("v2")]
    [InlineData("stable_1")]
    public void A_real_release_tag_is_accepted(string tag)
    {
        ImageReference.IsUsableTag(tag).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("with/slash")]
    [InlineData("with:colon")]
    [InlineData("with@at")]
    [InlineData(".leading-dot")]
    [InlineData("-leading-dash")]
    [InlineData("../../etc/passwd")]
    public void Anything_that_could_not_be_a_tag_is_refused(string? tag)
    {
        // This value is typed by a person and then used to build a registry URL and a stored image
        // reference. Refusing early is cheaper than escaping it in two places.
        ImageReference.IsUsableTag(tag).Should().BeFalse();
    }

    [Fact]
    public void A_tag_longer_than_a_registry_allows_is_refused()
    {
        ImageReference.IsUsableTag(new string('a', 129)).Should().BeFalse();
        ImageReference.IsUsableTag(new string('a', 128)).Should().BeTrue();
    }

    [Fact]
    public void A_repository_read_from_a_reference_can_be_resolved_to_a_registry()
    {
        // The two halves have to agree: what RepositoryOf produces is what RegistryReferences must
        // be able to parse, or naming a tag fails at the lookup with nothing to explain why.
        var repository = ImageReference.RepositoryOf("ghcr.io/n8n-io/n8n:1.70.1");

        RegistryReferences.Parse(repository).Should().NotBeNull();
    }
}
