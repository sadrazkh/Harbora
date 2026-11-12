using FluentAssertions;
using Harbora.Shared;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The one rule about env files that <c>harbora deploy</c>'s packer and the panel's zip filter share
/// (<see cref="EnvFiles"/>): which hold real values and stay on the machine they are on, which are
/// examples and go, and what a <c>!</c> line in the project's ignore file brings back.
/// </summary>
public sealed class EnvFilesTests
{
    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData(".env.production")]
    [InlineData(".env.staging")]
    [InlineData(".env.development")]
    [InlineData(".env.production.local")]
    [InlineData("apps/web/.env.production")]
    [InlineData(".ENV.PRODUCTION")]
    public void One_holding_real_values_stays_home_when_no_ignore_file_says_otherwise(string path)
    {
        // Only .env and .env.local used to: a folder that was never in Git has no ignore file to
        // name the rest, so .env.production went to the server with every push.
        EnvFiles.StaysHome(path, []).Should().BeTrue();
    }

    [Theory]
    [InlineData(".env.example")]
    [InlineData(".env.sample")]
    [InlineData(".env.template")]
    [InlineData(".env.dist")]
    [InlineData("env.example")]
    [InlineData("example.env")]
    [InlineData("sample.env")]
    [InlineData(".env.local.example")]
    [InlineData(".env.production.sample")]
    [InlineData(".envrc")]
    [InlineData("config/env")]
    [InlineData("production.env")]
    public void Examples_and_what_is_merely_named_like_one_go(string path)
    {
        EnvFiles.StaysHome(path, []).Should().BeFalse();
    }

    [Fact]
    public void Every_example_the_inspector_reads_is_one_the_upload_still_sends()
    {
        // Launch learns a project's variable names from these, from what the CLI uploaded. One kept
        // back here would be a question Launch never asks.
        EnvFiles.ExampleNames.Where(name => EnvFiles.StaysHome(name, [])).Should().BeEmpty();
        EnvFiles.ExampleNames.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("!.env.production", ".env.production", true)]
    [InlineData("!.env.production", "apps/web/.env.production", true)]       // a bare name, at any depth
    [InlineData("!apps/web/.env.production", "apps/web/.env.production", true)]
    [InlineData("!apps/web/.env.production", ".env.production", false)]      // a path, from the root
    [InlineData("!/.env.production/", ".env.production", true)]
    [InlineData("!.env.*", ".env.staging", true)]
    [InlineData("!**/.env.production", "apps/web/.env.production", true)]
    [InlineData("!.env.staging", ".env.production", false)]
    [InlineData("!.env", ".env", false)]
    [InlineData("!.env.local", ".env.local", false)]
    [InlineData("!.env*", ".env.local", false)]
    public void A_bang_line_lets_one_through_and_never_env_or_env_local(string line, string path, bool sent)
    {
        EnvFiles.StaysHome(path, IgnoreFile.Entries([line])).Should().Be(!sent);
    }

    [Fact]
    public void An_ordinary_line_naming_an_env_file_brings_nothing_back()
    {
        EnvFiles.StaysHome(".env.production", IgnoreFile.Entries([".env.production"])).Should().BeTrue();
    }

    [Fact]
    public void A_bang_line_brings_back_an_env_file_and_nothing_else()
    {
        EnvFiles.IsLetThrough("secrets.json", IgnoreFile.Entries(["!secrets.json"])).Should().BeFalse(
            "every other ! line is still not read, as it never was: this is no gitignore implementation");
        EnvFiles.IsLetThrough(".env.example", IgnoreFile.Entries(["!.env.example"])).Should().BeTrue();
    }

    [Fact]
    public void An_ignore_file_keeps_its_bang_lines_and_drops_comments_and_blank_ones()
    {
        IgnoreFile.Entries(["# a comment", "", "   ", "build-output", "!keep-me", "/leading-slash/", "! .env.production", "!", "dir\\sub"])
            .Should().Equal("build-output", "!keep-me", "leading-slash", "!.env.production", "dir/sub");
    }

    [Fact]
    public void The_dockerignore_is_read_before_the_gitignore()
    {
        IgnoreFile.Names.Should().Equal(".dockerignore", ".gitignore");
    }
}
