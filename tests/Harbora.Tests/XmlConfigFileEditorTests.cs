using FluentAssertions;
using Harbora.Domain.Configuration;
using Harbora.Infrastructure.Configuration;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// C2 (2026-08-22 config-delivery plan): <see cref="XmlConfigFileEditor"/> — classic .NET
/// <c>web.config</c>/<c>app.config</c>, keyed by real XPath rather than an invented syntax.
/// </summary>
public class XmlConfigFileEditorTests
{
    private readonly XmlConfigFileEditor _editor = new();

    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <!-- connection strings -->
          <connectionStrings>
            <add name="Default" connectionString="REPLACE_ME" providerName="System.Data.SqlClient" />
            <add name="Legacy" connectionString="Server=old" />
          </connectionStrings>
          <appSettings>
            <add key="Env" value="Production" />
          </appSettings>
        </configuration>
        """;

    [Fact]
    public void Replacing_an_attribute_by_xpath_leaves_the_rest_of_the_document_untouched()
    {
        var outcome = _editor.Apply(
            Sample, "/configuration/connectionStrings/add[@name='Default']/@connectionString",
            "Server=db;Password=s3cr3t");

        outcome.Ok.Should().BeTrue();
        outcome.NewContent.Should().Contain("Server=db;Password=s3cr3t");
        outcome.NewContent.Should().Contain("<!-- connection strings -->");
        outcome.NewContent.Should().Contain("Server=old");
        outcome.NewContent.Should().Contain("providerName=\"System.Data.SqlClient\"");
        outcome.NewContent.Should().Contain("value=\"Production\"");
    }

    [Fact]
    public void The_current_value_of_an_attribute_is_readable_before_any_change()
    {
        var inspection = _editor.Inspect(
            Sample, "/configuration/appSettings/add[@key='Env']/@value");

        inspection.KeyFound.Should().BeTrue();
        inspection.CurrentValue.Should().Be("Production");
    }

    [Fact]
    public void Every_real_attribute_path_is_listed_for_a_missed_key()
    {
        var outcome = _editor.Apply(
            Sample, "/configuration/connectionStrings/add[@name='Missing']/@connectionString", "x");

        outcome.Ok.Should().BeFalse();
        outcome.KeyPaths.Should().Contain(p => p.Contains("Default") && p.EndsWith("@connectionString"));
        // appSettings has only one <add>, so it is not discriminated by @key — an un-ambiguous path
        // needs no predicate, unlike connectionStrings' two <add> siblings above.
        outcome.KeyPaths.Should().Contain("/configuration/appSettings/add/@value");
    }

    [Fact]
    public void Malformed_xml_reports_the_parsers_own_line()
    {
        const string broken = "<configuration><connectionStrings></configuration>";

        var outcome = _editor.Apply(broken, "/configuration/connectionStrings/@x", "y");

        outcome.Ok.Should().BeFalse();
        outcome.ParseError.Should().NotBeNull();
    }

    [Fact]
    public void An_invalid_xpath_expression_is_a_named_failure_not_a_crash()
    {
        var outcome = _editor.Apply(Sample, "((not xpath", "y");

        outcome.Ok.Should().BeFalse();
        outcome.ParseError.Should().NotBeNull();
    }

    [Fact]
    public void Format_is_Xml()
    {
        _editor.Format.Should().Be(ConfigFileFormat.Xml);
    }
}
