using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Web.Infrastructure;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// One way of writing a moment.
///
/// The views had six hand-typed formats between them — "yyyy-MM-dd HH:mm", "MM/dd HH:mm",
/// "MM-dd HH:mm" and friends — so the same record was stamped three different ways depending on
/// which page happened to show it. The Dates constants are now the only formats a view may use.
/// </summary>
public class DateFormattingTests
{
    private static readonly string ViewsRoot = Path.Combine(TestPaths.WebRoot, "Views");

    /// <summary>A quoted format containing date parts. Number formats ("0.##", "N0") have none.</summary>
    private static readonly Regex HandWritten = new(
        @"ToString\(""[^""]*(yyyy|MM|dd|HH)[^""]*""\)", RegexOptions.Compiled);

    [Fact]
    public void No_view_writes_a_date_format_by_hand()
    {
        var views = Directory.EnumerateFiles(ViewsRoot, "*.cshtml", SearchOption.AllDirectories).ToList();
        views.Should().HaveCountGreaterThan(30, "an empty scan proves nothing");

        foreach (var view in views)
        {
            var match = HandWritten.Match(File.ReadAllText(view));
            match.Success.Should().BeFalse(
                $"{Path.GetFileName(view)} formats a date with {match.Value} — use Dates.Stamp/Day/Precise");
        }
    }

    // --- the relative age the backups card shows ---

    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "همین حالا", "just now")]
    [InlineData(59, "59 دقیقه پیش", "59 min ago")]
    [InlineData(60, "1 ساعت پیش", "1 h ago")]
    // 19.2 hours — the exact value the card used to print as "ساعت 19.2".
    [InlineData(19 * 60 + 12, "19 ساعت پیش", "19 h ago")]
    [InlineData(47 * 60 + 59, "47 ساعت پیش", "47 h ago")]
    [InlineData(48 * 60, "2 روز پیش", "2 d ago")]
    // 60 hours: 2.5 days must floor to 2, not round to 3.
    [InlineData(60 * 60, "2 روز پیش", "2 d ago")]
    [InlineData(10 * 24 * 60, "10 روز پیش", "10 d ago")]
    public void An_age_reads_as_a_sentence(int minutesAgo, string fa, string en)
    {
        var moment = Now.AddMinutes(-minutesAgo);

        Dates.Ago(moment, Now, isFa: true).Should().Be(fa);
        Dates.Ago(moment, Now, isFa: false).Should().Be(en);
    }

    [Fact]
    public void A_moment_slightly_in_the_future_is_now_not_time_travel()
    {
        // Two writers with skewed clocks produce this; "-3 min ago" reads as a bug.
        Dates.Ago(Now.AddSeconds(30), Now, isFa: false).Should().Be("just now");
    }

    [Fact]
    public void The_floor_keeps_an_age_true_for_its_whole_unit()
    {
        // 1h31m rounded up would claim "2 h ago" half an hour early.
        Dates.Ago(Now.AddMinutes(-91), Now, isFa: false).Should().Be("1 h ago");
    }
}
