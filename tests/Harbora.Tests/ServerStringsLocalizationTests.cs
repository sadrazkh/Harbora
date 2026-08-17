using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Dashboard;
using Harbora.Web.Infrastructure;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Text that is composed on the server and shown to a person.
///
/// <see cref="PersianResourceTests"/> covers `T["…"]` literals in views. These are the strings that
/// route around that guard: keys built in C# (the attention panel) and words produced by a helper
/// (status badges). Both shipped in English on Persian pages — the dashboard's "Disk is filling up"
/// and the badges' "Succeeded" were the most-read words on their screens, in the wrong language.
/// </summary>
public class ServerStringsLocalizationTests
{
    private static readonly string Resource =
        Path.Combine(TestPaths.WebRoot, "Resources", "SharedResource.fa.resx");

    private static readonly Regex Persian = new(@"[؀-ۿ]", RegexOptions.Compiled);

    private static HashSet<string> ResourceNames() =>
        System.Xml.Linq.XDocument.Load(Resource).Root!
            .Elements("data")
            .Select(e => (string?)e.Attribute("name") ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_attention_key_has_a_persian_entry()
    {
        // The dashboard's attention panel emits these dynamically, so the view-literal scan cannot
        // see them. AllKeys is pinned to what Build actually emits by
        // AttentionRulesTests.Every_key_the_rules_can_emit_is_declared.
        var names = ResourceNames();

        AttentionRules.AllKeys.Should().NotBeEmpty();
        AttentionRules.AllKeys.Where(k => !names.Contains(k)).Should().BeEmpty(
            "an attention key with no entry renders the dashboard's most important line in English");
    }

    [Fact]
    public void Every_validation_message_has_a_persian_entry()
    {
        // DataAnnotations messages resolve through the shared resource too, and they are the words
        // a person sees at the exact moment a form refuses them. An unkeyed attribute falls back to
        // the framework's English; a keyed one with no entry falls back to its own English key.
        // Either way the refusal is in the wrong language, so: every message keyed, every key
        // translated.
        var names = ResourceNames();
        var viewModels = typeof(Harbora.Web.SharedResource).Assembly.GetTypes()
            .Where(t => t.Namespace == "Harbora.Web.ViewModels" && (t.IsClass || t.IsValueType))
            .ToList();

        viewModels.Should().NotBeEmpty();

        var missing = new SortedSet<string>();

        foreach (var type in viewModels)
            foreach (var property in type.GetProperties())
                foreach (var attribute in property.GetCustomAttributes(true)
                             .OfType<System.ComponentModel.DataAnnotations.ValidationAttribute>())
                {
                    if (attribute.ErrorMessage is { Length: > 0 } message && !names.Contains(message))
                        missing.Add($"{type.Name}.{property.Name}: {message}");
                }

        missing.Should().BeEmpty("a keyed validation message with no resx entry refuses in English");
    }

    [Fact]
    public void Every_deployment_status_reads_in_both_languages()
    {
        foreach (var status in Enum.GetValues<DeploymentStatus>())
        {
            var fa = StatusLabel.For(status, isFa: true);
            var en = StatusLabel.For(status, isFa: false);

            en.Should().NotBeNullOrWhiteSpace();
            Persian.IsMatch(fa).Should().BeTrue($"{status} must have a Persian word, got '{fa}'");
        }
    }

    [Fact]
    public void Every_app_status_reads_in_both_languages()
    {
        foreach (var status in Enum.GetValues<AppStatus>())
        {
            Persian.IsMatch(StatusLabel.For(status, isFa: true)).Should().BeTrue($"{status}");
            StatusLabel.For(status, isFa: false).Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Every_trigger_reads_in_both_languages()
    {
        foreach (var trigger in Enum.GetValues<DeploymentTrigger>())
        {
            // CLI is the one deliberate exception: it is a product term, the same in both.
            if (trigger == DeploymentTrigger.Cli) continue;

            Persian.IsMatch(StatusLabel.For(trigger, isFa: true)).Should().BeTrue($"{trigger}");
            StatusLabel.For(trigger, isFa: false).Should().NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>P5's own status chip on <c>/activity</c> reads from this table.</summary>
    [Fact]
    public void Every_job_status_reads_in_both_languages()
    {
        foreach (var status in Enum.GetValues<Harbora.Domain.Jobs.JobStatus>())
        {
            var fa = StatusLabel.For(status, isFa: true);
            var en = StatusLabel.For(status, isFa: false);

            en.Should().NotBeNullOrWhiteSpace();
            Persian.IsMatch(fa).Should().BeTrue($"{status} must have a Persian word, got '{fa}'");
        }
    }

    [Fact]
    public void Docker_container_states_read_in_both_languages()
    {
        foreach (var state in new[] { "running", "exited", "restarting", "paused", "created", "dead" })
            Persian.IsMatch(StatusLabel.Container(state, isFa: true)).Should().BeTrue(state);
    }

    [Fact]
    public void An_unknown_state_passes_through_rather_than_being_guessed_at()
    {
        // Docker can grow states we have never seen. Showing the raw word is honest; inventing a
        // translation labels it with a guess.
        StatusLabel.Container("hibernating", isFa: true).Should().Be("hibernating");
        StatusLabel.Deploy("NotAStatus", isFa: true).Should().Be("NotAStatus");
        StatusLabel.Deploy(null, isFa: true).Should().Be("");
    }

    [Fact]
    public void A_stored_status_name_round_trips_through_the_string_overload()
    {
        // Read models store the enum's name; the string overload must agree with the enum one.
        StatusLabel.Deploy("Succeeded", isFa: true)
            .Should().Be(StatusLabel.For(DeploymentStatus.Succeeded, isFa: true));
    }
}
