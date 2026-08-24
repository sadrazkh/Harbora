using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The gap between "a fix lands on master" and "a user's <c>harbora update</c> gets it".
///
/// <para>
/// Verified, not assumed: the last hand-typed tag was <c>v0.4.0</c> (2026-08-10); 369 commits sat on
/// master after it, including the three fixes that made a real deploy fail today, none of them
/// released. Worse, <c>Directory.Build.props</c>' <c>&lt;Version&gt;</c> — the number the panel's own
/// <c>GET /api/v1/version</c> and the CLI's own <c>--version</c> both report — was last touched for
/// <c>v0.2.0</c> and never bumped for <c>v0.3.0</c> or <c>v0.4.0</c>, so a CLI or panel built from
/// either of those tags (or from any commit since) silently claimed to be <c>0.2.0</c>. That is the
/// exact bug that neutered <see cref="VersionNotice"/>: comparing a panel's own <c>0.2.0</c> against
/// a CLI's own <c>0.2.0</c> always says "not behind", regardless of how many real releases separate
/// them.
/// </para>
///
/// <para>
/// This suite does not re-test <c>SelfUpdate.IsNewer</c> (covered by <c>SelfUpdateTests</c>) — it
/// checks the wiring around it: that the release workflow now stamps a build with the tag that
/// triggered it rather than a hand-maintained number, that a version bump on master is tagged
/// automatically, and that the staleness notice fires from more than the one command it used to.
/// </para>
/// </summary>
public class CliReleaseWiringTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return dir!.FullName;
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    [Fact]
    public void Directory_build_props_no_longer_claims_the_version_of_two_releases_ago()
    {
        var props = Read("Directory.Build.props");
        var match = Regex.Match(props, @"<Version>([^<]+)</Version>");
        match.Success.Should().BeTrue("this is the one number the panel and the CLI both report");

        SelfUpdate.TryParse(match.Groups[1].Value, out var version).Should().BeTrue();
        SelfUpdate.TryParse("0.4.0", out var lastRealTag).Should().BeTrue();

        (version > lastRealTag).Should().BeTrue(
            $"Directory.Build.props says {match.Groups[1].Value}, but v0.4.0 is already tagged — a " +
            "product version claiming to be behind (or equal to) an already-released tag is the exact " +
            "drift that made every build since v0.2.0 misreport itself");
    }

    [Fact]
    public void The_release_workflow_stamps_the_binaries_with_the_tag_that_triggered_them()
    {
        var workflow = Read(".github", "workflows", "release-cli.yml");

        workflow.Should().Contain("GITHUB_REF_NAME#v",
            "the version a released binary reports must come from the tag that published it, not from " +
            "whichever number Directory.Build.props happened to hold on that commit");

        workflow.Should().Contain("-p:Version=${{ steps.version.outputs.value }}",
            "deriving the version is only useful if dotnet publish is actually told to use it");
    }

    [Fact]
    public void The_dotnet_tool_package_is_stamped_the_same_way_as_the_binaries()
    {
        var workflow = Read(".github", "workflows", "release-cli.yml");

        // dotnet pack is a separate job in the same workflow — easy to fix one and miss the other,
        // which would leave `dotnet tool install -g Harbora.Cli` resolving to a differently-versioned
        // package than the binaries published in the same release. The step is a folded (">") YAML
        // block, so its arguments span two lines.
        var packStep = Regex.Match(workflow, @"dotnet pack[\s\S]{0,120}", RegexOptions.None);
        packStep.Success.Should().BeTrue();
        packStep.Value.Should().Contain("-p:Version=${{ steps.version.outputs.value }}");
    }

    [Fact]
    public void A_version_bump_on_master_is_tagged_automatically()
    {
        // Directory.Build.props' own comment already describes the process: bump the number, tag it,
        // the tag publishes the CLI. This is that second step, automated — not an invented process.
        var workflow = Read(".github", "workflows", "tag-release.yml");

        workflow.Should().Contain("branches: [master]");
        workflow.Should().Contain("Directory.Build.props",
            "it must trigger on exactly the file whose <Version> is the thing being released");
        workflow.Should().Contain("git push origin \"$tag\"");
    }

    [Fact]
    public void The_tagging_workflow_does_not_re_tag_a_version_already_released()
    {
        var workflow = Read(".github", "workflows", "tag-release.yml");

        workflow.Should().MatchRegex(@"git rev-parse.*>/dev/null",
            "Directory.Build.props can change on master for reasons that are not a new release (a " +
            "revert, a merge); re-tagging an existing version would fail loudly on every such push " +
            "instead of recognising nothing new needs releasing");
    }

    // ---- VersionNotice reaches more than one command -------------------------------------------

    [Theory]
    [InlineData("WhoAmICommand")]
    [InlineData("AppsCommand")]
    [InlineData("StatusCommand")]
    public void The_staleness_notice_also_fires_from_everyday_commands_not_just_deploy(string commandClass)
    {
        // VersionNotice.MaybeWarnAsync already existed for `deploy` alone. A CLI that only learns it
        // is stale once in a while, on the one command that happens to run longest, is not "a CLI
        // that knows it is stale on every command" — broadening its reach to the commands someone
        // runs just to check in (whoami, apps, status) costs one line each and no new mechanism.
        var source = Read("src", "Harbora.Cli", "Commands.cs");
        var classStart = source.IndexOf($"class {commandClass}", StringComparison.Ordinal);
        classStart.Should().BeGreaterThan(-1, $"{commandClass} must exist");

        // Bounded to this class's own body (up to the next top-level class), so a call anywhere else
        // in the file cannot make this pass for the wrong reason.
        var nextClass = source.IndexOf("\npublic sealed class ", classStart + 1, StringComparison.Ordinal);
        var body = nextClass > -1 ? source[classStart..nextClass] : source[classStart..];

        body.Should().Contain("VersionNotice.MaybeWarnAsync",
            $"{commandClass} authenticates and completes real work but never told anyone it was " +
            "running a stale CLI");
    }

    [Fact]
    public void Cancel_deliberately_does_not_get_the_notice()
    {
        // The one command this was NOT added to, on purpose: docs/cli-deploy.md promises `harbora
        // cancel` is safe in a pipeline and "never asks a question" — an extra network round-trip
        // there is exactly the kind of surprise that promise exists to rule out, and
        // CliCancelTests.Cancel_posts_to_the_deployments_cancel_endpoint already pins it to exactly
        // one HTTP call.
        var source = Read("src", "Harbora.Cli", "Commands.cs");
        var classStart = source.IndexOf("class CancelCommand", StringComparison.Ordinal);
        var nextClass = source.IndexOf("\npublic sealed class ", classStart + 1, StringComparison.Ordinal);
        var body = source[classStart..(nextClass > -1 ? nextClass : source.Length)];

        body.Should().NotContain("VersionNotice.MaybeWarnAsync");
    }
}
