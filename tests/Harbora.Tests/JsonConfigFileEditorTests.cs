using FluentAssertions;
using Harbora.Domain.Configuration;
using Harbora.Infrastructure.Configuration;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// C2 (2026-08-22 config-delivery plan): <see cref="JsonConfigFileEditor"/> is what lets the panel
/// replace a value inside <c>appsettings.json</c> at deploy time. These tests prove the two
/// properties the owner made binding: the round trip keeps everything else recognisable, and a
/// failure names exactly what went wrong.
/// </summary>
public class JsonConfigFileEditorTests
{
    private readonly JsonConfigFileEditor _editor = new();

    private const string Sample = """
        {
          "ConnectionStrings": {
            "Default": "REPLACE_ME",
            "Legacy": "Server=old"
          },
          "Logging": {
            "LogLevel": {
              "Default": "Information"
            }
          },
          "AllowedHosts": "*"
        }
        """;

    [Fact]
    public void Replacing_a_nested_value_leaves_every_other_value_untouched()
    {
        var outcome = _editor.Apply(Sample, "ConnectionStrings:Default", "Host=db;Password=secret");

        outcome.Ok.Should().BeTrue();
        outcome.NewContent.Should().Contain("\"Default\": \"Host=db;Password=secret\"");
        outcome.NewContent.Should().Contain("\"Legacy\": \"Server=old\"");
        outcome.NewContent.Should().Contain("\"AllowedHosts\": \"*\"");
        outcome.NewContent.Should().Contain("\"Default\": \"Information\"");
    }

    [Fact]
    public void The_colon_key_path_matches_ASPNET_Cores_own_configuration_binder_idiom()
    {
        var inspection = _editor.Inspect(Sample, "Logging:LogLevel:Default");

        inspection.KeyFound.Should().BeTrue();
        inspection.CurrentValue.Should().Be("Information");
    }

    [Fact]
    public void Every_real_key_path_is_listed_for_a_missed_key()
    {
        var outcome = _editor.Apply(Sample, "ConnectionStrings:Missing", "x");

        outcome.Ok.Should().BeFalse();
        outcome.KeyFound.Should().BeFalse();
        outcome.KeyPaths.Should().Contain("ConnectionStrings:Default");
        outcome.KeyPaths.Should().Contain("Logging:LogLevel:Default");
        outcome.KeyPaths.Should().Contain("AllowedHosts");
    }

    [Fact]
    public void A_broken_file_reports_the_parsers_own_line_and_column()
    {
        var broken = "{ \"ConnectionStrings\": { \"Default\": \"x\" ";

        var outcome = _editor.Apply(broken, "ConnectionStrings:Default", "y");

        outcome.Ok.Should().BeFalse();
        outcome.ParseError.Should().NotBeNull();
        outcome.ParseError!.Line.Should().NotBeNull();
    }

    [Fact]
    public void A_secret_value_never_appears_in_the_key_not_found_diagnostic()
    {
        var outcome = _editor.Apply(Sample, "ConnectionStrings:Missing", "Host=db;Password=super-secret");

        outcome.KeyPaths.Should().NotContain(p => p.Contains("super-secret"));
    }

    [Fact]
    public void Array_elements_are_addressable_by_index()
    {
        const string withArray = """{ "Servers": ["a", "b", "c"] }""";

        var outcome = _editor.Apply(withArray, "Servers:1", "b-replaced");

        outcome.Ok.Should().BeTrue();
        outcome.NewContent.Should().Contain("\"a\"");
        outcome.NewContent.Should().Contain("b-replaced");
        outcome.NewContent.Should().Contain("\"c\"");
    }

    [Fact]
    public void Format_is_Json()
    {
        _editor.Format.Should().Be(ConfigFileFormat.Json);
    }
}
