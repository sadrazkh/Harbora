using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The harbora.yml schema. Other tools generate this file, so the key names and the shapes accepted
/// here are a public contract — documented in docs/cli-deploy.md and pinned by these tests.
/// </summary>
public class ProjectConfigTests
{
    private static ProjectConfig Parse(string yaml) =>
        ProjectConfig.Parse(yaml.Replace("\r\n", "\n").Split('\n'));

    [Fact]
    public void Reads_the_minimum_useful_file()
    {
        Parse("app: my-api").App.Should().Be("my-api");
    }

    [Fact]
    public void Reads_build_settings_nested_under_build()
    {
        var config = Parse("""
            app: shop
            build:
              dockerfile: docker/Dockerfile
              context: ./src
            """);

        config.App.Should().Be("shop");
        config.Dockerfile.Should().Be("docker/Dockerfile");
        config.Context.Should().Be("./src");
    }

    [Fact]
    public void Accepts_the_same_keys_at_the_top_level()
    {
        // Indentation carries no extra meaning, so a flat file works too.
        var config = Parse("app: shop\ndockerfile: Dockerfile.prod");

        config.Dockerfile.Should().Be("Dockerfile.prod");
    }

    [Fact]
    public void Reads_an_inline_dockerfile()
    {
        var config = Parse("""
            app: api
            dockerfileLines:
              - FROM node:20-alpine
              - WORKDIR /app
              - CMD ["npm", "start"]
            """);

        config.DockerfileLines.Should().Equal(
            "FROM node:20-alpine", "WORKDIR /app", """CMD ["npm", "start"]""");
    }

    [Fact]
    public void Reads_lists_written_inline()
    {
        Parse("ignore: [coverage, tmp]").Ignore.Should().Equal("coverage", "tmp");
    }

    [Fact]
    public void Reads_image_and_branch_defaults()
    {
        var config = Parse("app: a\nimage: nginx:alpine\nbranch: release");

        config.Image.Should().Be("nginx:alpine");
        config.Branch.Should().Be("release");
    }

    [Fact]
    public void Quotes_are_stripped()
    {
        Parse("app: \"my-app\"\nserver: 'https://panel.example.com'")
            .Should().BeEquivalentTo(new { App = "my-app", Server = "https://panel.example.com" },
                o => o.ExcludingMissingMembers());
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        var config = Parse("""
            # deploy config

            app: web   # the slug on the server
            """);

        config.App.Should().Be("web");
    }

    [Fact]
    public void A_hash_inside_quotes_is_content_not_a_comment()
    {
        Parse("""image: "registry.io/app@sha256#tag" """).Image.Should().Contain("#tag");
    }

    [Fact]
    public void Unknown_keys_are_ignored_so_newer_files_still_work()
    {
        // An older CLI must not choke on a file written by a newer one.
        var config = Parse("app: web\nsomeFutureKey: whatever\nnested:\n  alsoNew: 1");

        config.App.Should().Be("web");
    }

    [Fact]
    public void An_empty_file_yields_empty_config()
    {
        var config = Parse("");

        config.App.Should().BeNull();
        config.DockerfileLines.Should().BeEmpty();
    }

    [Fact]
    public void Name_and_url_are_accepted_as_aliases()
    {
        var config = Parse("name: aliased\nurl: https://panel.example.com");

        config.App.Should().Be("aliased");
        config.Server.Should().Be("https://panel.example.com");
    }
}
