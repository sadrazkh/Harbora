using System.Globalization;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// The one place minor units become something a person reads, and the one place something a person
/// typed becomes minor units.
///
/// <para>
/// Money is a <see cref="long"/> count of minor units everywhere behind this class — never
/// <see cref="decimal"/>, never a floating type — because a balance that can be a fraction of a
/// minor unit is a balance two readers can disagree about. <see cref="decimal"/> appears here and
/// nowhere else: this is the view boundary the rule allows it at, and it is used for exactly one
/// multiplication and one division.
/// </para>
///
/// <para>
/// <b>One hundred minor units to the major one, for every currency this install has.</b> That is a
/// simplification and it is written down rather than assumed: <c>Wallet.Currency</c> is one code per
/// install, and no currency with a different exponent is offered. A second exponent arriving means
/// this class grows a table, not that every call site starts dividing by its own constant.
/// </para>
/// </summary>
public static class MinorUnits
{
    /// <summary>Minor units in one major unit.</summary>
    public const long PerMajor = 100;

    /// <summary>
    /// The largest amount a person may type into a credit box, in major units.
    ///
    /// <para>
    /// Not a policy about how rich anybody is: it is the bound that keeps the multiplication below
    /// away from <see cref="long.MaxValue"/>. This project compiles unchecked, so an overflow there
    /// would not throw — it would wrap to a large negative and turn a credit into a charge, which is
    /// worse than a refusal because nothing reports it.
    /// </para>
    /// </summary>
    public const decimal MaxMajor = 1_000_000_000_000m;

    /// <summary>
    /// An amount, written out for a reader. Signed the way the ledger stores it, so money leaving
    /// the wallet reads with a minus in front of it rather than being quietly turned positive
    /// somewhere between the balance and the bill.
    ///
    /// <para>
    /// Grouped and always to two places. Always, because a column in which some rows have decimals
    /// and others do not is a column nobody can add up by eye — which is the one thing a customer
    /// does with a bill.
    /// </para>
    /// </summary>
    /// <para>
    /// <b>Invariant, whatever culture the request is being read in.</b> Every other number on this
    /// panel is written with an interpolated string and so follows the ambient culture, and for a
    /// megabyte or a core count that is right. It is not right here: a bill has to reconcile
    /// digit-for-digit against a bank statement and against the same bill opened in the other
    /// language, and Persian digits with a different group separator make two documents out of one.
    /// The figures already sit in LTR islands on both screens, which is where the RTL rules put a
    /// number that must not be re-shaped.
    /// </para>
    public static string Format(long minor) =>
        (minor / (decimal)PerMajor).ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>The same, with the install's currency code after it.</summary>
    public static string Format(long minor, string currency) => $"{Format(minor)} {currency}";

    /// <summary>
    /// Reads what somebody typed into a money box.
    ///
    /// <para>
    /// Invariant, and only invariant. A form is posted with whatever the browser's locale did to the
    /// keystrokes, and "1,5" is one and a half in one place and fifteen in another — a parser that
    /// followed the request's culture would put a factor of ten between two administrators typing
    /// the same thing. Thousands separators are allowed because people type them; a decimal comma is
    /// not, because guessing which one it was is exactly the ambiguity being refused.
    /// </para>
    ///
    /// <para>
    /// Rounds away from zero at the half, rather than to even. Banker's rounding is right for a long
    /// run of arithmetic and wrong for a single figure a person typed and expects to see back.
    /// </para>
    /// </summary>
    public static bool TryParseMajor(string? text, out long minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        const NumberStyles styles =
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint |
            NumberStyles.AllowThousands | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;

        if (!decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out var major)) return false;
        if (Math.Abs(major) > MaxMajor) return false;

        minor = (long)Math.Round(major * PerMajor, MidpointRounding.AwayFromZero);
        return true;
    }

    /// <summary>
    /// Reads a price box on an administration form, where leaving it empty is an answer of its own.
    ///
    /// <para>
    /// <b>An empty box is accepted and yields <c>null</c>, not <c>0</c>.</b> That is the whole
    /// difference between "this resource is deliberately free" and "no human has priced it yet",
    /// and the two want opposite responses: a zero is a line worth no money, an unset rate is an
    /// operator who has to be told. A form that wrote zero for an empty box would destroy the
    /// distinction every rate column on <c>Plan</c> and <c>InstanceSize</c> is nullable to keep.
    /// </para>
    ///
    /// <para>
    /// Refuses only what it cannot read. A negative figure parses here and is handed back as a
    /// negative — the caller refuses it and says so, because "that is not a number" and "a price
    /// cannot be negative" send an administrator to two different corrections, and one refusal
    /// covering both is the shape that makes somebody guess.
    /// </para>
    /// </summary>
    public static bool TryParseRate(string? text, out long? minor)
    {
        minor = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        if (!TryParseMajor(text, out var parsed)) return false;

        minor = parsed;
        return true;
    }

    /// <summary>
    /// A rate as it goes back into a form box: empty for a rate nobody has set, and <b>ungrouped</b>.
    ///
    /// <para>
    /// Not <see cref="Format(long)"/>, which groups. A number box whose value carries thousands
    /// separators is a value the input refuses, and a browser that refuses its own initial value
    /// renders the box empty — so an administrator who came to change a cap saves the form and
    /// silently un-prices the plan, which reads on every later tick as a resource nobody has
    /// priced. Invariant for the same reason the parser is: this string is posted straight back
    /// into <see cref="TryParseRate"/>, and a round trip through two cultures is where a factor of
    /// ten comes from.
    /// </para>
    /// </summary>
    public static string Box(long? minor) => minor is { } value
        ? (value / (decimal)PerMajor).ToString("0.00", CultureInfo.InvariantCulture)
        : string.Empty;
}
