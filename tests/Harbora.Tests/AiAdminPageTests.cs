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
    public void Using_the_AI_service_stays_available_in_simple_mode()
    {
        // The counterpart to the test above: hiding the administration screen must not take the
        // customer-facing page with it.
        var simple = NavigationMap.VisibleTo(NavigationMap.All, _ => true, PanelMode.Simple);

        simple.SelectMany(g => g.Items).Should().Contain(i => i.Controller == "Ai");
    }
}
