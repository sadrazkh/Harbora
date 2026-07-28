using FluentAssertions;
using Harbora.Web.Infrastructure;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// CSV export of the audit trail. Audit fields carry attacker-influenced text — an actor email, a
/// target id someone chose — and the export is opened in a spreadsheet by an administrator
/// investigating an incident. That is the worst possible moment for a logged value to execute.
/// </summary>
public class AuditExportTests
{
    [Fact]
    public void Fields_are_quoted()
    {
        CsvWriter.Field("app.deploy").Should().Be("\"app.deploy\"");
    }

    [Fact]
    public void Embedded_quotes_are_escaped()
    {
        CsvWriter.Field("say \"hello\"").Should().Be("\"say \"\"hello\"\"\"");
    }

    [Fact]
    public void A_comma_does_not_break_the_column_layout()
    {
        // Unescaped, this would shift every later column and misattribute the entry.
        CsvWriter.Field("a,b,c").Should().Be("\"a,b,c\"");
    }

    [Fact]
    public void A_newline_stays_inside_its_field()
    {
        CsvWriter.Field("line1\nline2").Should().Be("\"line1\nline2\"");
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1234")]
    [InlineData("-cmd")]
    [InlineData("@SUM(A1)")]
    public void Formula_injection_is_neutralised(string dangerous)
    {
        // Classic CSV injection: =HYPERLINK / =cmd|'...' execute on open in Excel.
        var result = CsvWriter.Field(dangerous);

        result.Should().StartWith("\"'", "a leading apostrophe forces the spreadsheet to treat it as text");
        result.Should().Contain(dangerous);
    }

    [Fact]
    public void A_dangerous_looking_email_is_still_readable_afterwards()
    {
        CsvWriter.Field("=attacker@example.com").Should().Be("\"'=attacker@example.com\"");
    }

    [Fact]
    public void An_ordinary_value_is_not_mangled()
    {
        CsvWriter.Field("admin@example.com").Should().Be("\"admin@example.com\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_values_become_an_empty_field(string? value)
    {
        CsvWriter.Field(value).Should().Be("\"\"");
    }
}
