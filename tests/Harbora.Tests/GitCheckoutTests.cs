using FluentAssertions;
using Harbora.Infrastructure.Git;
using LibGit2Sharp;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Checkout must hand back the WORKING TREE, not the .git directory.
///
/// It didn't. <c>Repository.Clone</c> returns the path of the .git folder with a trailing separator,
/// and <c>Path.GetDirectoryName</c> only strips that empty trailing segment — so the pipeline
/// received "….git" and looked for a Dockerfile, a package.json, a go.mod inside the metadata
/// folder. Every Git-sourced deployment failed with "the stack couldn't be auto-detected", and no
/// unit test could see it because the bug only exists once a real clone has happened.
///
/// These tests clone a genuine local repository — no network, but real libgit2.
/// </summary>
public class GitCheckoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harbora-git-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;

    public GitCheckoutTests()
    {
        _origin = Path.Combine(_root, "origin");
        Directory.CreateDirectory(_origin);

        Repository.Init(_origin);
        File.WriteAllText(Path.Combine(_origin, "package.json"), """{ "name": "demo", "version": "1.0.0" }""");
        File.WriteAllText(Path.Combine(_origin, "index.js"), "console.log('hi');");

        using var repo = new Repository(_origin);
        Commands.Stage(repo, "*");
        var who = new Signature("Harbora Test", "test@harbora.local", DateTimeOffset.UtcNow);
        repo.Commit("initial", who, who);
    }

    private async Task<Harbora.Application.Abstractions.GitCheckout> CheckoutAsync()
    {
        var service = new LibGit2GitService();
        var workDir = Path.Combine(_root, "work");
        return await service.CheckoutAsync(
            _origin, "master", credentialToken: null, workDir,
            new Progress<string>(_ => { }), default);
    }

    [Fact]
    public async Task The_checkout_path_is_the_working_tree_not_the_git_folder()
    {
        var checkout = await CheckoutAsync();

        Path.GetFileName(checkout.LocalPath).Should().NotBe(".git",
            "the pipeline builds from this path — pointing it at .git makes every Git deploy fail");
    }

    [Fact]
    public async Task Source_files_are_visible_at_the_checkout_path()
    {
        // This is precisely what Buildpacks.Detect does, and what failed in production.
        var checkout = await CheckoutAsync();

        File.Exists(Path.Combine(checkout.LocalPath, "package.json")).Should().BeTrue();
        File.Exists(Path.Combine(checkout.LocalPath, "index.js")).Should().BeTrue();
    }

    [Fact]
    public async Task The_buildpack_can_detect_a_stack_from_the_checkout()
    {
        var checkout = await CheckoutAsync();

        var pack = Harbora.Infrastructure.Deployments.Buildpacks.Detect(checkout.LocalPath, 3000);

        pack.Should().NotBeNull("a repository with package.json is a Node app");
        pack!.Value.Stack.Should().Be("Node.js");
    }

    [Fact]
    public async Task The_commit_metadata_is_captured()
    {
        var checkout = await CheckoutAsync();

        checkout.CommitSha.Should().NotBeNullOrWhiteSpace();
        checkout.CommitMessage.Should().Be("initial");
        checkout.CommitAuthor.Should().Be("Harbora Test");
    }

    public void Dispose()
    {
        // git objects are read-only on Windows; clear the attribute before deleting.
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* temp dir — best effort */ }
    }
}
