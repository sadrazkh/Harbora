using System.Text;
using System.Text.Json;
using FluentAssertions;
using Harbora.Domain.Auditing;
using Harbora.Web.Infrastructure;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="AuditExportWriter"/>: turning the rows a caller's query already selected and bounded
/// into a CSV or JSON file. <see cref="AuditExportTests"/> already proves <c>CsvWriter.Field</c>'s
/// low-level escaping (commas, quotes, newlines, formula injection); this file proves the two things
/// that are new here — that a full row built from those fields round-trips correctly, and that a
/// truncated export says so inside the file rather than just ending short.
///
/// Deliberately a plain unit test over five or so fake rows rather than an HTTP test over fifty
/// thousand real ones: <c>AuditExportWriter</c> is a pure function of <c>(entries, totalMatching,
/// maxExportRows)</c> exactly so the truncation path can be proven this way. Reaching the production
/// bound (50,000 rows) through the real workspace-scoped query would need a database seeded past
/// that count, which is not a reasonable thing to build for one boundary condition — the wiring that
/// hands this class its real <c>totalMatching</c> and <c>maxExportRows</c> is covered instead by
/// <c>WorkspaceAuditLogExportHttpTests</c> at ordinary, unbounded row counts.
/// </summary>
public class AuditExportWriterTests
{
    private static AuditLog Row(string actorEmail, string action, string? targetId = "abc123", string? ip = "198.51.100.9") =>
        new()
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero),
            ActorEmail = actorEmail,
            Action = action,
            TargetType = "app",
            TargetId = targetId,
            IpAddress = ip
        };

    [Fact]
    public void Csv_starts_with_a_utf8_bom_so_excel_reads_persian_text_correctly()
    {
        var bytes = AuditExportWriter.Csv([Row("a@example.com", "app.deploy")], totalMatching: 1, maxExportRows: 100);

        bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble(),
            "Excel guesses the wrong codepage for non-ASCII text without an explicit BOM");
    }

    [Fact]
    public void Csv_lists_every_row_and_nothing_is_dropped_when_under_the_bound()
    {
        var rows = new[] { Row("a@example.com", "app.deploy"), Row("b@example.com", "app.delete") };
        var text = TextOf(AuditExportWriter.Csv(rows, totalMatching: 2, maxExportRows: 100));

        text.Should().Contain("app.deploy").And.Contain("app.delete");
        text.Should().NotContain("TRUNCATED",
            "a file that is not actually truncated must not claim to be");
    }

    [Fact]
    public void Csv_round_trips_a_field_containing_a_comma_a_quote_and_a_newline_together()
    {
        // One value carrying all three special characters at once, exactly the combination that
        // would corrupt a naively-built CSV: the comma would add a column, the quote would end the
        // field early, and the newline would start a new row.
        const string awkward = "Vahid, \"the ops guy\"\nsecond line";
        var text = TextOf(AuditExportWriter.Csv([Row(awkward, "app.deploy")], totalMatching: 1, maxExportRows: 100));

        // CsvWriter.Field's own contract: wrap in quotes, double the embedded quotes, leave commas
        // and newlines untouched inside the quoted span. Presence of this exact escaped fragment is
        // proof the field survived the trip through AuditExportWriter as a single value.
        text.Should().Contain("\"Vahid, \"\"the ops guy\"\"\nsecond line\"");
    }

    [Fact]
    public void Csv_says_nothing_about_truncation_when_the_bound_was_not_reached()
    {
        var text = TextOf(AuditExportWriter.Csv([Row("a@example.com", "app.deploy")], totalMatching: 1, maxExportRows: 1));

        text.Should().NotContain("TRUNCATED");
    }

    [Fact]
    public void Csv_announces_truncation_as_a_padded_comment_row_rather_than_ending_silently()
    {
        var rows = new[] { Row("a@example.com", "app.deploy") };
        var text = TextOf(AuditExportWriter.Csv(rows, totalMatching: 500, maxExportRows: 1));

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines[0].Should().StartWith("\"# TRUNCATED",
            "the first line must be a comment row, not mistakeable for a real audit entry");
        lines[0].Should().Contain("1").And.Contain("500");
        // Padded to the same seven columns as a data row (one quoted note field, then six empty
        // ones) so a strict reader does not choke on a ragged file. The note text itself contains
        // commas, so this checks the trailing padding rather than naively splitting on ',' — a real
        // CSV parser would see 1 quoted field + 6 empty fields here, matching the header's 7 columns.
        lines[0].Should().EndWith("\",,,,,,");
        lines[1].Should().Be("id,timestamp,actor,action,targetType,targetId,ipAddress",
            "the header still comes right after the notice");
    }

    [Fact]
    public void Json_reports_truncated_false_and_no_note_when_under_the_bound()
    {
        var payload = JsonOf(AuditExportWriter.Json([Row("a@example.com", "app.deploy")], totalMatching: 1, maxExportRows: 100));

        payload.GetProperty("truncated").GetBoolean().Should().BeFalse();
        payload.GetProperty("truncationNote").ValueKind.Should().Be(JsonValueKind.Null);
        payload.GetProperty("totalMatchingRows").GetInt32().Should().Be(1);
        payload.GetProperty("returnedRows").GetInt32().Should().Be(1);
    }

    [Fact]
    public void Json_reports_truncated_true_with_both_counts_when_over_the_bound()
    {
        var rows = new[] { Row("a@example.com", "app.deploy") };
        var payload = JsonOf(AuditExportWriter.Json(rows, totalMatching: 500, maxExportRows: 1));

        payload.GetProperty("truncated").GetBoolean().Should().BeTrue();
        payload.GetProperty("totalMatchingRows").GetInt32().Should().Be(500);
        payload.GetProperty("returnedRows").GetInt32().Should().Be(1);
        payload.GetProperty("truncationNote").GetString().Should()
            .Contain("1").And.Contain("500",
                "a reader who only looks at the JSON, not the HTTP response, must still be told how much was left out");
    }

    [Fact]
    public void Json_entries_carry_the_fields_the_page_shows()
    {
        var row = Row("a@example.com", "app.deploy", targetId: "app-123", ip: "198.51.100.9");
        var payload = JsonOf(AuditExportWriter.Json([row], totalMatching: 1, maxExportRows: 100));

        var entry = payload.GetProperty("entries")[0];
        entry.GetProperty("id").GetGuid().Should().Be(row.Id);
        entry.GetProperty("actorEmail").GetString().Should().Be("a@example.com");
        entry.GetProperty("action").GetString().Should().Be("app.deploy");
        entry.GetProperty("targetType").GetString().Should().Be("app");
        entry.GetProperty("targetId").GetString().Should().Be("app-123");
        entry.GetProperty("ipAddress").GetString().Should().Be("198.51.100.9");
    }

    private static string TextOf(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes.Skip(3).ToArray()); // past the BOM

    private static JsonElement JsonOf(byte[] bytes) =>
        JsonDocument.Parse(bytes).RootElement;
}
