using System.Globalization;
using System.Text.RegularExpressions;

namespace Harbora.Infrastructure.Design;

/// <summary>
/// WCAG relative luminance and contrast, so the palette can be checked rather than admired.
///
/// Colours arrive as the <c>"R G B"</c> triples the stylesheet stores, because Tailwind needs that
/// form for its alpha-value trick and a second representation would be a second thing to keep in
/// step.
/// </summary>
public static class ColorContrast
{
    /// <summary>Contrast ratio between two colours, from 1 (identical) to 21 (black on white).</summary>
    public static double Ratio(string a, string b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);

        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>WCAG relative luminance of an <c>"R G B"</c> triple or a <c>#rrggbb</c> string.</summary>
    public static double RelativeLuminance(string color)
    {
        var (r, g, b) = Parse(color);
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    /// <summary>The sRGB → linear transfer function. The 0.03928 knee is part of the standard.</summary>
    private static double Channel(int value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static (int R, int G, int B) Parse(string color)
    {
        var text = color.Trim();

        if (text.StartsWith('#'))
        {
            var hex = text[1..];
            if (hex.Length != 6) throw new FormatException($"Not a #rrggbb colour: '{color}'.");
            return (Hex(hex[..2]), Hex(hex[2..4]), Hex(hex[4..]));
        }

        var parts = Regex.Split(text, @"[\s,]+");
        if (parts.Length != 3) throw new FormatException($"Not an 'R G B' colour: '{color}'.");

        return (Byte(parts[0]), Byte(parts[1]), Byte(parts[2]));
    }

    private static int Hex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static int Byte(string s) => int.Parse(s, CultureInfo.InvariantCulture);
}
