# Panel Design System and Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild Harbora's token layer, application shell, navigation and shared components to the
approved mockup language, without changing any page's behaviour.

**Architecture:** The existing CSS-variable token system is retuned rather than replaced, and a
semantic naming layer is added alongside the old `slate` aliases so current markup keeps working.
The shell becomes three regions (sidebar, topbar, optional right rail). Two decisions that fail
silently — what a metric panel prints when there is no data, and which navigation items a user may
see — are extracted into pure, tested, mutation-tested rule classes rather than living in Razor.

**Tech Stack:** .NET 10, ASP.NET Core MVC, Razor, Tailwind CSS 3 (Vite), xUnit + FluentAssertions.

## Global Constraints

- Persian is the default culture; every user-visible string ships in both `fa` and `en`.
- All spacing and positioning uses logical properties (`ms-`, `me-`, `start-`, `end-`) — never
  `ml-`/`mr-`/`left-`/`right-` — so RTL mirrors without per-view work.
- Class, method, entity and API names are English. Reports to the user are Persian.
- Light is the default theme; the existing `system`/`light`/`dark` toggle keeps working.
- No page's behaviour, route or controller changes in this plan. Views are touched in sub-project B.
- Never rewrite a working feature. The deploy engine and its views are not touched here.
- Contrast floors: normal text ≥ 4.5:1, tertiary/decorative text ≥ 3.0:1, in **both** themes.
- Unknown is never rendered as zero, an em dash, or a flat line.

---

### Task 1: Semantic design tokens

**Files:**
- Modify: `src/Harbora.Web/Scripts/app.css:10-38`
- Modify: `src/Harbora.Web/tailwind.config.js:11-36`
- Create: `src/Harbora.Infrastructure/Design/ColorContrast.cs`
- Test: `tests/Harbora.Tests/DesignTokenTests.cs`

**Interfaces:**
- Produces: `ColorContrast.Ratio(string hexA, string hexB) → double`,
  `ColorContrast.RelativeLuminance(string hex) → double`,
  `DesignTokens.Parse(string css) → IReadOnlyDictionary<string, IReadOnlyDictionary<string,string>>`
  keyed by theme (`"light"`, `"dark"`) then token name (`"--surface"`) to `"R G B"`.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Harbora.Infrastructure.Design;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The palette, checked rather than eyeballed.
///
/// Two values in the first draft of the design failed this test — tertiary text at 2.81 on white,
/// and the dark brand fill at 3.67 with white on it. Both looked fine in a mockup. Contrast is the
/// one part of visual design that is arithmetic, so it belongs in the suite and not in an opinion.
/// </summary>
public class DesignTokenTests
{
    private static readonly string Css =
        File.ReadAllText(Path.Combine(TestPaths.WebRoot, "Scripts", "app.css"));

