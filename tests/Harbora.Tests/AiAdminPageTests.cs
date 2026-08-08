using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Navigation;
using Harbora.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The AI administration screen holds the platform's provider tokens. Its security properties live
/// in markup and attributes rather than in a rule object, so they are asserted where they are
/// written: a token that is rendered once is a token in a browser cache, a screen recording and
/// every support screenshot it ever appears in.
/// </summary>
public class AiAdminPageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return dir!.FullName;
    }

    private static string ViewSource() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "Harbora.Web", "Views", "AiAdmin", "Index.cshtml"));

    private static string ControllerSource() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "Harbora.Web", "Controllers", "AiAdminController.cs"));

    [Fact]
    public void The_page_never_renders_a_stored_token()
    {
        // The whole point of the write-only design. A field pre-filled with the current token so it
        // can be "edited" is the single change that would undo it, and it looks like a convenience.
        ViewSource().Should().NotContain("EncryptedToken");
    }

    [Fact]
    public void The_controller_never_decrypts_a_token()
    {
        // Protect is expected here; Unprotect is not. Only the gateway needs the plaintext, and it
        // needs it to make a call, not to fill in a form.
        ControllerSource().Should().NotContain("Unprotect");
    }

    [Fact]
    public void Every_token_field_is_a_password_field()
    {
        // A token typed into a plain text input is legible over a shoulder, captured by a screen
        // share, and offered back by the browser's form history on the next administrator's visit.
        var offenders = Regex.Matches(ViewSource(), @"<input\b[^>]*\bname=""token""[^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Select(m => m.Value)
            .Where(tag => !tag.Contains("type=\"password\"") || !tag.Contains("autocomplete=\"off\""))
            .ToList();

        offenders.Should().BeEmpty("a token field must be masked and must not be remembered");
        Regex.Matches(ViewSource(), @"\bname=""token""").Count
            .Should().BeGreaterThan(0, "the page must be able to add and rotate tokens");
    }

    [Fact]
    public void The_whole_controller_needs_the_platform_capability()
    {
        // Asserted on the type, not per action: an action added later inherits it, which is the
        // opposite of the usual failure where the new one is the only one left unguarded.
        var authorize = typeof(AiAdminController).GetCustomAttributes<AuthorizeAttribute>().ToList();

        authorize.Should().ContainSingle();
        authorize[0].Policy.Should().Be(Capabilities.PlatformManage);
    }

    [Fact]
    public void Every_mutating_action_validates_the_anti_forgery_token()
    {
        var unguarded = typeof(AiAdminController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null)
            .Where(m => m.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() is null)
            .Select(m => m.Name)
            .ToList();

        unguarded.Should().BeEmpty("a POST here changes provider credentials or pricing");
    }

    [Fact]
    public void The_administration_screen_is_reachable_from_the_sidebar()
    {
        // A page nobody can find is a page nobody uses, and the tokens then get configured by
        // whatever back door somebody improvises instead.
        NavigationMap.All.SelectMany(g => g.Items)
            .Should().ContainSingle(i => i.Controller == "AiAdmin");
    }

    [Fact]
    public void The_administration_screen_is_hidden_from_someone_without_the_capability()
    {
        var visible = NavigationMap.VisibleTo(c => c != Capabilities.PlatformManage);

        visible.SelectMany(g => g.Items).Should().NotContain(i => i.Controller == "AiAdmin");
    }

    [Fact]
    public void The_administration_screen_is_hidden_in_simple_mode()
    {
        // Simple mode hides it; the route stays live. Someone in Simple mode who follows a link from
        // a runbook still gets the page, subject to the same capability check.
        var simple = NavigationMap.VisibleTo(NavigationMap.All, _ => true, PanelMode.Simple);
        var advanced = NavigationMap.VisibleTo(NavigationMap.All, _ => true, PanelMode.Advanced);

        simple.SelectMany(g => g.Items).Should().NotContain(i => i.Controller == "AiAdmin");
        advanced.SelectMany(g => g.Items).Should().Contain(i => i.Controller == "AiAdmin");
    }

    [Fact]
    public void Using_the_AI_service_is_hidden_in_simple_mode_too_while_it_is_a_preview()
    {
        // This test used to assert the opposite, and the old expectation is corrected here rather
        // than deleted, so the change is a decision on the record instead of a test that quietly
        // stopped covering something.
        //
        // The reason it was written that way was sound: hiding the administration screen must not
        // take the customer-facing page with it. What was missing is that the gateway has never
        // made a request to a real provider. Everything up to the network boundary is covered;
        // nothing covers the last hop. Offering that to somebody who chose the simple panel — which
        // is a request for the parts that just work — is the sort of thing this project's own
        // delivery rules forbid.
        //
        // Folded, not removed, like every other Simple-mode decision: /ai answers in both modes.
        // When one live round-trip has been made and recorded (HARBORA-0054), the Advanced flag and
        // the preview block on the page come off together and this test goes back to what it was.
        var simple = NavigationMap.VisibleTo(NavigationMap.All, _ => true, PanelMode.Simple);
        var advanced = NavigationMap.VisibleTo(NavigationMap.All, _ => true, PanelMode.Advanced);

        simple.SelectMany(g => g.Items).Should().NotContain(i => i.Controller == "Ai");
        advanced.SelectMany(g => g.Items).Should().Contain(i => i.Controller == "Ai");
    }

    [Fact]
    public void The_AI_page_says_on_itself_that_it_is_a_preview()
    {
        // Hiding it in Simple mode is half the honesty. The other half is that somebody who finds
        // the page in Advanced mode, and is about to hand a key to a customer, is told what has and
        // has not been proven — on the page, not in a release note they will not read.
        var view = File.ReadAllText(Path.Combine(
            TestPaths.WebRoot, "Views", "Ai", "Index.cshtml"));

        view.Should().Contain("Preview",
            "src/Harbora.Web/Views/Ai/Index.cshtml must label the service a preview while no request " +
            "has been made to a real provider");
        view.Should().Contain("پیش‌نمایش",
            "the preview label must be bilingual, like every other notice in this panel");
        view.Should().Contain("never been run against a real provider",
            "the label has to say what is unproven, not merely that something is");
    }
}
