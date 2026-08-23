using FluentAssertions;
using Harbora.Domain.Configuration;
using Harbora.Infrastructure.Configuration;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// C2 (2026-08-22 config-delivery plan): <see cref="TomlConfigFileEditor"/> — TOML, sharing the
/// plan's "INI/TOML" bucket and the same <c>section.key</c> idiom, but validated with Tomlyn (a real
/// TOML parser) rather than hand-rolled.
/// </summary>
public class TomlConfigFileEditorTests
{
    private readonly TomlConfigFileEditor _editor = new();

    private const string Sample = """
        debug = false

        [database]
        host = "localhost"
        password = "REPLACE_ME"
        port = 5432
        """;

    [Fact]
    public void Replacing_a_table_value_leaves_every_other_line_untouched()
    {
        var outcome = _editor.Apply(Sample, "database.password", "s3cr3t");

        outcome.Ok.Should().BeTrue();
        outcome.NewContent.Should().Contain("debug = false");
        outcome.NewContent.Should().Contain("host = \"localhost\"");
        outcome.NewContent.Should().Contain("password = \"s3cr3t\"");
        outcome.NewContent.Should().Contain("port = 5432");
    }

    [Fact]
    public void The_dotted_key_path_reaches_a_value_inside_a_table()
    {
        var inspection = _editor.Inspect(Sample, "database.host");

        inspection.KeyFound.Should().BeTrue();
        inspection.CurrentValue.Should().Be("localhost");
    }

    [Fact]
    public void Every_real_key_path_is_listed_for_a_missed_key()
    {
        var outcome = _editor.Apply(Sample, "database.missing", "x");

        outcome.Ok.Should().BeFalse();
        outcome.KeyPaths.Should().Contain("database.host");
        outcome.KeyPaths.Should().Contain("debug");
    }

    [Fact]
    public void A_syntactically_broken_file_reports_Tomlyns_own_diagnostic()
    {
        const string broken = "debug = \n[database\nhost = \"x\"";

        var outcome = _editor.Apply(broken, "database.host", "y");

        outcome.Ok.Should().BeFalse();
        outcome.ParseError.Should().NotBeNull();
    }

    [Fact]
    public void An_array_of_tables_is_refused_rather_than_mangled()
    {
        const string withArrayOfTables = """
            [[servers]]
            host = "a"

            [[servers]]
            host = "b"
            """;

        var outcome = _editor.Apply(withArrayOfTables, "servers.0.host", "replaced");

        outcome.Ok.Should().BeFalse();
        outcome.ParseError.Should().NotBeNull();
    }

    [Fact]
    public void Format_is_Toml()
    {
        _editor.Format.Should().Be(ConfigFileFormat.Toml);
    }
}
