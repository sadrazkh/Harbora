using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The census over the two detail-page tab strips, following <c>StartPathCensusTests</c>
/// (<c>tests/Harbora.Tests/Billing/BillingGateTests.cs</c>): a tab strip can look completely right and
/// not be. A link pointing at an action that does not exist gives a 404 nobody tried; an action with no
/// link is a page nobody finds. Both pass every other test in the suite.
///
/// <para>
/// Every assertion here reads the shell view or the controller's own route attributes off disk rather
/// than off a list maintained by hand — the same reasoning the docstring on
/// <c>StartPathCensusTests</c> gives: a hand-kept list is checked by a reviewer noticing an addition is
/// missing from it, and a reviewer noticing is exactly the step a real gap slips past. Add a fifth tab
/// to either shell or its route file and this suite is what notices, on the day it happens, without
/// anyone updating a list here first.
/// </para>
///
/// <para>
/// Deliberately silent about the count. Apps has four tabs and Databases has three — Databases lost its
/// Backups tab in commit 4514ad5 because <c>BackupsController.Index</c> takes no filter and
/// <c>/backups</c> is workspace-wide — and nothing below asserts either number. A hard-coded count is
/// exactly the kind of hand-maintained fact this file exists to avoid needing.
/// </para>
/// </summary>
public class DetailTabCensusTests
{
    private static string ViewPath(string relativeToViews) =>
        Path.Combine(TestPaths.WebRoot, "Views", relativeToViews.Replace('/', Path.DirectorySeparatorChar));

    private static string ControllerSource(string fileName) =>
        File.ReadAllText(Path.Combine(TestPaths.WebRoot, "Controllers", fileName));

    /// <summary>Matches a route template that is nothing but "…/{id:guid}/word" — the shape every
    /// tab href in a shell's <c>tabs = [ … ]</c> array takes. A multi-segment template like
    /// <c>apps/{id:guid}/logs/data</c> does not match: the strip never links to those directly, so a
    /// route like it must not silently widen what counts as a reachable tab.</summary>
    private static readonly Regex TabRouteSegment = new(@"\{id:guid\}/(?<tab>[a-z]+)$", RegexOptions.Compiled);

    /// <summary>
    /// The tab names a controller's own <c>[HttpGet]</c> route templates actually answer to — read
    /// from the attribute's <see cref="Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider.Template"/>,
    /// not from the method's name. The two can differ (a method named <c>Usage</c> could carry the
    /// route <c>"…/consumption"</c>), and a census built on the name would call a link to the old
    /// route "fine" the day the route moved and the method did not.
    ///
    /// <para>
    /// <see cref="BindingFlags.DeclaredOnly"/> keeps this to methods the controller itself declares —
    /// across every partial-class file it is split into, since a partial class is one type at
    /// runtime — rather than the ~80 public methods it inherits from <c>Controller</c>,
    /// <c>ControllerBase</c> and <c>object</c> (<c>Content</c>, <c>Json</c>, <c>View</c>,
    /// <c>ToString</c>…). Without it, a tab href of <c>/apps/{id}/json</c> would pass this census
    /// while 404-ing for real.
    /// </para>
    /// </summary>
    private static HashSet<string> RouteTabs<TController>() where TController : Controller =>
        typeof(TController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>(inherit: false))
            .Select(a => a.Template)
            .Where(t => t is not null)
            .Select(t => TabRouteSegment.Match(t!))
            .Where(m => m.Success)
            .Select(m => m.Groups["tab"].Value)
            .ToHashSet();

    /// <summary>
    /// Just the <c>tabs = [ … ];</c> declaration out of a shell — the strip itself, not the file it
    /// lives in. The difference is not pedantry: the database shell's header already carries an
    /// "External access" button pointing at <c>/databases/{id}/access</c>, so a test asking whether the
    /// whole file mentions <c>/access</c> answers yes whether or not the Access tab is in the strip. It
    /// would go on saying yes the day somebody deleted that tab. Reading the array is what makes the
    /// reachability assertion below about the strip that is supposed to offer the tab.
    /// </summary>
    private static string TabStrip(string shell)
    {
        var start = shell.IndexOf("tabs =", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "every shell declares its strip as a `tabs = [ … ];` array");

        var end = shell.IndexOf("];", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "the strip declaration must be closed");

        return shell[start..end];
    }

    // ---- Apps ----

