using FluentAssertions;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Splitting an image reference for Docker's pull API.
///
/// This platform pins by digest everywhere — <c>VersionSelection.PinnedImage</c> produces
/// <c>repo@sha256:…</c> and it is what every ready-made application is deployed from. The split
/// took the last colon, which in a digest reference is the digest's own separator, so the
/// repository came out as <c>repo@sha256</c> and Docker refused the pull outright with "invalid
/// reference format".
///
/// The pinning the whole version model exists for was therefore the thing that made an image
/// unpullable, and it failed at deploy time rather than anywhere a test was looking.
/// </summary>
public class ImagePullReferenceTests
{
    [Fact]
    public void A_digest_reference_keeps_its_digest_whole()
    {
        var (repo, tag) = DockerEngine.SplitImageReference(
            "quay.io/minio/minio@sha256:9535594ad4122b7a78c6632788a989b96d9199b483d3bd71a5ceae73a922cdfa");

        repo.Should().Be("quay.io/minio/minio");
        tag.Should().Be("sha256:9535594ad4122b7a78c6632788a989b96d9199b483d3bd71a5ceae73a922cdfa");
    }

    [Fact]
    public void The_repository_of_a_digest_reference_never_carries_the_at_sign()
    {
        // "repo@sha256" is the exact string Docker called an invalid reference format.
        var (repo, _) = DockerEngine.SplitImageReference("gitea/gitea@sha256:abc123");

        repo.Should().NotContain("@");
        repo.Should().NotContain("sha256");
    }

    [Theory]
    [InlineData("nginx:1.27", "nginx", "1.27")]
    [InlineData("ghcr.io/n8n-io/n8n:1.70.1", "ghcr.io/n8n-io/n8n", "1.70.1")]
    [InlineData("quay.io/minio/minio:RELEASE.2024-10-13T13-34-11Z", "quay.io/minio/minio", "RELEASE.2024-10-13T13-34-11Z")]
    public void A_tagged_reference_still_splits_on_its_tag(string image, string repo, string tag)
    {
        DockerEngine.SplitImageReference(image).Should().Be((repo, tag));
    }

    [Fact]
    public void An_untagged_reference_gets_latest()
    {
        DockerEngine.SplitImageReference("nginx").Should().Be(("nginx", "latest"));
    }

    [Fact]
    public void A_registry_port_is_not_mistaken_for_a_tag()
    {
        // The colon before the last slash is a port. This case was already handled and is kept
        // under test because the digest fix touches the same line.
        DockerEngine.SplitImageReference("registry.example.com:5000/app")
            .Should().Be(("registry.example.com:5000/app", "latest"));
    }

    [Fact]
    public void A_digest_on_a_private_registry_with_a_port_survives_both_traps()
    {
        // Two colons that are not tag separators, in one reference.
        var (repo, tag) = DockerEngine.SplitImageReference("registry.example.com:5000/app@sha256:abc");

        repo.Should().Be("registry.example.com:5000/app");
        tag.Should().Be("sha256:abc");
    }

    [Fact]
    public void A_tagged_digest_reference_keeps_the_digest_as_the_thing_to_pull()
    {
        // "repo:tag@sha256:…" is legal. The digest is what identifies the image, so that is what
        // the pull must ask for — taking the tag would pull whatever it points at today, which is
        // the entire failure the pinning exists to prevent.
        var (repo, tag) = DockerEngine.SplitImageReference("postgres:16@sha256:abc");

        repo.Should().Be("postgres:16");
        tag.Should().Be("sha256:abc");
    }
}
