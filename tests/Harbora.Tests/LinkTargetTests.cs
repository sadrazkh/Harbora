using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Hand-written hrefs that point at a route form the target controller does not expose.
///
/// A hard-coded <c>href</c> compiles, renders and looks identical to a working link — MVC only
/// checks that a route resolves when something actually requests it. Three of these were found by
/// reading every controller's route attributes against every href that names it; the fourth is the
/// general form of the same defect, so a new view cannot reintroduce the specific 404 that shipped
/// with <c>Views/Networks/Index.cshtml</c>.
/// </summary>
public class LinkTargetTests
{
    private static string ViewsRoot => Path.Combine(TestPaths.WebRoot, "Views");

    private static string View(params string[] parts) =>
        File.ReadAllText(Path.Combine([ViewsRoot, .. parts]));

    private static IEnumerable<string> AllViews() =>
        Directory.EnumerateFiles(ViewsRoot, "*.cshtml", SearchOption.AllDirectories);

    [Fact]
    public void The_notification_bell_does_not_link_to_the_post_only_alerts_route()
    {
        // AlertsController declares only [HttpPost] routes ("", "{id}/test", "{id}/delete") — a GET
        // to /alerts, which is exactly what clicking a plain <a href> does, is a guaranteed 404 on
        // every page in the panel. The bell's badge counts this person's unread notifications now
        // (N3), so it points at NotificationsController's own [HttpGet("")] route rather than at the
        // rule-management section on /monitoring, which is where it used to send M4's open-incident
        // count before that count moved to the timeline's own badge there.
        var markup = View("Shared", "Design", "_Topbar.cshtml");

        markup.Should().NotContain("href=\"/alerts\"",
            "AlertsController has no GET route at /alerts — the bell would 404 on every page");
        markup.Should().Contain("href=\"/notifications\"",
            "the bell counts this person's unread notifications, so it should open their inbox");
    }

    [Fact]
    public void No_view_links_to_the_conventional_databases_details_route()
    {
        // DatabasesController.Details is attribute-routed at "/databases/{id:guid}"
        // (DatabasesController.cs: [HttpGet("{id:guid}")]) — "/databases/details/{id}", the form every
        // conventionally-routed controller uses, was never a route this controller answers to.
        // General over every view, not just Networks/Index.cshtml, so a new caller cannot reintroduce
        // the same guess.
        var offenders = AllViews()
            .Where(p => File.ReadAllText(p).Contains("/databases/details/"))
            .Select(p => Path.GetRelativePath(ViewsRoot, p))
            .ToList();

        offenders.Should().BeEmpty(
            "DatabasesController.Details is routed at /databases/{id}, not /databases/details/{id}");
    }

    [Fact]
    public void The_terminal_breadcrumb_links_to_the_route_AppsController_actually_answers()
    {
        // AppsController.Details carries no [Route]/[HttpGet] attribute, so it falls back to the
        // conventional default route "{controller}/{action}/{id?}" — the working URL is
        // /apps/details/{id}. A bare /apps/{id} asks the default route to treat the id as an action
        // name, which AppsController has none of, so it 404s. Every other view already gets this
        // right; the breadcrumb on the terminal page did not.
        var markup = View("Terminal", "Index.cshtml");

        markup.Should().Contain("href=\"/apps/details/@app.Id\"",
            "AppsController.Details is conventionally routed at /apps/details/{id}");
    }
}
