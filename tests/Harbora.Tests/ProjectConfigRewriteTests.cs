using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Correcting the app name in a config the user already has.
///
/// The first `harbora deploy Kousar-kolie` wrote that name into harbora.yml, and RememberApp never
/// overwrites — so the folder repeated the same hidden 404 on every later run, with no way out short
/// of editing the file by hand. Fixing it must not cost the user the rest of their config, so this
/// replaces one line rather than regenerating a file from the two fields the CLI happens to know.
/// </summary>
public class ProjectConfigRewriteTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"harbora-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void The_app_name_is_replaced_and_everything_else_survives()
    {
        var path = WriteTemp(
            """
            # Written by `harbora deploy`. Full schema: docs/cli-deploy.md
            app: Kousar-kolie
            server: https://platform.irnetfree.info
            dockerfile: docker/Dockerfile
            ignore:
              - node_modules
              - .cache
            """);

        ProjectConfig.RewriteAppSlug(path, "kousar-kolie");

        var config = ProjectConfig.Parse(File.ReadAllLines(path));
        config.App.Should().Be("kousar-kolie");
        config.Server.Should().Be("https://platform.irnetfree.info");
        config.Dockerfile.Should().Be("docker/Dockerfile");
        config.Ignore.Should().Equal("node_modules", ".cache");
        File.ReadAllText(path).Should().Contain("# Written by", "comments are the user's, not ours");

        File.Delete(path);
    }

    [Fact]
    public void The_name_alias_is_rewritten_where_it_stands()
    {
        // `name:` is the documented alias for `app:`. Appending a second key would leave the file
        // saying two different things, and Parse takes the last one it reads.
        var path = WriteTemp("name: old-app\nserver: https://panel.example.com\n");

        ProjectConfig.RewriteAppSlug(path, "new-app");

        File.ReadAllText(path).Should().NotContain("old-app");
        ProjectConfig.Parse(File.ReadAllLines(path)).App.Should().Be("new-app");

        File.Delete(path);
    }

    [Fact]
    public void A_config_with_no_app_line_gains_one()
    {
        var path = WriteTemp("server: https://panel.example.com\n");

        ProjectConfig.RewriteAppSlug(path, "my-api");

        var config = ProjectConfig.Parse(File.ReadAllLines(path));
        config.App.Should().Be("my-api");
        config.Server.Should().Be("https://panel.example.com");

        File.Delete(path);
    }

    [Fact]
    public void An_indented_app_key_is_not_the_app_name()
    {
        // `app:` nested under another key belongs to that block. Rewriting it would corrupt the file
        // and leave the real app name untouched.
        var path = WriteTemp("build:\n  app: inner\nserver: https://panel.example.com\n");

        ProjectConfig.RewriteAppSlug(path, "my-api");

        var text = File.ReadAllText(path);
        text.Should().Contain("  app: inner");
        ProjectConfig.Parse(File.ReadAllLines(path)).App.Should().Be("my-api");

        File.Delete(path);
    }
}
