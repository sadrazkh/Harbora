using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The defect Task 1's exploration found and Task 2 exists to close: the root <c>Dockerfile</c> copies
/// <c>Harbora.slnx</c>, <c>Directory.Build.props</c> and <c>src/</c> — never <c>docs/</c>. Every chapter
/// test in <c>LearningLibraryTests</c> and <c>LearnHttpTests</c> passes on this machine because
/// <c>docs/tutorial</c> sits on this disk regardless of what the Dockerfile says, so none of them can
/// catch the COPY line going missing. Only reading the Dockerfile itself can.
///
/// <para>
/// <b>What this suite proves, and what it cannot.</b> It is a text assertion on two files that ship —
/// the Dockerfile and .dockerignore — not a built image: this machine has no Docker to actually build
/// one against. It catches the COPY line, or the .dockerignore negation that keeps the chapters IN the
/// build context the COPY line reads from, being removed or renamed. It would not catch Docker's own
/// ignore-pattern matcher disagreeing with the regex reproduction here, and it would not catch the
/// COPY line being present but pointed at the wrong stage by a future multi-stage reshuffle it cannot
/// anticipate — the stage-scoping check below is the mitigation for today's shape, not a guarantee
/// against every reshuffle.
/// </para>
/// </summary>
public class LearningCentreDockerImageTests
{
    private static string DockerfileText => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "Dockerfile"));
    private static string DockerIgnoreText => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, ".dockerignore"));

    /// <summary>The runtime stage's own text, from its FROM line to the next top-level FROM (or EOF).</summary>
    private static string RuntimeStage()
    {
        var text = DockerfileText;
        var marker = "AS runtime";
        var markerAt = text.IndexOf(marker, StringComparison.Ordinal);
        markerAt.Should().BeGreaterThan(-1, "the Dockerfile defines a runtime stage — this test's own premise");

        var stageStart = text.LastIndexOf("\nFROM ", markerAt, StringComparison.Ordinal);
        stageStart.Should().BeGreaterThan(-1, "the runtime stage begins with its own FROM line");

        var nextStage = text.IndexOf("\nFROM ", stageStart + 1, StringComparison.Ordinal);
        return nextStage > -1 ? text[stageStart..nextStage] : text[stageStart..];
    }

    [Fact]
    public void The_runtime_stage_copies_the_tutorial_chapters_the_learning_centre_reads_at_runtime()
    {
        RuntimeStage().Should().MatchRegex(@"(?m)^COPY\s+docs[/\\]tutorial\b",
            "LearningLibrary (Harbora.Infrastructure/Learning/LearningLibrary.cs) reads docs/tutorial " +
            "off disk at runtime, never compiling it in — a Dockerfile that never copies it ships a " +
            "panel where /learn finds nothing, exactly as it did before this line existed, and every " +
            "chapter test still passes because docs/tutorial is on THIS disk regardless");
    }

    [Fact]
    public void The_chapters_are_copied_into_the_runtime_stage_not_only_the_build_stage()
    {
        // The build stage never needs the chapters — LearningLibrary is a runtime read, not a compile
        // input — so a COPY that only landed there would build cleanly and still ship an empty /learn.
        var text = DockerfileText;
        var buildStageEnd = text.IndexOf("AS runtime", StringComparison.Ordinal);
        var buildStage = text[..buildStageEnd];

        buildStage.Should().NotMatchRegex(@"(?m)^COPY\s+docs[/\\]tutorial\b",
            "this pins today's shape (the copy belongs in the runtime stage) so a future move that " +
            "drops the runtime-stage COPY while accidentally leaving a build-stage one is still caught " +
            "by the sibling test above, rather than this one silently agreeing either placement is fine");
    }

    [Fact]
    public void The_dockerignore_does_not_let_its_own_blanket_markdown_rule_swallow_the_chapters_the_image_copies()
    {
        var dockerignore = DockerIgnoreText;

        dockerignore.Should().Contain("*.md",
            "this test's premise is the blanket rule that would otherwise drop every chapter " +
            "from the build context before the Dockerfile's COPY ever runs");

        dockerignore.Should().MatchRegex(@"(?m)^!docs[/\\]tutorial[/\\]\*\.md",
            "without a negation after the blanket *.md rule, Docker drops docs/tutorial's chapter " +
            "files from the build context — the runtime image would still gain img/ (COPY line " +
            "restored) but none of the text, the same defect reappearing one file over");
    }
}
