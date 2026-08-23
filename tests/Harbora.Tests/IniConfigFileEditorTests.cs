using FluentAssertions;
using Harbora.Domain.Configuration;
using Harbora.Infrastructure.Configuration;
using Xunit;

namespace Harbora.Tests;

/// <summary>C2 (2026-08-22 config-delivery plan): <see cref="IniConfigFileEditor"/> — classic
/// <c>.ini</c>/<c>.conf</c> files, keyed by <c>section.key</c>.</summary>
public class IniConfigFileEditorTests
{
    private readonly IniConfigFileEditor _editor = new();

    private const string Sample =
        "; top of file\n" +
        "debug = false\n" +
        "\n" +
        "[database]\n" +
        "host = localhost\n" +
        "password = REPLACE_ME\n" +
        "\n" +
        "[cache]\n" +
        "host = 127.0.0.1\n";

    [Fact]
    public void Replacing_a_sectioned_value_leaves_every_other_line_untouched()
    {
        var outcome = _editor.Apply(Sample, "database.password", "s3cr3t");

        outcome.Ok.Should().BeTrue();
        outcome.NewContent.Should().Contain("; top of file");
        outcome.NewContent.Should().Contain("debug = false");
        outcome.NewContent.Should().Contain("[database]");
        outcome.NewContent.Should().Contain("host = localhost");
        outcome.NewContent.Should().Contain("password = s3cr3t");
        outcome.NewContent.Should().Contain("[cache]");
        outcome.NewContent.Should().Contain("host = 127.0.0.1");
    }

    [Fact]
    public void Two_sections_sharing_a_key_name_are_disambiguated_by_section()
    {
        var inspectionDb = _editor.Inspect(Sample, "database.host");
        var inspectionCache = _editor.Inspect(Sample, "cache.host");

        inspectionDb.CurrentValue.Should().Be("localhost");
        inspectionCache.CurrentValue.Should().Be("127.0.0.1");
    }

    [Fact]
    public void A_key_outside_any_section_uses_a_bare_path()
    {
        var inspection = _editor.Inspect(Sample, "debug");

        inspection.KeyFound.Should().BeTrue();
        inspection.CurrentValue.Should().Be("false");
    }

    [Fact]
    public void Every_real_key_path_is_listed_for_a_missed_key()
    {
        var outcome = _editor.Apply(Sample, "database.missing", "x");

        outcome.Ok.Should().BeFalse();
        outcome.KeyPaths.Should().Contain("database.host");
        outcome.KeyPaths.Should().Contain("cache.host");
        outcome.KeyPaths.Should().Contain("debug");
    }

    [Fact]
    public void Format_is_Ini()
    {
        _editor.Format.Should().Be(ConfigFileFormat.Ini);
    }
}
