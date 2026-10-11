using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Agent;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Learning;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The learning-center guide for the in-panel assistant (<c>docs/tutorial/08-ai.md</c>) quotes numbers
/// the code owns — the daily default, the call ceiling, how long a plan waits, how many prompts the
/// model test sends. A guide that says "300" after the code moved to 500 is the one kind of wrong
/// documentation nobody notices until a customer acts on it, so the figures it states are read from
/// the same constants the assistant runs on and the build fails when they part.
///
/// <para>
/// Same reasoning as <see cref="LearningCensusTests"/>: assert against the real files and the real
/// constants, not against a copy of either written here.
/// </para>
/// </summary>
public class AssistantGuideTests
{
    private static readonly LearningLibrary Library = new(TestPaths.DocsRoot);

    private static string Chapter() => File.ReadAllText(Path.Combine(TestPaths.DocsRoot, "08-ai.md"));

    /// <summary>The chapter writes numbers in Persian digits, like every other chapter.</summary>
    private static string Fa(int number) =>
        string.Concat(number.ToString().Select(digit => (char)('۰' + (digit - '0'))));

    [Fact]
    public void The_locked_assistant_page_opens_the_ai_chapter_but_the_features_console_does_not()
    {
        // The topbar's locked toggle links here; the customer who lands on it is exactly who chapter 8
        // is for. The console beside it covers every sellable feature, so it must not claim to be
        // about the assistant.
        HelpMap.ChapterFor("/features/assistant").Should().Be("08-ai");
        HelpMap.ChapterFor("/features/admin").Should().BeNull();
        HelpMap.ChapterFor("/features/functions").Should().BeNull();
    }

    [Fact]
    public async Task The_ai_chapter_renders_the_assistant_guide_with_all_three_of_its_tables()
    {
        var html = await Library.ReadAsync("08-ai");

        html.Should().NotBeNull();
        Regex.IsMatch(html!, "<h2[^>]*>دستیار هوشمند پنل</h2>").Should().BeTrue(
            "the guide is a section of its own, under the exact heading the brief and the Help control use");
        html.Should().Contain("تست مدل عامل").And.Contain("سه سطح تأیید")
            .And.Contain("رمزها: دکمهٔ قفل").And.Contain("جواب، رفتن و برگشتن");

        // Pipe syntax that Markdig does not recognise as a table is shown as a paragraph of pipes: the
        // tools-by-kind table, the result-column table and the consent table are the three this guide has.
        Regex.Matches(html!, "<table").Count.Should().Be(3);
        html.Should().NotContain("|---").And.NotContain("| نتیجه").And.NotContain("| دسته");
    }

    [Fact]
    public void The_guides_figures_are_the_ones_the_assistant_runs_on()
    {
        var chapter = Chapter();

        chapter.Should().Contain($"خالی یعنی {Fa(AgentSettingKeys.DefaultDailyRequestLimit)}",
            "the default daily limit is the constant the settings page and the meter both read");
        chapter.Should().Contain($"حداکثر {Fa(AgentConversationService.MaxModelCallsPerMessage)} بار پرسیدن از مدل");
        chapter.Should().Contain($"**{Fa((int)AgentPlan.Lifetime.TotalMinutes)} دقیقه**");
        chapter.Should().Contain($"حداکثر {Fa(AgentConversationService.MaxMessageLength)} نویسه");
        chapter.Should().Contain($"{Fa(AgentConversationService.HistoryMessages)} پیام آخر");
        chapter.Should().Contain($"{Fa(AgentConversationService.ConversationListSize)} گفت‌وگوی آخر");

        // The lock button's vault and the turn that outlives the request.
        chapter.Should().Contain($"**{Fa((int)AgentSecretHandles.Lifetime.TotalMinutes)} دقیقه**",
            "the lock button's 30 minutes are the vault's lifetime");
        chapter.Should().Contain($"حداکثر {Fa(InMemoryAgentSecretVault.MaxPerPerson)} رمز همزمان");
        chapter.Should().Contain($"نام تا {Fa(AgentSecretService.MaxNameLength)} نویسه و مقدار تا {Fa(AgentSecretService.MaxValueLength)} نویسه");
        chapter.Should().Contain($"حداکثر {Fa((int)AgentConversationService.DefaultTurnDeadline.TotalMinutes)} دقیقه فرصت");

        // These two are written as words in the guide ("شش پرسش", "نه ابزار").
        AgentModelBench.Cases.Count.Should().Be(6);
        AgentModelBench.ToolSpecs.Count.Should().Be(9);
        chapter.Should().Contain("شش پرسش").And.Contain("نه ابزار");
    }
}
