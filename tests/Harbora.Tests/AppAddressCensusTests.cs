using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Every place that creates an app hands it to the one address rule.
///
/// This suite exists because fixing the four paths fixes today. There were four, they disagreed, and
/// nobody noticed until somebody asked why a cloned app had no URL. The fifth path — added next
/// quarter by somebody who never read the spec — is what this catches, on the day it is written.
///
/// Reads the source rather than a list kept by hand, for the reason DetailTabCensusTests gives: a
/// hand-kept list is checked by a reviewer noticing an addition is missing from it, and a reviewer
/// noticing is exactly the step a real gap slips past.
/// </summary>
public class AppAddressCensusTests
{
    /// <summary>
    /// Files that create an app and are exempt, each with the reason. Keep this empty if you can — an
    /// entry here is a path where the guarantee does not hold, and it should read like one.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new();

    /// <summary>
    /// Matches <c>db.Apps.Add(</c> specifically, not the looser <c>.Apps.Add(</c>. All four real
    /// creation paths add to the EF <c>DbSet&lt;App&gt;</c> through a variable named <c>db</c> — that
    /// name is consistent across every controller and service in this codebase, so requiring it is not
    /// a coincidence being relied on. The looser pattern has a live false positive today:
    /// <c>MonitoringController.cs</c> builds a dashboard view model with a property also named
    /// <c>Apps</c>, and calls <c>vm.Apps.Add(new AppHealth(...))</c> to populate it — no database row,
    /// no address to assign, and no <c>AssignAsync</c> anywhere in the file. Under the looser pattern
    /// that file would show up as a "creator" that never assigns an address, and the census would fail
    /// on a page that was never broken. Anchoring to <c>db.Apps.Add(</c> removes that false alarm
    /// without narrowing what the census is actually guarding.
    /// </summary>
    private static readonly Regex AppRowCreated = new(@"\bdb\.Apps\.Add\(", RegexOptions.Compiled);

    [Fact]
    public void Every_file_that_adds_an_app_to_the_database_also_assigns_its_address()
    {
        var roots = new[] { TestPaths.WebRoot, TestPaths.InfrastructureRoot };

        var creators = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path: f, Text: File.ReadAllText(f)))
            .Where(f => AppRowCreated.IsMatch(f.Text))
            .ToList();

        creators.Should().NotBeEmpty(
            "a regex that matches nothing would pass this test for ever — there are at least four such files");

        var missing = creators
            .Where(f => !Exempt.ContainsKey(Path.GetFileName(f.Path)))
            .Where(f => !f.Text.Contains("AssignAsync", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f.Path))
            .ToList();

        missing.Should().BeEmpty(
            "a path that creates an app without assigning its address is how this project ended up with " +
            "four different answers to one question — add the AssignAsync call, or an Exempt entry saying why not");
    }
}
