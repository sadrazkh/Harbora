using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="DotEnvFile"/> — the format <c>harbora env pull</c> writes. Covers the two guarantees the
/// task brief names directly: secrets are marked (never folded into the value itself, so the file
/// stays plain <c>KEY=VALUE</c>), and a diff against an existing file never prints a secret's actual
/// value.
/// </summary>
public class DotEnvFileTests
{
    private static EffectiveEnvEntry Entry(string key, string value, bool secret = false, string source = "App") =>
        new(key, value, secret, source);

    [Fact]
    public void A_secret_entry_gets_a_SECRET_comment_naming_its_source_directly_above_it()
    {
        var rendered = DotEnvFile.Render("blog", "https://panel.example.com",
            [Entry("DATABASE_URL", "postgres://user:pass@host/db", secret: true, source: "Database: orders")]);

        rendered.Should().Contain("# SECRET (from Database: orders)\nDATABASE_URL=");
    }

    [Fact]
    public void A_non_secret_entry_gets_no_SECRET_comment()
    {
        var rendered = DotEnvFile.Render("blog", "https://panel.example.com",
            [Entry("API_BASE", "https://api.example.com")]);

        // The file's own explanatory header mentions the word "SECRET" (it defines what the marker
        // means), so the check has to be for the per-entry marker specifically, not the bare word.
        rendered.Should().NotContain("# SECRET (from");
        rendered.Should().Contain("API_BASE=https://api.example.com");
    }

    [Fact]
    public void Plain_values_are_written_unquoted()
    {
        var rendered = DotEnvFile.Render("blog", "https://panel.example.com", [Entry("PORT", "8080")]);

        rendered.Should().Contain("PORT=8080\n");
        rendered.Should().NotContain("PORT=\"8080\"");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has#hash")]
    [InlineData("")]
    public void Values_needing_it_are_quoted_and_round_trip_through_Parse(string value)
    {
        var rendered = DotEnvFile.Render("blog", "https://panel.example.com", [Entry("KEY", value)]);

        var parsed = DotEnvFile.Parse(rendered);

        parsed.Should().ContainKey("KEY").WhoseValue.Should().Be(value);
    }

    [Fact]
    public void A_value_with_a_literal_quote_round_trips()
    {
        var rendered = DotEnvFile.Render("blog", "https://panel.example.com", [Entry("KEY", "say \"hi\"")]);

        DotEnvFile.Parse(rendered).Should().ContainKey("KEY").WhoseValue.Should().Be("say \"hi\"");
    }

    [Fact]
    public void Parse_ignores_comments_and_blank_lines_including_the_SECRET_marker()
    {
        var content = """
            # a leading comment

            # SECRET (from App)
            KEY=value

            """;

        var parsed = DotEnvFile.Parse(content);

        parsed.Should().ContainSingle().Which.Key.Should().Be("KEY");
    }

    [Fact]
    public void Diff_reports_added_removed_and_changed_keys_by_name_only()
    {
        var before = DotEnvFile.Render("blog", "https://panel.example.com",
            [Entry("KEEP", "same"), Entry("CHANGE", "old-value"), Entry("REMOVE", "gone")]);
        var after = DotEnvFile.Render("blog", "https://panel.example.com",
            [Entry("KEEP", "same"), Entry("CHANGE", "new-value"), Entry("ADD", "new")]);

        var diff = DotEnvFile.Diff(before, after);

        diff.Should().BeEquivalentTo(["+ ADD", "- REMOVE", "~ CHANGE"]);
    }

    [Fact]
    public void Diff_never_prints_a_changed_secrets_actual_value()
    {
        var before = DotEnvFile.Render("blog", "https://panel.example.com",
            [Entry("DB_PASSWORD", "old-super-secret-1", secret: true, source: "App")]);
        var after = DotEnvFile.Render("blog", "https://panel.example.com",
            [Entry("DB_PASSWORD", "new-super-secret-2", secret: true, source: "App")]);

        var diff = DotEnvFile.Diff(before, after);

        diff.Should().ContainSingle().Which.Should().Be("~ DB_PASSWORD");
        string.Join(" ", diff).Should()
            .NotContain("old-super-secret-1").And.NotContain("new-super-secret-2");
    }

    [Fact]
    public void Diff_is_empty_when_nothing_would_change()
    {
        var rendered = DotEnvFile.Render("blog", "https://panel.example.com",
            [Entry("KEY", "value"), Entry("SECRET_KEY", "s3cret", secret: true, source: "App")]);

        DotEnvFile.Diff(rendered, rendered).Should().BeEmpty();
    }

    [Fact]
    public void Rendered_file_names_the_app_and_server_it_came_from()
    {
        var rendered = DotEnvFile.Render("my-api", "https://panel.example.com", []);

        rendered.Should().Contain("my-api").And.Contain("https://panel.example.com");
    }
}
