using FluentAssertions;
using Harbora.Domain.Configuration;
using Harbora.Infrastructure.Configuration;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// C2 (2026-08-22 config-delivery plan): <see cref="YamlConfigFileEditor"/> is what lets a rule
/// replace a value inside a Rails <c>config/database.yml</c>-shaped file using the dot key path the
/// owner's correction requires — not the colon syntax JSON uses.
/// </summary>
public class YamlConfigFileEditorTests
{
    private readonly YamlConfigFileEditor _editor = new();

    private const string Sample = """
        # database config
        default: &default
          adapter: postgresql
          encoding: unicode

        production:
          <<: *default
          adapter: postgresql
          database: myapp_production
          password: REPLACE_ME
        """;

    [Fact]
    public void Replacing_a_nested_value_keeps_the_comment_and_the_other_keys()
    {
        var outcome = _editor.Apply(Sample, "production.password", "s3cr3t");

        outcome.Ok.Should().BeTrue();
        outcome.NewContent.Should().Contain("# database config");
        outcome.NewContent.Should().Contain("adapter: postgresql");
        outcome.NewContent.Should().Contain("database: myapp_production");
        outcome.NewContent.Should().Contain("password: \"s3cr3t\"");
    }

    [Fact]
    public void The_dot_key_path_is_the_YAML_idiom_not_the_JSON_colon_one()
    {
        var inspection = _editor.Inspect(Sample, "production.adapter");

        inspection.KeyFound.Should().BeTrue();
        inspection.CurrentValue.Should().Be("postgresql");
    }

    [Fact]
    public void Every_real_key_path_is_listed_for_a_missed_key()
    {
        var outcome = _editor.Apply(Sample, "production.missing", "x");

        outcome.Ok.Should().BeFalse();
        outcome.KeyFound.Should().BeFalse();
        outcome.KeyPaths.Should().Contain("production.database");
        outcome.KeyPaths.Should().Contain("default.adapter");
    }

    [Fact]
    public void A_broken_file_reports_the_parsers_own_line()
    {
        const string broken = "production:\n  adapter: [postgresql\n";

        var outcome = _editor.Apply(broken, "production.adapter", "y");

        outcome.Ok.Should().BeFalse();
        outcome.ParseError.Should().NotBeNull();
    }

    [Fact]
    public void Format_is_Yaml()
    {
        _editor.Format.Should().Be(ConfigFileFormat.Yaml);
    }
}
