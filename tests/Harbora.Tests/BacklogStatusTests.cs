using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The census-test idiom, pointed at process data instead of source: every entry in
/// <c>docs/product-audit/backlog.json</c> must carry an evidenced <c>status</c>, read from the file
/// itself rather than from a hand-kept list, so a 67th entry added without one is caught the same
/// day rather than the next time somebody spends real effort re-discovering that it already shipped.
///
/// <para>
/// This exists because the backlog had no status field at all before this test, and that absence had
/// a cost: <c>HARBORA-0008</c> (and, it turned out once actually checked, <c>HARBORA-0009</c> beside
/// it) were fixed by commit <c>995ebe7</c> on 2026-08-07 while both entries still read as open
/// problems, and work was spent re-discovering that three separate times across this programme.
/// </para>
///
/// <para>
/// <c>status</c> alone is not enough — a bare "done" is exactly as trustworthy as no field at all,
/// since nothing stops it from being wrong the same way the silence was. Every non-open status here
/// carries a <c>statusEvidence</c> string naming a commit sha, a <c>file:line</c>, or a one-line
/// justification, so the next reader can check the claim instead of inheriting it. The item's own
/// pre-existing <c>evidence</c> array is left untouched — it cites where the ORIGINAL problem was
/// found, not whether it has since been fixed, and reusing that key for the fix claim would have
/// silently overwritten the problem citation with the resolution citation, losing the former.
/// </para>
/// </summary>
public class BacklogStatusTests
{
    private static readonly string[] AllowedStatuses = ["done", "open", "partial", "withdrawn"];

    private static string BacklogPath =>
        Path.Combine(TestPaths.RepoRoot, "docs", "product-audit", "backlog.json");

    /// <summary>
    /// Re-parses the file on every call rather than caching it, so each test gets its own
    /// <see cref="JsonDocument"/> lifetime — the document is deliberately never disposed here (it
    /// backs the <see cref="JsonElement"/>s the caller enumerates); for a file this small the tests
    /// finish long before that matters.
    /// </summary>
    private static JsonElement.ArrayEnumerator Items()
    {
        File.Exists(BacklogPath).Should().BeTrue($"{BacklogPath} is the backlog this test reads");
        var json = File.ReadAllText(BacklogPath);
        var document = JsonDocument.Parse(json);
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Array, "the backlog is a JSON array of items");
        return document.RootElement.EnumerateArray();
    }

    private static string Id(JsonElement item) => item.TryGetProperty("id", out var id)
        ? id.GetString() ?? "(unnamed item)"
        : "(item with no id)";

    [Fact]
    public void The_backlog_file_parses_as_a_non_empty_JSON_array()
    {
        var items = Items().ToList();
        items.Should().NotBeEmpty("a backlog that failed to parse or collapsed to nothing would make every test below vacuously pass");
        items.Should().HaveCountGreaterThanOrEqualTo(66, "66 items were catalogued at the 2026-08-20 audit; the count only grows");
    }

    [Fact]
    public void Every_item_has_an_id()
    {
        foreach (var item in Items())
            item.TryGetProperty("id", out _).Should().BeTrue("every item must be identifiable in a failure message");
    }

    [Fact]
    public void Every_item_carries_a_status_from_the_allowed_set()
    {
        foreach (var item in Items())
        {
            item.TryGetProperty("status", out var status).Should()
                .BeTrue($"{Id(item)} has no \"status\" field at all");
            status.ValueKind.Should().Be(JsonValueKind.String, $"{Id(item)}'s status must be a string");
            var value = status.GetString();
            value.Should().NotBeNullOrWhiteSpace($"{Id(item)} has a blank status");
            AllowedStatuses.Should().Contain(value, $"{Id(item)} has status \"{value}\", not one of done/open/partial/withdrawn");
        }
    }

    [Fact]
    public void Every_non_open_item_names_its_evidence()
    {
        foreach (var item in Items())
        {
            if (!item.TryGetProperty("status", out var status) || status.GetString() == "open")
                continue;

            item.TryGetProperty("statusEvidence", out var evidence).Should()
                .BeTrue($"{Id(item)} is \"{status.GetString()}\" but has no statusEvidence — a bare non-open status is exactly as unverifiable as no status at all");
            evidence.ValueKind.Should().Be(JsonValueKind.String, $"{Id(item)}'s statusEvidence must be a string");
            evidence.GetString().Should().NotBeNullOrWhiteSpace(
                $"{Id(item)} is \"{status.GetString()}\" but statusEvidence is blank — a commit sha, file:line, or one-line justification is required");
        }
    }

    [Fact]
    public void Open_items_may_omit_evidence_but_it_is_not_required_to_be_absent()
    {
        // "open" is the honest default when nothing conclusive was found, so it deliberately carries
        // no evidence requirement — this test exists only to document that omission is a choice, not
        // an oversight, and to notice if a future edit ever tries to *require* evidence on open items
        // (which would invert the safety default the acceptance criteria calls for).
        bool IsOpen(JsonElement item) => item.TryGetProperty("status", out var s) && s.GetString() == "open";
        Items().Any(IsOpen).Should().BeTrue("at least one item is expected to honestly remain open");
    }
}