    private static double Ratio(string theme, string a, string b)
    {
        var tokens = DesignTokens.Parse(Css)[theme];
        return ColorContrast.Ratio(tokens[a], tokens[b]);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Body_text_is_readable_on_every_surface(string theme)
    {
        Ratio(theme, "--text", "--surface").Should().BeGreaterThanOrEqualTo(4.5);
        Ratio(theme, "--text", "--canvas").Should().BeGreaterThanOrEqualTo(4.5);
        Ratio(theme, "--text", "--surface-2").Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Secondary_text_is_readable(string theme)
    {
        // Muted text carries most of the panel captions in the mockups. It is normal text, so it
        // gets the normal floor, not the decorative one.
        Ratio(theme, "--text-muted", "--surface").Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Tertiary_text_clears_the_decorative_floor(string theme)
    {
        Ratio(theme, "--text-faint", "--surface").Should().BeGreaterThanOrEqualTo(3.0);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void White_on_the_brand_fill_is_readable(string theme)
    {
        // Every primary button in the mockups is white on violet.
        var tokens = DesignTokens.Parse(Css)[theme];
        ColorContrast.Ratio("255 255 255", tokens["--brand"]).Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Brand_links_are_readable_on_a_surface(string theme)
    {
        // Deliberately a different token from the fill: the colour that works under white text is
        // not the colour that works as text on white.
        Ratio(theme, "--brand-text", "--surface").Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Fact]
    public void Both_themes_define_the_same_tokens()
    {
        // A token defined in one theme and forgotten in the other renders as an inherited value,
        // which is how a dark-mode page ends up with one white card on a black page.
        var parsed = DesignTokens.Parse(Css);

        parsed["dark"].Keys.Should().BeEquivalentTo(parsed["light"].Keys);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~DesignTokenTests"`
Expected: FAIL — `ColorContrast`, `DesignTokens` and `TestPaths` do not exist.

- [ ] **Step 3: Write the contrast and parser implementation**

Create `src/Harbora.Infrastructure/Design/ColorContrast.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace Harbora.Infrastructure.Design;

/// <summary>
/// WCAG relative luminance and contrast, so the palette can be checked rather than admired.
///
/// Colours arrive as the `"R G B"` triples the stylesheet stores, because Tailwind needs that form
/// for its alpha-value trick and a second representation would be a second thing to keep in step.
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

    /// <summary>WCAG relative luminance of an `"R G B"` triple or a `#rrggbb` string.</summary>
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
```

Create `src/Harbora.Infrastructure/Design/DesignTokens.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Harbora.Infrastructure.Design;

/// <summary>
/// Reads the theme blocks out of the stylesheet so the palette can be asserted against.
///
/// Parsing the real file rather than duplicating the values in C# is the entire point: a copy would
/// drift, and a test that passes against a copy of the palette proves nothing about the palette.
/// </summary>
public static class DesignTokens
{
    private static readonly Regex LightBlock = new(@":root\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
    private static readonly Regex DarkBlock = new(@"html\.dark\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
    private static readonly Regex Declaration = new(@"(?<name>--[a-z0-9-]+)\s*:\s*(?<value>[^;]+);", RegexOptions.IgnoreCase);

    /// <summary>Theme name → token name → raw value, exactly as written in the stylesheet.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Parse(string css) =>
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["light"] = Block(css, LightBlock),
            ["dark"] = Block(css, DarkBlock)
        };

    private static IReadOnlyDictionary<string, string> Block(string css, Regex block)
    {
        var match = block.Match(css);
        if (!match.Success) return new Dictionary<string, string>();

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match declaration in Declaration.Matches(match.Groups["body"].Value))
            tokens[declaration.Groups["name"].Value] = declaration.Groups["value"].Value.Trim();

        return tokens;
    }
}
```

Create `tests/Harbora.Tests/TestPaths.cs`:

```csharp
namespace Harbora.Tests;

/// <summary>Locating source files the tests read, from wherever the runner happens to start.</summary>
public static class TestPaths
{
    /// <summary>The Harbora.Web project directory.</summary>
    public static string WebRoot { get; } = Find();

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Harbora.Web");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/Harbora.Web from the test output directory.");
    }
}
```

- [ ] **Step 4: Add the tokens to the stylesheet**

Replace the `@layer base` block in `src/Harbora.Web/Scripts/app.css` (lines 10-38) with:

```css
@layer base {
  /*
   * Two naming layers on purpose.
   *
   * The `--sNNN` ramp is what every existing view already renders through, remapped onto Tailwind's
   * `slate` scale. Its light values are retuned here to the warm, violet-tinted neutrals of the new
   * design, so pages move most of the way to the target without being edited.
   *
   * The semantic tokens below are what new markup uses. They say what a colour is for rather than
   * how dark it is, which is the only way `bg-slate-900` meaning "a white card" ever stops being a
   * sentence somebody has to explain.
   */
  :root {
    color-scheme: light;

    /* Compatibility ramp — inverted, so existing markup lands close to the new design. */
    --s50:  26 21 35;
    --s100: 26 21 35;
    --s200: 45 39 58;
    --s300: 61 56 76;
    --s400: 107 104 128;
    --s500: 139 135 153;
    --s600: 176 172 190;
    --s700: 226 223 238;
    --s800: 244 243 250;
    --s900: 255 255 255;
    --s950: 248 247 253;

    /* Semantic tokens — what new markup uses. */
    --canvas:        248 247 253;
    --surface:       255 255 255;
    --surface-2:     250 250 252;
    --border:        239 237 245;
    --border-strong: 226 223 238;
    --text:          26 21 35;
    --text-muted:    107 104 128;
    --text-faint:    139 135 153;
    --brand:         109 74 255;
    --brand-hover:   91 55 232;
    --brand-text:    91 55 232;
    --brand-soft:    241 237 255;

    --ok:    22 163 74;   --ok-soft:    220 252 231;
    --warn:  217 119 6;   --warn-soft:  254 243 199;
    --error: 220 38 38;   --error-soft: 254 226 226;
    --info:  37 99 235;   --info-soft:  219 234 254;
    --idle:  107 114 128; --idle-soft:  243 244 246;
  }

  html.dark {
    color-scheme: dark;

    --s50: 248 250 252;
    --s100: 241 245 249;
    --s200: 226 232 240;
    --s300: 203 213 225;
    --s400: 169 164 188;
    --s500: 117 112 138;
    --s600: 71 85 105;
    --s700: 38 34 49;
    --s800: 30 26 41;
    --s900: 23 20 31;
    --s950: 15 13 23;

    --canvas:        15 13 23;
    --surface:       23 20 31;
    --surface-2:     30 26 41;
    --border:        38 34 49;
    --border-strong: 51 46 66;
    --text:          236 234 242;
    --text-muted:    169 164 188;
    --text-faint:    117 112 138;
    /* The fill stays the light-mode violet: the lighter tint measures 3.67 with white on it. */
    --brand:         109 74 255;
    --brand-hover:   124 91 255;
    --brand-text:    167 139 250;
    --brand-soft:    36 30 58;

    --ok:    74 222 128;  --ok-soft:    5 46 22;
    --warn:  251 191 36;  --warn-soft:  56 32 4;
    --error: 248 113 113; --error-soft: 60 15 15;
    --info:  96 165 250;  --info-soft:  17 33 66;
    --idle:  156 163 175; --idle-soft:  34 32 44;
  }
}
```

- [ ] **Step 5: Expose the tokens to Tailwind**

In `src/Harbora.Web/tailwind.config.js`, inside `theme.extend.colors`, after the `brand` block, add:

```js
        // Semantic colours. New markup uses these; the slate ramp above stays for existing views.
        canvas:   'rgb(var(--canvas) / <alpha-value>)',
        surface:  'rgb(var(--surface) / <alpha-value>)',
        'surface-2': 'rgb(var(--surface-2) / <alpha-value>)',
        line:     'rgb(var(--border) / <alpha-value>)',
        'line-strong': 'rgb(var(--border-strong) / <alpha-value>)',
        ink:      'rgb(var(--text) / <alpha-value>)',
        'ink-muted': 'rgb(var(--text-muted) / <alpha-value>)',
        'ink-faint': 'rgb(var(--text-faint) / <alpha-value>)',
        accent:   'rgb(var(--brand) / <alpha-value>)',
        'accent-hover': 'rgb(var(--brand-hover) / <alpha-value>)',
        'accent-text':  'rgb(var(--brand-text) / <alpha-value>)',
        'accent-soft':  'rgb(var(--brand-soft) / <alpha-value>)',
        ok:    'rgb(var(--ok) / <alpha-value>)',
        'ok-soft': 'rgb(var(--ok-soft) / <alpha-value>)',
        warn:  'rgb(var(--warn) / <alpha-value>)',
        'warn-soft': 'rgb(var(--warn-soft) / <alpha-value>)',
        danger: 'rgb(var(--error) / <alpha-value>)',
        'danger-soft': 'rgb(var(--error-soft) / <alpha-value>)',
        info:  'rgb(var(--info) / <alpha-value>)',
        'info-soft': 'rgb(var(--info-soft) / <alpha-value>)',
        idle:  'rgb(var(--idle) / <alpha-value>)',
        'idle-soft': 'rgb(var(--idle-soft) / <alpha-value>)',
```

Note: `line` and `ink` rather than `border` and `text`, because Tailwind already owns
`border-*` and `text-*` as utility prefixes and colliding with them produces `border-border`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~DesignTokenTests"`
Expected: PASS, 11 tests.

- [ ] **Step 7: Verify the build still produces CSS**

Run: `cd src/Harbora.Web && npm run build`
Expected: exit 0, and `wwwroot/build/` contains a rebuilt stylesheet.

- [ ] **Step 8: Commit**

```bash
git add src/Harbora.Web/Scripts/app.css src/Harbora.Web/tailwind.config.js \
        src/Harbora.Infrastructure/Design tests/Harbora.Tests/DesignTokenTests.cs \
        tests/Harbora.Tests/TestPaths.cs
git commit -m "Add semantic design tokens with a contrast test"
```

---

### Task 2: The metric honesty gate

**Files:**
- Create: `src/Harbora.Infrastructure/Monitoring/MetricDisplay.cs`
- Test: `tests/Harbora.Tests/MetricDisplayTests.cs`

**Interfaces:**
- Produces: `MetricDisplay.For(double? value, string unit) → MetricView`,
  `MetricDisplay.ForSeries(IReadOnlyList<double>? series, string unit) → MetricView`,
  and `record MetricView(bool HasData, string Text, IReadOnlyList<double> Series)`.
  `MetricView.Text` is the formatted value when `HasData`, and the empty string otherwise —
  callers render the localized "not collected yet" line themselves.

This is the load-bearing rule of the entire redesign. It is a class rather than a Razor condition
because a condition repeated across forty panels is forty chances to write `?? 0`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Globalization;
using FluentAssertions;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What a panel prints when nobody measured anything.
///
/// The mockups this design came from show about forty populated panels; Harbora collects four
/// metrics. Every one of the rest is a chance to print a confident zero about something that was
/// never observed — a flat line at the bottom of a chart reads as "no traffic", not "no data", and
/// somebody eventually makes a decision on it.
/// </summary>
public class MetricDisplayTests
{
    [Fact]
    public void A_measured_value_is_shown()
    {
        MetricDisplay.For(28.4, "%").Text.Should().Be("28.4%");
    }

    [Fact]
    public void An_unmeasured_value_prints_nothing_at_all()
    {
        var view = MetricDisplay.For(null, "%");

        view.HasData.Should().BeFalse();
        view.Text.Should().BeEmpty();
    }

    [Fact]
    public void Zero_is_a_measurement_and_survives()
    {
        // The other half of the rule, and the one a careless implementation breaks: a service that
        // genuinely served no requests measured zero, and must not be hidden as "unknown".
        var view = MetricDisplay.For(0, "req");

        view.HasData.Should().BeTrue();
        view.Text.Should().Be("0req");
    }

    [Fact]
    public void An_empty_series_is_not_a_flat_line()
    {
        var view = MetricDisplay.ForSeries([], "%");

        view.HasData.Should().BeFalse();
        view.Series.Should().BeEmpty();
    }

    [Fact]
    public void A_null_series_is_not_a_flat_line()
    {
        MetricDisplay.ForSeries(null, "%").HasData.Should().BeFalse();
    }

    [Fact]
    public void A_series_reports_its_last_sample()
    {
        // The headline number over a sparkline is "now", not the average of a day.
        var view = MetricDisplay.ForSeries([10, 20, 30], "%");

        view.HasData.Should().BeTrue();
        view.Text.Should().Be("30%");
        view.Series.Should().Equal(10, 20, 30);
    }

    [Fact]
    public void Numbers_are_formatted_the_same_in_every_culture()
    {
        // The Persian ambient culture renders digits and separators differently, and a metric that
        // reads "۲۸٫۴" in one place and "28.4" in another looks like two different measurements.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");
            MetricDisplay.For(1234.5, "").Text.Should().Be("1234.5");
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void A_whole_number_is_not_padded_with_a_decimal()
    {
        MetricDisplay.For(42, "%").Text.Should().Be("42%");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~MetricDisplayTests"`
Expected: FAIL — `MetricDisplay` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Globalization;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>A metric ready to render, and whether there is anything to render at all.</summary>
/// <param name="HasData">False when nothing was ever measured. Not the same as measuring zero.</param>
/// <param name="Text">The formatted value, or empty when there is nothing to show.</param>
/// <param name="Series">The samples behind it, empty when there are none.</param>
public sealed record MetricView(bool HasData, string Text, IReadOnlyList<double> Series);

/// <summary>
/// The one place allowed to turn a measurement into something a person reads.
///
/// The distinction it exists to hold: <b>unknown is not zero</b>. A panel with no data behind it
/// must say so, because a zero, an em dash or a flat line all read as an observation. Harbora
/// currently collects four metrics and the design has room for forty, so most panels are in this
/// state — and will be until the collector catches up.
///
/// Formatting is invariant on purpose. The ambient culture here is usually Persian, whose digits
/// and decimal separator would make the same number look like two different measurements depending
/// on which page rendered it.
/// </summary>
public static class MetricDisplay
{
    /// <summary>A single measurement, or nothing.</summary>
    public static MetricView For(double? value, string unit = "") =>
        value is { } measured
            ? new MetricView(true, Format(measured) + unit, [])
            : new MetricView(false, string.Empty, []);

    /// <summary>
    /// A series and its headline, which is the latest sample rather than an average: the number
    /// above a sparkline answers "what is it now".
    /// </summary>
    public static MetricView ForSeries(IReadOnlyList<double>? series, string unit = "")
    {
        if (series is not { Count: > 0 }) return new MetricView(false, string.Empty, []);

        return new MetricView(true, Format(series[^1]) + unit, series);
    }

    /// <summary>One decimal place, and none at all when it would only ever be a zero.</summary>
    private static string Format(double value) =>
        value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~MetricDisplayTests"`
Expected: PASS, 8 tests.

- [ ] **Step 5: Mutation-test the rule**

Apply each mutation, run the filtered suite, restore the file, and record whether a test failed.
Every one must be caught; a survivor means a missing test, not an acceptable mutation.

| # | Mutation | Must be caught by |
|---|---|---|
| 1 | `value is { } measured` → `value is { } measured && measured != 0` | `Zero_is_a_measurement_and_survives` |
| 2 | `new MetricView(false, string.Empty, [])` → `new MetricView(false, "0", [])` | `An_unmeasured_value_prints_nothing_at_all` |
| 3 | `new MetricView(false, string.Empty, [])` → `new MetricView(true, string.Empty, [])` | `An_unmeasured_value_prints_nothing_at_all` |
| 4 | `series is not { Count: > 0 }` → `series is null` | `An_empty_series_is_not_a_flat_line` |
| 5 | `series[^1]` → `series[0]` | `A_series_reports_its_last_sample` |
| 6 | `CultureInfo.InvariantCulture` → `CultureInfo.CurrentCulture` | `Numbers_are_formatted_the_same_in_every_culture` |

**Important:** after restoring each mutated file, touch its modification time
(`os.utime(path, None)` or equivalent) — msbuild compares timestamps, and a restored file that
looks older than the last build silently keeps running as the mutant.

- [ ] **Step 6: Commit**

```bash
git add src/Harbora.Infrastructure/Monitoring/MetricDisplay.cs tests/Harbora.Tests/MetricDisplayTests.cs
git commit -m "Add the metric honesty gate: unknown is not zero"
```

---

### Task 3: Navigation model and capability filter

**Files:**
- Create: `src/Harbora.Infrastructure/Navigation/NavigationMap.cs`
- Test: `tests/Harbora.Tests/NavigationMapTests.cs`

**Interfaces:**
- Consumes: `Harbora.Domain.Authorization.Capabilities` (existing constants).
- Produces: `record NavItem(string Key, string Controller, string Action, string Icon, string? Capability)`,
  `record NavGroup(string Key, IReadOnlyList<NavItem> Items)`,
  `NavigationMap.All → IReadOnlyList<NavGroup>`,
  `NavigationMap.VisibleTo(Func<string,bool> hasCapability) → IReadOnlyList<NavGroup>`.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Navigation;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which doors the sidebar advertises.
///
/// A menu that lists a section the viewer cannot open is not a cosmetic problem: it is the same
/// defect as a disabled button that still posts, seen from the other side. The filter is a rule
/// here rather than a chain of `@if (User.IsInRole(...))` in the layout, because the layout is
/// where that check silently stops being applied to the item somebody adds next.
/// </summary>
public class NavigationMapTests
{
    private static IReadOnlyList<NavItem> Items(IReadOnlyList<NavGroup> groups) =>
        groups.SelectMany(g => g.Items).ToList();

    [Fact]
    public void Everything_is_visible_to_someone_with_every_capability()
    {
        var visible = NavigationMap.VisibleTo(_ => true);

        Items(visible).Should().HaveCount(Items(NavigationMap.All).Count);
    }

    [Fact]
    public void A_section_needing_a_capability_is_hidden_without_it()
    {
        var visible = NavigationMap.VisibleTo(c => c != Capabilities.TenantsManage);

        Items(visible).Should().NotContain(i => i.Capability == Capabilities.TenantsManage);
    }

    [Fact]
    public void Open_sections_stay_visible_to_everyone()
    {
        // Reading is not an action capability. A viewer who can see the app list must still reach it.
        var visible = NavigationMap.VisibleTo(_ => false);

        Items(visible).Should().NotBeEmpty();
        Items(visible).Should().OnlyContain(i => i.Capability == null);
    }

    [Fact]
    public void A_group_whose_items_are_all_hidden_disappears()
    {
        // An empty group header is a heading over nothing, which reads as a broken page.
        var visible = NavigationMap.VisibleTo(_ => false);

        visible.Should().OnlyContain(g => g.Items.Count > 0);
    }

    [Fact]
    public void Every_item_names_a_real_capability()
    {
        // A typo in a capability name hides a section forever and looks exactly like a permission
        // problem, which is the most expensive kind of bug to chase.
        Items(NavigationMap.All)
            .Where(i => i.Capability is not null)
            .Should().OnlyContain(i => Capabilities.All.Contains(i.Capability!));
    }

    [Fact]
    public void Keys_are_unique()
    {
        var keys = Items(NavigationMap.All).Select(i => i.Key).ToList();

        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void There_is_no_billing_section()
    {
        // The mockups have one. Harbora has no billing, and a menu item leading to an empty page is
        // the exact thing this redesign is meant to stop doing.
        Items(NavigationMap.All).Should().NotContain(i => i.Key == "billing");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~NavigationMapTests"`
Expected: FAIL — `NavigationMap` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using Harbora.Domain.Authorization;

namespace Harbora.Infrastructure.Navigation;

/// <summary>One destination in the sidebar.</summary>
/// <param name="Key">Stable identifier, also the translation key.</param>
/// <param name="Capability">Null when reading the section needs no action capability.</param>
public sealed record NavItem(string Key, string Controller, string Action, string Icon, string? Capability = null);

/// <summary>A labelled run of destinations.</summary>
public sealed record NavGroup(string Key, IReadOnlyList<NavItem> Items);

/// <summary>
/// The sidebar, as data.
///
/// Grouped the way the mockups group things, and containing every section Harbora actually has —
/// nothing functional was dropped to match a picture, and nothing was invented to fill one. There
/// is deliberately no Billing item: there is no billing.
/// </summary>
public static class NavigationMap
{
    public static IReadOnlyList<NavGroup> All { get; } =
    [
        new("overview", [
            new("dashboard", "Home", "Index", "layout-dashboard")
        ]),
        new("deploy", [
            new("applications", "Apps", "Index", "boxes"),
            new("services", "Databases", "Index", "layers"),
            new("deployments", "Deployments", "Index", "rocket")
        ]),
        new("connect", [
            new("networks", "Projects", "Index", "network"),
            new("domains", "Domains", "Index", "globe"),
            new("routing", "Routes", "Index", "route", Capabilities.RoutesManage)
        ]),
        new("data", [
            new("backups", "Backups", "Index", "archive")
        ]),
        new("insight", [
            new("monitoring", "Monitoring", "Index", "activity"),
            new("audit", "Audit", "Index", "scroll-text")
        ]),
        new("build", [
            new("templates", "Templates", "Index", "shapes"),
            new("git", "Git", "Index", "git-branch", Capabilities.GitManage)
        ]),
        new("platform", [
            new("servers", "Servers", "Index", "server", Capabilities.ServersManage),
            new("plans", "Plans", "Index", "credit-card", Capabilities.PlansManage),
            new("tenants", "Tenants", "Index", "building", Capabilities.TenantsManage),
            new("settings", "Settings", "Index", "settings")
        ])
    ];

    /// <summary>
    /// The map as one caller may see it. Items are hidden rather than disabled: a sidebar that
    /// lists a locked door is a sidebar people learn to distrust.
    /// </summary>
    public static IReadOnlyList<NavGroup> VisibleTo(Func<string, bool> hasCapability) =>
        All
            .Select(group => group with
            {
                Items = group.Items.Where(i => i.Capability is null || hasCapability(i.Capability)).ToList()
            })
            .Where(group => group.Items.Count > 0)
            .ToList();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~NavigationMapTests"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Mutation-test the filter**

| # | Mutation | Must be caught by |
|---|---|---|
| 1 | `i.Capability is null \|\| hasCapability(...)` → `hasCapability(i.Capability!)` | `Open_sections_stay_visible_to_everyone` |
| 2 | `i.Capability is null \|\| hasCapability(...)` → `true` | `A_section_needing_a_capability_is_hidden_without_it` |
| 3 | `.Where(group => group.Items.Count > 0)` → removed | `A_group_whose_items_are_all_hidden_disappears` |

Restore each file and touch its timestamp afterwards.

- [ ] **Step 6: Commit**

```bash
git add src/Harbora.Infrastructure/Navigation tests/Harbora.Tests/NavigationMapTests.cs
git commit -m "Add the navigation map with a capability filter"
```

---

### Task 4: Presentational partials

**Files:**
- Create: `src/Harbora.Web/Views/Shared/Design/_Panel.cshtml`
- Create: `src/Harbora.Web/Views/Shared/Design/_StatCard.cshtml`
- Create: `src/Harbora.Web/Views/Shared/Design/_StatusPill.cshtml`
- Create: `src/Harbora.Web/Views/Shared/Design/_EmptyState.cshtml`
- Create: `src/Harbora.Web/Views/Shared/Design/_Metric.cshtml`
- Create: `src/Harbora.Web/ViewModels/DesignViewModels.cs`

**Interfaces:**
- Consumes: `MetricView` and `MetricDisplay` from Task 2.
- Produces: view models `PanelModel(string Title, string? LinkText, string? LinkUrl)`,
  `StatCardModel(string Icon, string Label, MetricView Value, string? Delta)`,
  `StatusPillModel(string Text, string Tone)` where `Tone` is one of
  `ok|warn|error|info|idle`, `EmptyStateModel(string Icon, string Message, string? ActionText, string? ActionUrl)`,
  `MetricModel(MetricView View, string Label)`.

- [ ] **Step 1: Write the view models**

Create `src/Harbora.Web/ViewModels/DesignViewModels.cs`:

```csharp
using Harbora.Infrastructure.Monitoring;

namespace Harbora.Web.ViewModels;

/// <summary>A bordered card with a header. The unit every screen in the design is built from.</summary>
public sealed record PanelModel(string Title, string? LinkText = null, string? LinkUrl = null);

/// <summary>One of the headline figures across the top of a page.</summary>
public sealed record StatCardModel(string Icon, string Label, MetricView Value, string? Delta = null);

/// <summary>Tone is semantic, not a colour: the palette decides what "warn" looks like.</summary>
public sealed record StatusPillModel(string Text, string Tone);

/// <summary>Shown instead of an empty table, with the one action that fills it.</summary>
public sealed record EmptyStateModel(string Icon, string Message, string? ActionText = null, string? ActionUrl = null);

/// <summary>A measurement and its label, routed through the honesty gate.</summary>
public sealed record MetricModel(MetricView View, string Label);
```

- [ ] **Step 2: Write `_Metric.cshtml`**

```razor
@model Harbora.Web.ViewModels.MetricModel
@{
    var isFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
}
@* The only component permitted to print a measured number.

   When nothing was collected it says so, in words, rather than printing a zero — a zero here is a
   claim that something was observed and found to be none, which is a different and much stronger
   statement than "we have not looked yet". *@
<div class="space-y-0.5">
    <div class="text-xs text-ink-muted">@Model.Label</div>
    @if (Model.View.HasData)
    {
        <div dir="ltr" class="text-2xl font-semibold text-ink tabular-nums">@Model.View.Text</div>
    }
    else
    {
        <div class="text-sm text-ink-faint">@(isFa ? "هنوز جمع‌آوری نشده" : "not collected yet")</div>
    }
</div>
```

- [ ] **Step 3: Write `_Panel.cshtml`**

```razor
@model Harbora.Web.ViewModels.PanelModel
<section class="rounded-xl border border-line bg-surface shadow-panel">
    <header class="flex items-center justify-between gap-2 px-5 py-3.5 border-b border-line">
        <h2 class="text-[15px] font-semibold text-ink">@Model.Title</h2>
        @if (Model.LinkUrl is not null && Model.LinkText is not null)
        {
            <a href="@Model.LinkUrl" class="text-xs text-accent-text hover:underline">@Model.LinkText</a>
        }
    </header>
    <div class="p-5">
        @RenderBody()
    </div>
</section>
```

Note: Razor partials cannot call `RenderBody`. Implement this as a **tag helper-free layout
partial** by making it a `@await Html.PartialAsync` wrapper is not possible either — so `_Panel`
is written instead as two partials, `_PanelStart.cshtml` and `_PanelEnd.cshtml`, used as:

```razor
<partial name="Design/_PanelStart" model='new PanelModel("Recent Backups", "View all", "/backups")' />
    ... content ...
<partial name="Design/_PanelEnd" />
```

`_PanelStart.cshtml`:

```razor
@model Harbora.Web.ViewModels.PanelModel
<section class="rounded-xl border border-line bg-surface shadow-panel">
    <header class="flex items-center justify-between gap-2 px-5 py-3.5 border-b border-line">
        <h2 class="text-[15px] font-semibold text-ink">@Model.Title</h2>
        @if (Model.LinkUrl is not null && Model.LinkText is not null)
        {
            <a href="@Model.LinkUrl" class="text-xs text-accent-text hover:underline">@Model.LinkText</a>
        }
    </header>
    <div class="p-5">
```

`_PanelEnd.cshtml`:

```razor
    </div>
</section>
```

- [ ] **Step 4: Write `_StatCard.cshtml`**

```razor
@model Harbora.Web.ViewModels.StatCardModel
<div class="rounded-xl border border-line bg-surface p-5 shadow-panel">
    <div class="flex items-start gap-3">
        <span class="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent-text">
            <i data-lucide="@Model.Icon" class="h-5 w-5"></i>
        </span>
        <div class="min-w-0 flex-1">
            <partial name="Design/_Metric" model='new Harbora.Web.ViewModels.MetricModel(Model.Value, Model.Label)' />
            @if (Model.Delta is not null)
            {
                <div class="mt-1 text-xs text-ink-faint">@Model.Delta</div>
            }
        </div>
    </div>
</div>
```

- [ ] **Step 5: Write `_StatusPill.cshtml`**

```razor
@model Harbora.Web.ViewModels.StatusPillModel
@{
    // Tone → classes, in one place. Interpolating the tone into a class name would work until
    // Tailwind's purge removed every class it could not see written out.
    var classes = Model.Tone switch
    {
        "ok"    => "bg-ok-soft text-ok",
        "warn"  => "bg-warn-soft text-warn",
        "error" => "bg-danger-soft text-danger",
        "info"  => "bg-info-soft text-info",
        _       => "bg-idle-soft text-idle"
    };
}
<span class="inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium @classes">
    <span class="h-1.5 w-1.5 rounded-full bg-current"></span>@Model.Text
</span>
```

- [ ] **Step 6: Write `_EmptyState.cshtml`**

```razor
@model Harbora.Web.ViewModels.EmptyStateModel
<div class="flex flex-col items-center justify-center gap-3 px-6 py-14 text-center">
    <span class="inline-flex h-12 w-12 items-center justify-center rounded-2xl bg-accent-soft text-accent-text">
        <i data-lucide="@Model.Icon" class="h-6 w-6"></i>
    </span>
    <p class="text-sm text-ink-muted max-w-sm">@Model.Message</p>
    @if (Model.ActionUrl is not null && Model.ActionText is not null)
    {
        <a href="@Model.ActionUrl" class="rounded-lg bg-accent px-4 py-2 text-sm font-semibold text-white hover:bg-accent-hover">@Model.ActionText</a>
    }
</div>
```

- [ ] **Step 7: Add the panel shadow to Tailwind**

In `src/Harbora.Web/tailwind.config.js`, inside `theme.extend`, add:

```js
      boxShadow: {
        panel: '0 1px 2px rgb(16 12 40 / 0.04)',
        'panel-hover': '0 4px 12px rgb(16 12 40 / 0.06)',
      },
```

- [ ] **Step 8: Build and commit**

Run: `cd src/Harbora.Web && npm run build && cd ../.. && dotnet build Harbora.slnx -v q --nologo`
Expected: both succeed.

```bash
git add src/Harbora.Web/Views/Shared/Design src/Harbora.Web/ViewModels/DesignViewModels.cs \
        src/Harbora.Web/tailwind.config.js
git commit -m "Add the shared design partials"
```

---

### Task 5: Sparkline and mini chart

**Files:**
- Create: `src/Harbora.Infrastructure/Design/SparklinePath.cs`
- Create: `src/Harbora.Web/Views/Shared/Design/_Sparkline.cshtml`
- Test: `tests/Harbora.Tests/SparklinePathTests.cs`

**Interfaces:**
- Consumes: `MetricView` from Task 2.
- Produces: `SparklinePath.Build(IReadOnlyList<double> series, int width, int height) → string?`
  returning an SVG `d` attribute, or null when the series cannot draw one.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Harbora.Infrastructure.Design;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Turning samples into a line.
///
/// The case that matters is the degenerate one: a flat series, a single sample, or no samples at
/// all must not silently become a confident-looking chart. A sparkline is read at a glance, which
/// is exactly why a wrong one is believed.
/// </summary>
public class SparklinePathTests
{
    [Fact]
    public void A_series_becomes_a_path()
    {
        var path = SparklinePath.Build([0, 5, 10], 100, 20);

        path.Should().StartWith("M").And.Contain("L");
    }

    [Fact]
    public void No_samples_draw_nothing()
    {
        SparklinePath.Build([], 100, 20).Should().BeNull();
    }

    [Fact]
    public void One_sample_draws_nothing()
    {
        // A single point is not a trend, and a dot at the left edge reads as a crash to zero.
        SparklinePath.Build([42], 100, 20).Should().BeNull();
    }

    [Fact]
    public void A_flat_series_draws_a_flat_line_through_the_middle()
    {
        // Not at the bottom: a constant 80% drawn along the floor reads as an outage.
        var path = SparklinePath.Build([80, 80, 80], 100, 20);

        path.Should().Contain("10");
        path.Should().NotContain("NaN");
    }

    [Fact]
    public void The_line_stays_inside_the_box()
    {
        var path = SparklinePath.Build([1, 500, 3, 900, 2], 100, 20);

        foreach (var y in Ys(path))
        {
            y.Should().BeGreaterThanOrEqualTo(0);
            y.Should().BeLessThanOrEqualTo(20);
        }
    }

    [Fact]
    public void Coordinates_are_invariant_regardless_of_culture()
    {
        // A Persian decimal separator inside an SVG path attribute silently breaks the whole shape.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fa-IR");
            SparklinePath.Build([1, 2, 3], 100, 20).Should().NotContain("٫").And.NotContain(",");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }

    private static IEnumerable<double> Ys(string? path) =>
        (path ?? "").Split(['M', 'L'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Split(' ')[1])
            .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~SparklinePathTests"`
Expected: FAIL — `SparklinePath` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Globalization;
using System.Text;

namespace Harbora.Infrastructure.Design;

/// <summary>
/// The `d` attribute of a sparkline.
///
/// Returns null rather than a path whenever the samples cannot honestly describe a trend — no
/// samples, or only one. The caller renders the "not collected yet" state instead; a chart drawn
/// from nothing is read as a measurement, and it is read at a glance, which is precisely why a
/// wrong one is believed.
/// </summary>
public static class SparklinePath
{
    public static string? Build(IReadOnlyList<double> series, int width, int height)
    {
        if (series.Count < 2) return null;

        var min = series.Min();
        var max = series.Max();
        var span = max - min;

        var stepX = (double)width / (series.Count - 1);
        var path = new StringBuilder();

        for (var i = 0; i < series.Count; i++)
        {
            // A constant series has no span to scale by. Drawn along the floor it would read as an
            // outage, so it runs through the middle instead.
            var normalized = span == 0 ? 0.5 : (series[i] - min) / span;

            var x = i * stepX;
            var y = height - normalized * height;

            path.Append(i == 0 ? 'M' : 'L')
                .Append(Coordinate(x)).Append(' ').Append(Coordinate(y));
        }

        return path.ToString();
    }

    /// <summary>
    /// Invariant, always. A Persian decimal separator inside a path attribute does not throw — it
    /// produces a shape nobody drew.
    /// </summary>
    private static string Coordinate(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~SparklinePathTests"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Write `_Sparkline.cshtml`**

```razor
@model Harbora.Web.ViewModels.MetricModel
@{
    var isFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
    var path = Model.View.HasData
        ? Harbora.Infrastructure.Design.SparklinePath.Build(Model.View.Series, 100, 20)
        : null;
}
@if (path is not null)
{
    <svg viewBox="0 0 100 20" preserveAspectRatio="none" class="h-6 w-full" aria-hidden="true">
        <path d="@path" fill="none" stroke="currentColor" stroke-width="1.5"
              stroke-linecap="round" stroke-linejoin="round" vector-effect="non-scaling-stroke" />
    </svg>
}
else
{
    <div class="h-6 flex items-center text-[11px] text-ink-faint">
        @(isFa ? "هنوز جمع‌آوری نشده" : "not collected yet")
    </div>
}
```

- [ ] **Step 6: Commit**

```bash
git add src/Harbora.Infrastructure/Design/SparklinePath.cs \
        src/Harbora.Web/Views/Shared/Design/_Sparkline.cshtml \
        tests/Harbora.Tests/SparklinePathTests.cs
git commit -m "Add the sparkline, which refuses to draw a trend from one sample"
```

---

### Task 6: The sidebar

**Files:**
- Create: `src/Harbora.Web/Views/Shared/Design/_Sidebar.cshtml`
- Modify: `src/Harbora.Web/Views/Shared/_Layout.cshtml:57-90`
- Create: `src/Harbora.Web/Infrastructure/NavigationCapabilities.cs`

**Interfaces:**
- Consumes: `NavigationMap.VisibleTo` from Task 3.
- Produces: `NavigationCapabilities.For(ClaimsPrincipal user) → Func<string,bool>` — the adapter
  between the signed-in principal and the map's filter.

- [ ] **Step 1: Write the capability adapter**

Create `src/Harbora.Web/Infrastructure/NavigationCapabilities.cs`:

```csharp
using System.Security.Claims;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Bridges the signed-in principal to the navigation map's filter.
///
/// Capabilities are registered as authorization policies, and policies are asynchronous — but a
/// layout cannot await. The claims are already on the principal, so this reads them directly rather
/// than resolving the authorization service per menu item on every request.
/// </summary>
public static class NavigationCapabilities
{
    /// <summary>The claim type each capability is issued as.</summary>
    public const string ClaimType = "capability";

    public static Func<string, bool> For(ClaimsPrincipal user)
    {
        var granted = user.FindAll(ClaimType).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        return capability => granted.Contains(capability);
    }
}
```

Verify the claim type matches what the sign-in path issues:

Run: `grep -rn "capability" --include=*.cs src/Harbora.Web/ src/Harbora.Infrastructure/ | head`
Expected: a claim of this name is added when the principal is built. If the codebase issues
capabilities under a different claim type or derives them from `SystemRole`, change `ClaimType` to
match — do **not** change the sign-in path.

- [ ] **Step 2: Write `_Sidebar.cshtml`**

```razor
@using Harbora.Infrastructure.Navigation
@using Harbora.Web.Infrastructure
@{
    var isFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
    var groups = NavigationMap.VisibleTo(NavigationCapabilities.For(User));
    var currentController = (string?)ViewContext.RouteData.Values["controller"];

    string GroupLabel(string key) => (isFa, key) switch
    {
        (true,  "overview") => "نمای کلی",   (false, "overview") => "Overview",
        (true,  "deploy")   => "استقرار",     (false, "deploy")   => "Deploy",
        (true,  "connect")  => "اتصال",       (false, "connect")  => "Connect",
        (true,  "data")     => "داده",        (false, "data")     => "Data",
        (true,  "insight")  => "بینش",        (false, "insight")  => "Insight",
        (true,  "build")    => "ساخت",        (false, "build")    => "Build",
        (true,  "platform") => "پلتفرم",      (false, "platform") => "Platform",
        _ => key
    };

    string ItemLabel(string key) => (isFa, key) switch
    {
        (true,  "dashboard")    => "داشبورد",        (false, "dashboard")    => "Dashboard",
        (true,  "applications") => "برنامه‌ها",       (false, "applications") => "Applications",
        (true,  "services")     => "سرویس‌ها",        (false, "services")     => "Services",
        (true,  "deployments")  => "استقرارها",       (false, "deployments")  => "Deployments",
        (true,  "networks")     => "شبکه‌ها",         (false, "networks")     => "Networks",
        (true,  "domains")      => "دامنه‌ها و SSL",  (false, "domains")      => "Domains & SSL",
        (true,  "routing")      => "مسیریابی",        (false, "routing")      => "Routing",
        (true,  "backups")      => "پشتیبان‌ها",      (false, "backups")      => "Backups",
        (true,  "monitoring")   => "پایش",            (false, "monitoring")   => "Monitoring",
        (true,  "audit")        => "گزارش رخدادها",   (false, "audit")        => "Audit log",
        (true,  "templates")    => "قالب‌ها",          (false, "templates")    => "Templates",
        (true,  "git")          => "Git",             (false, "git")          => "Git",
        (true,  "servers")      => "سرورها",          (false, "servers")      => "Servers",
        (true,  "plans")        => "پلن‌ها",           (false, "plans")        => "Plans",
        (true,  "tenants")      => "مستأجرها",        (false, "tenants")      => "Tenants",
        (true,  "settings")     => "تنظیمات",         (false, "settings")     => "Settings",
        _ => key
    };
}
<nav class="flex-1 overflow-y-auto px-3 py-4 space-y-5 scrollbar-thin">
    @foreach (var group in groups)
    {
        <div>
            <div class="px-3 pb-1.5 text-[11px] font-semibold uppercase tracking-wide text-ink-faint">
                @GroupLabel(group.Key)
            </div>
            <div class="space-y-0.5">
                @foreach (var item in group.Items)
                {
                    var active = string.Equals(currentController, item.Controller, StringComparison.OrdinalIgnoreCase);
                    <a asp-controller="@item.Controller" asp-action="@item.Action"
                       class="flex items-center gap-2.5 rounded-lg px-3 py-2 text-sm transition
                              @(active ? "bg-accent-soft text-accent-text font-medium" : "text-ink-muted hover:bg-surface-2 hover:text-ink")">
                        <i data-lucide="@item.Icon" class="h-4 w-4 shrink-0"></i>
                        <span class="truncate">@ItemLabel(item.Key)</span>
                    </a>
                }
            </div>
        </div>
    }
</nav>
```

- [ ] **Step 3: Replace the sidebar in the layout**

In `src/Harbora.Web/Views/Shared/_Layout.cshtml`, replace the `<nav>` element inside `<aside>`
with `<partial name="Design/_Sidebar" />`, and change the `<aside>` and `<body>` classes:

```razor
<body class="h-full bg-canvas text-ink antialiased">
<div class="flex h-full">
    <div id="backdrop" class="app-backdrop hidden md:hidden"></div>
    <aside id="sidebar" class="app-sidebar flex md:w-60 flex-col border-e border-line bg-surface">
        <div class="flex items-center gap-2.5 px-5 h-16 border-b border-line">
            <span class="inline-flex h-8 w-8 items-center justify-center rounded-lg bg-accent text-white font-bold">H</span>
            <span class="text-lg font-semibold tracking-tight text-ink">Harbora</span>
        </div>
        <partial name="Design/_Sidebar" />
    </aside>
```

- [ ] **Step 4: Verify every route still renders**

Run: `dotnet build Harbora.slnx -v q --nologo && dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --nologo`
Expected: build succeeds, all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Harbora.Web/Views/Shared/Design/_Sidebar.cshtml \
        src/Harbora.Web/Views/Shared/_Layout.cshtml \
        src/Harbora.Web/Infrastructure/NavigationCapabilities.cs
git commit -m "Rebuild the sidebar from the navigation map"
```

---

### Task 7: The topbar

**Files:**
- Create: `src/Harbora.Web/Views/Shared/Design/_Topbar.cshtml`
- Modify: `src/Harbora.Web/Views/Shared/_Layout.cshtml` (the existing header element)
- Modify: `src/Harbora.Web/Controllers/HomeController.cs` — no changes; the environment list comes
  from a view component instead.
- Create: `src/Harbora.Web/Components/EnvironmentSwitcherViewComponent.cs`

**Interfaces:**
- Produces: `EnvironmentSwitcherViewComponent.InvokeAsync() → IViewComponentResult` rendering the
  current workspace's environments, with the active one marked.

- [ ] **Step 1: Write the view component**

```csharp
using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Components;

/// <summary>
/// The environment picker in the topbar.
///
/// Reads the real environments rather than showing a fixed "Production" label: a switcher that
/// always says the same word is a decoration, and this one is how somebody tells which environment
/// the numbers on the page belong to.
/// </summary>
public sealed class EnvironmentSwitcherViewComponent(HarboraDbContext db, ICurrentUser currentUser) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var workspaceId = currentUser.WorkspaceId ?? Guid.Empty;

        var environments = await db.Environments
            .Where(e => e.WorkspaceId == workspaceId)
            .OrderByDescending(e => e.IsDefault).ThenBy(e => e.Name)
            .Select(e => new EnvironmentOption(e.Id, e.Name, e.IsDefault))
            .ToListAsync();

        return View(environments);
    }

    public sealed record EnvironmentOption(Guid Id, string Name, bool IsDefault);
}
```

Create `src/Harbora.Web/Views/Shared/Components/EnvironmentSwitcher/Default.cshtml`:

```razor
@model IReadOnlyList<Harbora.Web.Components.EnvironmentSwitcherViewComponent.EnvironmentOption>
@{
    var isFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
    var current = Model.FirstOrDefault(e => e.IsDefault) ?? Model.FirstOrDefault();
}
@if (current is not null)
{
    <div class="relative" data-env-switcher>
        <button type="button" class="flex items-center gap-2 rounded-lg border border-line bg-surface px-3 py-1.5 text-sm text-ink hover:bg-surface-2">
            <i data-lucide="layers" class="h-4 w-4 text-ink-faint"></i>
            <span>@current.Name</span>
            <i data-lucide="chevron-down" class="h-3.5 w-3.5 text-ink-faint"></i>
        </button>
    </div>
}
else
{
    <span class="text-sm text-ink-faint">@(isFa ? "بدون محیط" : "No environment")</span>
}
```

- [ ] **Step 2: Write `_Topbar.cshtml`**

```razor
@{
    var isFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
}
<header class="flex h-16 shrink-0 items-center gap-3 border-b border-line bg-surface px-5">
    <button id="menuBtn" class="md:hidden rounded-lg p-2 text-ink-muted hover:bg-surface-2" aria-label="Menu">
        <i data-lucide="menu" class="h-5 w-5"></i>
    </button>

    @await Component.InvokeAsync("EnvironmentSwitcher")

    <div class="flex-1 flex justify-center px-4">
        <label class="relative w-full max-w-md">
            <i data-lucide="search" class="pointer-events-none absolute inset-y-0 start-3 my-auto h-4 w-4 text-ink-faint"></i>
            <input type="search" data-global-search
                   placeholder="@(isFa ? "جست‌وجو…" : "Search anything…")"
                   class="w-full rounded-lg border border-line bg-surface-2 py-2 ps-9 pe-12 text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none" />
            <kbd class="pointer-events-none absolute inset-y-0 end-3 my-auto h-5 rounded border border-line px-1.5 text-[10px] leading-5 text-ink-faint">⌘K</kbd>
        </label>
    </div>

    <div class="flex items-center gap-1">
        <a href="/monitoring" class="rounded-lg p-2 text-ink-muted hover:bg-surface-2" title="@(isFa ? "پایش" : "Monitoring")">
            <i data-lucide="activity" class="h-5 w-5"></i>
        </a>
        <button type="button" data-theme-toggle class="rounded-lg p-2 text-ink-muted hover:bg-surface-2" title="@(isFa ? "تم" : "Theme")">
            <i data-lucide="sun" class="h-5 w-5"></i>
        </button>
        <partial name="_Auth" />
    </div>
</header>
```

- [ ] **Step 3: Wire it into the layout**

Replace the existing `<header>` in `_Layout.cshtml` with `<partial name="Design/_Topbar" />`.
Keep the existing `#menuBtn` and theme-toggle JavaScript: their selectors are preserved above.

- [ ] **Step 4: Verify the theme toggle and mobile menu still work**

Run: `dotnet build Harbora.slnx -v q --nologo`
Then load the panel and confirm: the menu button opens the sidebar under 768px, and the theme
button still cycles system → light → dark.

- [ ] **Step 5: Commit**

```bash
git add src/Harbora.Web/Views/Shared/Design/_Topbar.cshtml \
        src/Harbora.Web/Components src/Harbora.Web/Views/Shared/Components \
        src/Harbora.Web/Views/Shared/_Layout.cshtml
git commit -m "Rebuild the topbar with a real environment switcher"
```

---

### Task 8: Page header and the right rail

**Files:**
- Create: `src/Harbora.Web/Views/Shared/Design/_PageHeader.cshtml`
- Modify: `src/Harbora.Web/Views/Shared/_Layout.cshtml` (the `<main>` element)
- Create: `src/Harbora.Web/ViewModels/PageHeaderModel.cs`

**Interfaces:**
- Produces: `PageHeaderModel(string Title, string? Description, string? Badge)`, and a
  `RightRail` Razor section that pages may define.

- [ ] **Step 1: Write the model and partial**

`src/Harbora.Web/ViewModels/PageHeaderModel.cs`:

```csharp
namespace Harbora.Web.ViewModels;

/// <summary>The title block every page opens with.</summary>
public sealed record PageHeaderModel(string Title, string? Description = null, string? Badge = null);
```

`_PageHeader.cshtml`:

```razor
@model Harbora.Web.ViewModels.PageHeaderModel
<div class="mb-6 flex flex-wrap items-start justify-between gap-3">
    <div>
        <div class="flex items-center gap-2">
            <h1 class="text-2xl font-bold tracking-tight text-ink">@Model.Title</h1>
            @if (Model.Badge is not null)
            {
                <span class="rounded-full bg-accent-soft px-2 py-0.5 text-[11px] font-medium text-accent-text">@Model.Badge</span>
            }
        </div>
        @if (Model.Description is not null)
        {
            <p class="mt-1 text-sm text-ink-muted">@Model.Description</p>
        }
    </div>
    @RenderSection("PageActions", required: false)
</div>
```

Note: a partial cannot render a section. Move `@RenderSection("PageActions", required: false)` into
`_Layout.cshtml` immediately after the `<partial name="Design/_PageHeader" ... />` call site instead,
and keep `_PageHeader` to the title block only.

- [ ] **Step 2: Restructure `<main>` in the layout**

```razor
        <div class="flex flex-1 min-w-0 flex-col">
            <partial name="Design/_Topbar" />
            <div class="flex flex-1 min-h-0 overflow-y-auto">
                <main class="flex-1 min-w-0 p-6">
                    @RenderBody()
                </main>
                @if (IsSectionDefined("RightRail"))
                {
                    @* Below 1280px the rail moves under the content rather than disappearing: on
                       these pages it carries information, not decoration. *@
                    <aside class="hidden xl:block w-80 shrink-0 border-s border-line bg-surface p-5 space-y-5">
                        @await RenderSectionAsync("RightRail", required: false)
                    </aside>
                }
            </div>
            @if (IsSectionDefined("RightRail"))
            {
                <div class="xl:hidden px-6 pb-6 space-y-5">
                    @await RenderSectionAsync("RightRail", required: false)
                </div>
            }
        </div>
```

**Problem:** a Razor section can only be rendered once. Rendering `RightRail` twice throws.
Instead, render it once inside a container whose placement is controlled by CSS:

```razor
        <div class="flex flex-1 min-w-0 flex-col">
            <partial name="Design/_Topbar" />
            <div class="flex flex-1 min-h-0 flex-col overflow-y-auto xl:flex-row">
                <main class="flex-1 min-w-0 p-6">
                    @RenderBody()
                </main>
                @if (IsSectionDefined("RightRail"))
                {
                    <aside class="w-full xl:w-80 shrink-0 border-t xl:border-t-0 xl:border-s border-line bg-surface p-5 space-y-5">
                        @await RenderSectionAsync("RightRail", required: false)
                    </aside>
                }
            </div>
        </div>
```

- [ ] **Step 3: Verify with a page that defines a rail and one that does not**

Add `@section RightRail { <partial name="Design/_PanelStart" model='new PanelModel("Test")' /><p>ok</p><partial name="Design/_PanelEnd" /> }`
temporarily to `Views/Home/Index.cshtml`, load `/` and `/apps`, confirm the rail appears on one and
not the other, then remove it.

- [ ] **Step 4: Commit**

```bash
git add src/Harbora.Web/Views/Shared/Design/_PageHeader.cshtml \
        src/Harbora.Web/ViewModels/PageHeaderModel.cs \
        src/Harbora.Web/Views/Shared/_Layout.cshtml
git commit -m "Add the page header and the optional right rail"
```

---

### Task 9: Icons

**Files:**
- Modify: `src/Harbora.Web/Scripts/main.ts`
- Modify: `src/Harbora.Web/package.json`

The partials above reference `data-lucide` icons. Wire the library up once, here, rather than
per-partial.

- [ ] **Step 1: Add the dependency**

Run: `cd src/Harbora.Web && npm install lucide@^0.460.0`

- [ ] **Step 2: Initialise icons on load and after any dynamic update**

Append to `src/Harbora.Web/Scripts/main.ts`:

```ts
import { createIcons, icons } from 'lucide';

// Icons are declared as `data-lucide` attributes in Razor rather than inlined SVG, so a partial
// stays readable. They are re-rendered after dynamic updates because the Vue islands and the log
// poller both insert markup after first paint.
function renderIcons() {
  createIcons({ icons, attrs: { 'stroke-width': '1.75' } });
}

renderIcons();
document.addEventListener('harbora:content-changed', renderIcons);
```

- [ ] **Step 3: Verify icons render**

Run: `cd src/Harbora.Web && npm run build`
Then load the panel and confirm sidebar icons are visible rather than empty boxes.

- [ ] **Step 4: Commit**

```bash
git add src/Harbora.Web/Scripts/main.ts src/Harbora.Web/package.json src/Harbora.Web/package-lock.json
git commit -m "Render sidebar and panel icons"
```

---

### Task 10: Verify the shell end to end

**Files:**
- Test: `tests/Harbora.Tests/LayoutConventionTests.cs`

- [ ] **Step 1: Write the convention test**

```csharp
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Conventions the shell depends on, checked mechanically because they are the kind of thing that
/// decays one view at a time and is never noticed until somebody opens the panel in Persian.
/// </summary>
public class LayoutConventionTests
{
    private static IEnumerable<string> DesignViews() =>
        Directory.EnumerateFiles(Path.Combine(TestPaths.WebRoot, "Views", "Shared", "Design"), "*.cshtml");

    [Fact]
    public void No_design_partial_uses_a_physical_direction_class()
    {
        // ml-/mr-/left-/right-/pl-/pr- do not mirror in RTL. Persian is the default culture here,
        // so a physical class is a layout that is wrong for most of the people using it.
        var physical = new Regex(@"(?<![a-z-])(ml|mr|pl|pr|left|right|border-l|border-r|rounded-l|rounded-r)-", RegexOptions.Compiled);

        foreach (var file in DesignViews())
        {
            var text = File.ReadAllText(file);
            physical.IsMatch(text).Should().BeFalse($"{Path.GetFileName(file)} must use logical properties");
        }
    }

    [Fact]
    public void Every_design_partial_offers_both_languages()
    {
        // A partial with user-visible English and no Persian branch ships an untranslated screen.
        foreach (var file in DesignViews())
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("isFa")) continue;

            Regex.Matches(text, @"isFa \? ""[^""]+"" : ""[^""]+""").Count
                .Should().BeGreaterThan(0, $"{Path.GetFileName(file)} branches on culture but has no pair");
        }
    }

    [Fact]
    public void The_metric_partial_is_the_only_one_printing_a_raw_metric_value()
    {
        // The honesty gate only works if nothing routes around it.
        foreach (var file in DesignViews())
        {
            var name = Path.GetFileName(file);
            if (name is "_Metric.cshtml" or "_Sparkline.cshtml") continue;

            File.ReadAllText(file).Should().NotContain("Model.View.Text",
                $"{name} should render Design/_Metric instead of printing the value itself");
        }
    }
}
```

- [ ] **Step 2: Run the full suite**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --nologo`
Expected: PASS, no failures.

- [ ] **Step 3: Verify both cultures and both themes render**

Run the panel locally, then for each of `/`, `/apps`, `/deployments`, `/backups`, `/monitoring`,
`/settings`: confirm HTTP 200, check `dir="rtl"` under Persian, and toggle the theme.

- [ ] **Step 4: Deploy to the server and verify**

Upload the changed files, rebuild the panel image, restart it, and smoke-test every route as in
previous phases. Check the layout at 1440px, 1024px and 390px widths.

- [ ] **Step 5: Commit**

```bash
git add tests/Harbora.Tests/LayoutConventionTests.cs
git commit -m "Add layout convention tests for RTL, translation and the honesty gate"
```

---

## Self-review notes

- **Spec coverage:** §2 tokens → Task 1. §3 shell → Tasks 6, 7, 8. §4 navigation → Task 3, 6.
  §5 components → Tasks 4, 5. §5.1 honesty gate → Task 2, enforced by Task 10. §7 testing → Tasks
  1, 2, 3, 5, 10. Icons were implied by the mockups but unnamed in the spec; Task 9 covers them.
- **Two Razor limitations were found while writing this plan and are resolved inline:** a partial
  cannot call `RenderBody` (Task 4 splits `_Panel` into start/end partials), and a section cannot be
  rendered twice (Task 8 uses one container reordered by CSS instead of two).
- **Deferred deliberately:** the collapse-to-icon-rail control, the `⌘K` command palette behaviour,
  and the notification bell's contents. The markup reserves their places; their behaviour belongs
  with the pages and data they act on, in sub-projects B and C.
