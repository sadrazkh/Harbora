using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Navigation;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What Simple mode does inside a page.
///
/// It folds; it does not remove. A form that quietly drops fields between modes is one where the
/// settings a person gets depend on a preference they set weeks ago, with nothing on screen saying
/// so — and the brief for this work prohibits removing features from the advanced panel outright.
/// </summary>
public class PanelSectionTests
{
    [Fact]
    public void Advanced_mode_opens_specialist_blocks()
    {
        PanelSections.StartsOpen(PanelMode.Advanced).Should().BeTrue();
    }

    [Fact]
    public void Simple_mode_folds_them()
    {
        PanelSections.StartsOpen(PanelMode.Simple).Should().BeFalse();
    }

    [Fact]
    public void A_rejected_form_opens_them_in_either_mode()
    {
        // The case that matters. Half of these fields are required for some source types, so a
        // folded block over a rejected field is a form reporting an error about a control the
        // person cannot see — and re-reading the page will never show it to them.
        PanelSections.StartsOpen(PanelMode.Simple, hasErrors: true).Should().BeTrue();
        PanelSections.StartsOpen(PanelMode.Advanced, hasErrors: true).Should().BeTrue();
    }

    // ---- the markup that depends on it ----

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull();
        return dir!.FullName;
    }

    private static string View(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), "src", "Harbora.Web", "Views", .. parts]));

    [Fact]
    public void Every_folding_block_takes_its_open_state_from_the_shared_decision()
    {
        // Scoped to advanced-panel: the other <details> in the panel are dropdowns, accordions and
        // an FAQ, and none of them is a Simple/Advanced fold.
        //
        // Two ways to get this wrong, and both are here. A block with no `open` at all is folded for
        // everyone, in every mode, on a rejected form — which reads as "those settings were
        // removed". A block with its own `open="@(mode == Advanced)"` works today and silently stops
        // opening on a rejected form, because that rule already has a second condition.
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "src", "Harbora.Web", "Views"), "*.cshtml",
                     SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(path);

            foreach (Match match in Regex.Matches(markup, @"<details\b[^>]*advanced-panel[^>]*>",
                         RegexOptions.IgnoreCase))
            {
                var open = Regex.Match(match.Value, @"\bopen=""([^""]*)""", RegexOptions.IgnoreCase);

                if (!open.Success || !open.Groups[1].Value.Contains("Open", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(path)}: {match.Value.Trim()}");
            }
        }

        offenders.Should().BeEmpty("a folding block must take its open state from PanelSections");
    }

    [Fact]
    public void The_shared_disclosure_is_the_one_that_carries_the_decision()
    {
        // Asserted directly as well as by the sweep above, because this partial is the one every
        // page delegates to: if it stops binding, every fold closes at once and nothing else in the
        // suite would say why.
        View("Shared", "Design", "_AdvancedStart.cshtml").Should().Contain("open=\"@Model.Open\"");
    }

    [Fact]
    public void The_deploy_form_still_says_which_version_will_install_when_the_picker_is_folded()
    {
        // Folding takes away the choice, not the fact. Somebody in Simple mode still needs to know
        // what they are about to install — that is the whole reason versions exist.
        var markup = View("Templates", "Deploy.cshtml");

        markup.Should().Contain("!versionsOpen && selectedVersion is not null");
        markup.Should().Contain("selectedVersion.Version");
    }

    [Fact]
    public void The_application_form_keeps_every_advanced_field()
    {
        // The list this work must not shorten. Simple mode folds the block; nothing inside it may
        // stop being rendered.
        var markup = View("Apps", "Create.cshtml");

        foreach (var field in new[]
                 {
                     "Slug", "GitRef", "GitToken", "DockerfilePath", "ComposeFilePath",
                     "Kind", "ContainerPort", "ReleaseCommand", "Command", "CronExpression",
                     "PreviewsEnabled"
                 })
        {
            markup.Should().Contain($"asp-for=\"{field}\"", $"{field} must still be on the form");
        }
    }
}
