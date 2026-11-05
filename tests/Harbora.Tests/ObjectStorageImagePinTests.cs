using FluentAssertions;
using Harbora.Infrastructure.Storage;
using Harbora.Infrastructure.Templates;
using System.Text.RegularExpressions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The launch proof's first run on a clean machine died here: quay.io/minio/minio answers 401 to
/// everybody now that MinIO has stopped publishing images, so <c>docker compose up</c> stopped at its
/// first pull and the installer never built anything. Every install that already existed kept running,
/// from an image it had pulled years ago, so nobody could have seen it from there, and no test can ask a
/// registry. What a test can hold is that the one image the stack, the panel's helper and the catalogue
/// run is one pin, and that it is not the registry that closed.
/// </summary>
public class ObjectStorageImagePinTests
{
    private const string ClosedRepository = "quay.io/minio/minio";

    private static string ComposeImage()
    {
        var compose = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "deploy", "docker-compose.yml"));
        var match = Regex.Match(compose, @"^  minio:\s*\r?\n(?:\s*#[^\n]*\n)*\s*image:\s*(\S+)", RegexOptions.Multiline);
        match.Success.Should().BeTrue("the compose file's minio service should name an image");
        return match.Groups[1].Value;
    }

    [Fact]
    public void The_stack_does_not_pull_from_the_registry_that_closed()
    {
        ComposeImage().Should().NotContain(ClosedRepository);
        new ObjectStorageOptions().ClientImage.Should().NotContain(ClosedRepository);
    }

    [Fact]
    public void The_server_and_the_helper_that_administers_it_are_one_pin()
    {
        // The helper runs mc against this server, and mc's admin commands follow the server's version.
        ComposeImage().Should().Be(new ObjectStorageOptions().ClientImage);
    }

    [Fact]
    public void The_catalogue_offers_the_same_image_it_runs()
    {
        var minio = ReadyAppCatalog.All().Single(a => a.Template.Key == "minio");

        minio.Versions.Should().NotBeEmpty();
        foreach (var version in minio.Versions)
        {
            version.ImageRepository.Should().NotBe(ClosedRepository);
            $"{version.ImageRepository}@{version.ImageDigest}".Should().Be(new ObjectStorageOptions().ClientImage);
        }
    }
}
