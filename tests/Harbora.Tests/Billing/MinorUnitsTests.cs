using System.Globalization;
using FluentAssertions;
using Harbora.Web.Infrastructure;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// The boundary money crosses on its way to and from a person.
///
/// <para>
/// Everything behind this class counts whole minor units in a <c>long</c>, which is the reason a
/// balance is checkable. Both places that stops being true are here — the box an administrator types
/// a top-up into, and the figure a customer reads off their bill — so the mistakes that live in
/// those two conversions live here too, and nowhere else.
/// </para>
/// </summary>
public class MinorUnitsTests
{
    [Theory]
    [InlineData(0, "0.00")]
    [InlineData(1, "0.01")]
    [InlineData(100, "1.00")]
    [InlineData(123_456, "1,234.56")]
    [InlineData(-500_000, "-5,000.00")]
    public void An_amount_reads_as_major_units_with_the_sign_the_ledger_gave_it(long minor, string expected)
    {
        // Signed as stored. Turning a charge positive on the way to the screen would mean the bill
        // and the balance describe the same movement with opposite signs, and the customer adding
        // the column up gets an answer that is not their balance.
        MinorUnits.Format(minor).Should().Be(expected);
    }

    [Fact]
    public void An_amount_reads_the_same_however_the_reader_writes_their_numbers()
    {
        // The bill is bilingual and its figures sit in LTR islands. A number that changed shape with
        // the ambient culture would make the same statement unreconcilable against itself in two
        // languages, and against the bank statement beside it — which is the one thing a bill may
        // not do. Every other number on this panel is interpolated and therefore does follow the
        // culture; this is the exception, and it only holds while nothing here reaches for it.
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fa-IR");
            MinorUnits.Format(123_456).Should().Be("1,234.56");
            MinorUnits.TryParseMajor("1,234.56", out var minor).Should().BeTrue();
            minor.Should().Be(123_456);
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    [Theory]
    [InlineData("100", 10_000)]
    [InlineData("100.5", 10_050)]
    [InlineData("1,234.56", 123_456)]
    [InlineData("  50  ", 5_000)]
    [InlineData("0.01", 1)]
    public void What_somebody_typed_becomes_whole_minor_units(string typed, long expected)
    {
        MinorUnits.TryParseMajor(typed, out var minor).Should().BeTrue();
        minor.Should().Be(expected);
    }

    [Fact]
    public void A_third_decimal_place_is_rounded_away_from_zero_rather_than_to_even()
    {
        // Banker's rounding is right for a long run of arithmetic and wrong for one figure a person
        // typed and expects to see handed straight back to them.
        MinorUnits.TryParseMajor("1.005", out var up).Should().BeTrue();
        up.Should().Be(101);

        MinorUnits.TryParseMajor("1.015", out var alsoUp).Should().BeTrue();
        alsoUp.Should().Be(102);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("free")]
    [InlineData("1e6")]
    [InlineData("۱۰۰")]
    public void Something_that_is_not_a_number_is_refused_rather_than_read_as_nothing(string typed)
    {
        // Zero is the dangerous answer here, not false: a parser that returned zero for a typo would
        // write a credit of nothing, or — on a form that treats zero as "leave it alone" — silently
        // do nothing at all while the page reported a top-up.
        MinorUnits.TryParseMajor(typed, out var minor).Should().BeFalse();
        minor.Should().Be(0);
    }

    [Fact]
    public void An_amount_too_large_to_hold_is_refused_rather_than_wrapped()
    {
        // This project compiles unchecked. Without the bound the multiplication wraps to a large
        // negative, and a credit becomes a charge with nothing raised and nothing logged.
        MinorUnits.TryParseMajor("100000000000000000000", out _).Should().BeFalse();
    }
}
