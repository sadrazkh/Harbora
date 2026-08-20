using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Authorization;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The list of acts a support session may not perform, kept honest by reading the controllers
/// rather than a note somebody maintained.
///
/// <para>
/// The failure this exists for is not a missing attribute today — the HTTP tests cover the ones
/// that are there. It is the next action: somebody adds a way to change an email address, or a
/// second route that mints a token, and the refusal list silently stops covering what it claims to.
/// A hand-kept list would agree with itself forever, which is why every other census in this
/// codebase reads source too.
/// </para>
///
/// <para>
/// Anything whose name is about a credential or about money must either carry the attribute or be
/// on the allowlist below with a reason. The allowlist is the interesting half: each line is a
/// decision, and one that stops being true is a line somebody has to argue with.
/// </para>
/// </summary>
public class SupportRestrictionCensusTests
{
    /// <summary>
    /// The vocabulary of an act that takes an account away from its owner or moves money. Matched
    /// against the action method's own name.
    /// </summary>
    private static readonly Regex Sensitive = new(
        "Password|Totp|TwoFactor|RevokeSession|RevokeOtherSessions|CreateToken|VerifyEmail|"
        + "^Credit$|^Adjustment$|RedeemVoucher",
        RegexOptions.Compiled);

    /// <summary>
    /// Actions the vocabulary catches that are deliberately NOT refused, each with the reason.
    /// Keyed "Controller.Action".
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["AccountController.Reset"] =
            "The password-reset form reached from an emailed link. Anonymous — a support session has "
            + "no cookie on it, and it is no more reachable while impersonating than it is to anybody "
            + "else with the link.",
        ["AccountController.Forgot"] =
            "Asks the platform to email a reset link to an address. Anonymous and public; refusing it "
            + "under a support session would change nothing an administrator could not do signed out.",
        ["AccountController.Totp"] =
            "The two-factor challenge during sign-in. It is reached with no session at all — a support "
            + "session exists only after one has been established.",
        ["AccountController.VerifyEmail"] =
            "Spends an emailed verification token. Anonymous, and the proof is the token rather than "
            + "whoever's browser presents it.",
        ["AccountController.ResendVerification"] =
            "Anonymous; sends a fresh link to an address that already has an unverified account.",
        ["MailController.ResetPassword"] =
            "Rotates a managed MAILBOX's password, not an account's. It is an ordinary operational "
            + "act on a workspace resource — the same class of thing as restarting an app — and it "
            + "cannot take the customer's Harbora account away from them. The refusal list is "
            + "deliberately short: support usually has to do the thing to see it fail.",
        ["TenantsController.ConfirmCredit"] =
            "The confirmation PAGE, not the act. Left reachable on purpose: hiding the form is not a "
            + "control, and the POST behind it refuses — which is what a support person needs to see.",
        ["TenantsController.ConfirmAdjustment"] =
            "The same, for an adjustment: the page renders, the act refuses."
    };

    private static IEnumerable<(Type Controller, MethodInfo Action)> Actions() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(Controller).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.DeclaringType == t)
                .Select(m => (Controller: t, Action: m)));

    [Fact]
    public void The_census_actually_finds_controllers_to_read()
    {
        // Guards every other assertion here: an empty scan passes them all.
        Actions().Should().HaveCountGreaterThan(100);
        Actions().Select(a => a.Controller).Distinct().Should().HaveCountGreaterThan(20);
    }

    [Fact]
    public void Every_credential_or_money_action_is_either_refused_under_support_or_explained()
    {
        var unguarded = new List<string>();

        foreach (var (controller, action) in Actions())
        {
            if (!Sensitive.IsMatch(action.Name)) continue;

            var key = $"{controller.Name}.{action.Name}";
            if (Allowed.ContainsKey(key)) continue;
            if (action.GetCustomAttribute<RefuseUnderSupportSessionAttribute>() is not null) continue;

            unguarded.Add(key);
        }

        unguarded.Should().BeEmpty(
            "each of these changes a credential or moves money, so it either carries "
            + "[RefuseUnderSupportSession] or joins the allowlist in this test with the reason it does not");
    }

    [Fact]
    public void The_allowlist_names_only_actions_that_still_exist()
    {
        // An allowlist entry for a deleted action is a hole that reads as a decision.
        var present = Actions().Select(a => $"{a.Controller.Name}.{a.Action.Name}").ToHashSet(StringComparer.Ordinal);

        foreach (var key in Allowed.Keys)
            present.Should().Contain(key, $"{key} is allowlisted but no such action exists any more");
    }

    [Fact]
    public void Every_named_act_is_actually_refused_somewhere()
    {
        // The enum is the customer-facing vocabulary of what support cannot do, and the confirmation
        // page promises all six. A value nobody applies is a promise nothing keeps.
        var applied = Actions()
            .Select(a => a.Action.GetCustomAttribute<RefuseUnderSupportSessionAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Act)
            .Distinct()
            .ToList();

        applied.Should().BeEquivalentTo(Enum.GetValues<SupportRestrictedAct>());
    }
}
