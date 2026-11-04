using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests.Launch;

/// <summary>
/// The pages of this flow say «کدت کجاست؟», "you" in the singular, the way a person talks. What the
/// service says in the middle of those pages has to be the same person talking. A census of the source
/// rather than of each message, because most of them are only reached by something going wrong.
/// </summary>
public sealed class LaunchPersianVoiceTests
{
    private static readonly string[] Speaking =
    [
        "src/Harbora.Infrastructure/Launch/LaunchAdvance.cs",
        "src/Harbora.Infrastructure/Launch/LaunchService.cs",
        "src/Harbora.Infrastructure/Launch/LaunchService.Advance.cs",
        "src/Harbora.Infrastructure/Launch/LaunchService.Confirm.cs",
        "src/Harbora.Infrastructure/Launch/LaunchService.Proposal.cs",
        "src/Harbora.Infrastructure/Launch/LaunchService.View.cs",
        "src/Harbora.Infrastructure/Launch/LaunchSource.cs",
        "src/Harbora.Infrastructure/Verification/AppVerifier.cs",
        "src/Harbora.Infrastructure/Verification/AppLogDiagnosis.cs",
        "src/Harbora.Infrastructure/Networking/ConnectionProbe.cs",
        "src/Harbora.Infrastructure/Deployments/SourceArchive.cs",
        "src/Harbora.Shared/Inspection/InspectionNotes.cs"
    ];

    /// <summary>The plural imperatives and the polite word that goes with them. Whole words: جدید and شاید end the same way.</summary>
    private static readonly Regex Plural = new(
        "(?<![\u0621-\u064A\u0670-\u06D3\u200C])(نکنید|کنید|بدهید|بسازید|بگذارید|ببینید|بخوانید|ببرید|بیندازید|بودید|لطفاً)(?![\u0621-\u064A\u0670-\u06D3\u200C])",
        RegexOptions.CultureInvariant);

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var file in Speaking) data.Add(file);
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Everything_said_to_a_person_here_says_you_in_the_singular(string file)
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, file));

        Plural.Matches(text).Select(m => m.Value).Should().BeEmpty(file);
    }

    [Theory]
    [InlineData("دوباره امتحان کنید.", true)]
    [InlineData("روشنش کنید، یا ببینید", true)]
    [InlineData("لطفاً دوباره", true)]
    [InlineData("دوباره امتحان کن.", false)]
    [InlineData("رمز جدید را بگیرند، شاید", false)]
    [InlineData("سرویس‌هایی که استفاده می‌کنند", false)]
    public void The_census_can_tell_them_apart(string text, bool plural)
    {
        Plural.IsMatch(text).Should().Be(plural);
    }
}