    /// <summary>
    /// Every href the app shell's tab strip hands out under <c>/apps/{id}/…</c> must land on a method
    /// <c>AppsController</c> actually has. The Overview tab is not part of this: its href is
    /// <c>/apps/details/{Model.Id}</c>, a different shape entirely, because it is the page the shell
    /// is already rendered on — not a link that could 404 the way the others could.
    /// </summary>
    [Fact]
    public void Every_tab_the_app_shell_links_to_is_an_action_that_exists()
    {
        var shell = File.ReadAllText(ViewPath("Apps/_Shell.cshtml"));
        var linked = Regex.Matches(shell, @"\$""/apps/\{Model\.Id\}/(?<tab>[a-z]+)""")
            .Select(m => m.Groups["tab"].Value).ToList();

        linked.Should().NotBeEmpty("a regex that matches nothing would pass this test for ever");

        var actions = RouteTabs<AppsController>();

        linked.Should().OnlyContain(tab => actions.Contains(tab),
            "a tab pointed at an action that does not exist is a 404 nobody ever tries");
    }

    /// <summary>
    /// The other direction. A GET action declared in <c>AppsController.Tabs.cs</c> under
    /// <c>apps/{id:guid}/…</c> — the file that exists specifically to hold the app's tab actions — is a
    /// page somebody built. If the shell never links to it, it is a page nobody finds.
    /// </summary>
    [Fact]
    public void Every_app_tab_action_declared_in_its_route_file_is_reachable_from_the_shell()
    {
        var shell = TabStrip(File.ReadAllText(ViewPath("Apps/_Shell.cshtml")));
        var tabsSource = ControllerSource("AppsController.Tabs.cs");

        var declared = Regex.Matches(tabsSource, @"\[HttpGet\(""apps/\{id:guid\}/(?<tab>[a-z]+)""\)\]")
            .Select(m => m.Groups["tab"].Value).ToList();

        declared.Should().NotBeEmpty("a regex that matches nothing would pass this test for ever");

        foreach (var name in declared)
        {
            shell.Should().Contain($"/{name}", $"the {name} tab action exists and must be reachable from the shell");
        }
    }

    // ---- Databases ----

    /// <summary>
    /// Every href the database shell's tab strip hands out under <c>/databases/{id}/…</c> must land on
    /// a method <c>DatabasesController</c> actually has. Overview is excluded the same way, and for the
    /// same reason, as the app shell above: its href is the bare <c>/databases/{Model.Id}</c>, with no
    /// action segment to check because it is the page already on screen.
    /// </summary>
    [Fact]
    public void Every_tab_the_database_shell_links_to_is_an_action_that_exists()
    {
        var shell = File.ReadAllText(ViewPath("Databases/_Shell.cshtml"));
        var linked = Regex.Matches(shell, @"\$""/databases/\{Model\.Id\}/(?<tab>[a-z]+)""")
            .Select(m => m.Groups["tab"].Value).ToList();

        linked.Should().NotBeEmpty("a regex that matches nothing would pass this test for ever");

        var actions = RouteTabs<DatabasesController>();

        linked.Should().OnlyContain(tab => actions.Contains(tab),
            "a tab pointed at an action that does not exist is a 404 nobody ever tries");
    }

    /// <summary>
    /// The other direction, for both files a database tab action can live in. Usage is declared in
    /// <c>DatabasesController.Tabs.cs</c>; Access is declared in <c>DatabaseAccessActions.cs</c> — the
    /// comment at the top of <c>Tabs.cs</c> explains why it was left out of that file — so both are
    /// read here, the same way a person auditing "what tabs does this controller offer" would have to
    /// look in both.
    ///
    /// <para>
    /// There is no Backups entry to find in either file, because there is no Backups action: the tab
    /// was never built, on purpose (see the comment in <c>Databases/_Shell.cshtml</c>). A census that
    /// discovers zero Backups tab actions and has nothing to say about it is not a gap — it is this
    /// test agreeing with the file that there are three tabs, without being told the number.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_database_tab_action_declared_in_its_route_files_is_reachable_from_the_shell()
    {
        var shell = TabStrip(File.ReadAllText(ViewPath("Databases/_Shell.cshtml")));
        var tabsSource = ControllerSource("DatabasesController.Tabs.cs") + ControllerSource("DatabaseAccessActions.cs");

        var declared = Regex.Matches(tabsSource, @"\[HttpGet\(""\{id:guid\}/(?<tab>[a-z]+)""\)\]")
            .Select(m => m.Groups["tab"].Value).ToList();

        declared.Should().NotBeEmpty("a regex that matches nothing would pass this test for ever");

        foreach (var name in declared)
        {
            shell.Should().Contain($"/{name}", $"the {name} tab action exists and must be reachable from the shell");
        }
    }
}
