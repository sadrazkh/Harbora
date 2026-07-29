using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which source `harbora deploy` actually uses. Getting this wrong means deploying something other
/// than what the user asked for — the most expensive kind of CLI bug — so the precedence is pinned
/// rather than left to read off the implementation.
/// </summary>
public class DeployPlanTests
{
    private static DeployPlan.Choice Decide(
        string? image = null, string? tar = null, string? branch = null, string? gitRef = null,
        bool push = false, ProjectConfig? config = null, bool gitRepo = false)
        => DeployPlan.Decide(image, tar, branch, gitRef, push, config ?? new ProjectConfig(), gitRepo);

    [Fact]
    public void An_image_flag_builds_nothing()
    {
        var choice = Decide(image: "nginx:alpine");

        choice.Mode.Should().Be(DeployMode.Image);
        choice.Value.Should().Be("nginx:alpine");
    }

    [Fact]
    public void A_tarball_is_uploaded_as_given()
    {
        Decide(tar: "build.tar.gz").Mode.Should().Be(DeployMode.PushTarball);
    }

    [Fact]
    public void A_branch_is_archived_and_uploaded()
    {
        Decide(branch: "main").Mode.Should().Be(DeployMode.PushGitBranch);
    }

    [Fact]
    public void A_ref_means_the_server_pulls()
    {
        // Naming a ref is an unambiguous "deploy what the remote has", not "upload my folder".
        Decide(gitRef: "v1.2.0").Mode.Should().Be(DeployMode.ServerGit);
    }

    [Fact]
    public void Flags_beat_config()
    {
        var config = new ProjectConfig { Image = "from-config:1" };

        Decide(image: "from-flag:1", config: config).Value.Should().Be("from-flag:1");
    }

    [Fact]
    public void Config_supplies_a_default_when_no_flag_does()
    {
        Decide(config: new ProjectConfig { Image = "nginx:alpine" }).Mode.Should().Be(DeployMode.Image);
        Decide(config: new ProjectConfig { Branch = "release" }).Mode.Should().Be(DeployMode.PushGitBranch);
    }

    [Fact]
    public void A_folder_with_no_git_is_pushed()
    {
        // The server has no remote to pull from, so uploading is the only thing that could work.
        var choice = Decide(gitRepo: false);

        choice.Mode.Should().Be(DeployMode.PushFolder);
        choice.Reason.Should().Contain("nothing to pull");
    }

    [Fact]
    public void A_git_checkout_still_defers_to_the_server()
    {
        // Keeps `harbora deploy` inside a repo meaning what it always meant.
        Decide(gitRepo: true).Mode.Should().Be(DeployMode.ServerGit);
    }

    [Fact]
    public void Push_forces_an_upload_even_inside_a_repo()
    {
        Decide(push: true, gitRepo: true).Mode.Should().Be(DeployMode.PushFolder);
    }

    [Fact]
    public void Every_choice_explains_itself()
    {
        // The CLI prints this; "why did it do that" should never require reading the source.
        foreach (var choice in new[]
                 {
                     Decide(image: "x"), Decide(tar: "x"), Decide(branch: "x"),
                     Decide(gitRef: "x"), Decide(push: true), Decide(gitRepo: true), Decide()
                 })
        {
            choice.Reason.Should().NotBeNullOrWhiteSpace();
        }
    }
}
