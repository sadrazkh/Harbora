using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Git;
using LibGit2Sharp;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A real clone of a real repository on disk. A blank ref has to mean the repository's own default
/// branch, and what comes back has to be its actual name: an app created tracking a form's <c>main</c>
/// on a repository that has only <c>trunk</c> deploys nothing on its next push.
/// </summary>
public sealed class LibGit2GitServiceBranchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harbora-git-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;
    private readonly LibGit2GitService _git = new();
    private static readonly IProgress<string> NoLog = new InlineProgress<string>(_ => { });

    public LibGit2GitServiceBranchTests()
    {
        _origin = Path.Combine(_root, "origin");
        Repository.Init(_origin);
        using var repo = new Repository(_origin);
        var who = new Signature("Tester", "tester@example.com", DateTimeOffset.UtcNow);

        File.WriteAllText(Path.Combine(_origin, "on-trunk.txt"), "trunk");
        Commands.Stage(repo, "*");
        repo.Commit("first", who, who);
        repo.Branches.Rename(repo.Head, "trunk");

        repo.ApplyTag("v1.0");

        var develop = repo.CreateBranch("develop");
        Commands.Checkout(repo, develop);
        File.WriteAllText(Path.Combine(_origin, "on-develop.txt"), "develop");
        Commands.Stage(repo, "*");
        repo.Commit("second", who, who);

        // HEAD is what a clone takes as the default branch.
        Commands.Checkout(repo, repo.Branches["trunk"]);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private string Work => Path.Combine(_root, "work");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_ref_is_the_repositorys_default_branch_by_its_real_name(string gitRef)
    {
        var checkout = await _git.CheckoutAsync(_origin, gitRef, null, Work, NoLog, default);

        checkout.Branch.Should().Be("trunk");
        File.Exists(Path.Combine(checkout.LocalPath, "on-trunk.txt")).Should().BeTrue();
        File.Exists(Path.Combine(checkout.LocalPath, "on-develop.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task A_named_branch_comes_back_by_its_short_name_and_not_as_no_branch()
    {
        var checkout = await _git.CheckoutAsync(_origin, "develop", null, Work, NoLog, default);

        checkout.Branch.Should().Be("develop", "a checkout of a remote-tracking branch detaches HEAD");
        File.Exists(Path.Combine(checkout.LocalPath, "on-develop.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task A_tag_stays_what_was_asked_for()
    {
        var checkout = await _git.CheckoutAsync(_origin, "v1.0", null, Work, NoLog, default);

        checkout.Branch.Should().Be("v1.0");
        File.Exists(Path.Combine(checkout.LocalPath, "on-develop.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task A_ref_that_is_not_there_falls_back_to_the_default_and_says_which_that_was()
    {
        var checkout = await _git.CheckoutAsync(_origin, "no-such-branch", null, Work, NoLog, default);

        checkout.Branch.Should().Be("trunk");
    }
}
