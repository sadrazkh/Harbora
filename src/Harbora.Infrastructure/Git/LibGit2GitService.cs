using Harbora.Application.Abstractions;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace Harbora.Infrastructure.Git;

/// <summary>
/// LibGit2Sharp-backed source checkout. Supports token auth (PAT sent as the username with an
/// empty password, which GitHub/GitLab/Gitea all accept over HTTPS).
/// </summary>
public sealed class LibGit2GitService : IGitService
{
    public Task<GitCheckout> CheckoutAsync(string cloneUrl, string gitRef, string? credentialToken, string workingDir, IProgress<string> log, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            if (Directory.Exists(workingDir))
                DeleteDirectory(workingDir);
            Directory.CreateDirectory(workingDir);

            var co = new CloneOptions { };
            co.FetchOptions.CredentialsProvider = BuildCredentials(credentialToken);
            co.FetchOptions.OnProgress = p => { log.Report(p); return !ct.IsCancellationRequested; };

            log.Report($"Cloning {cloneUrl} …");
            var path = Repository.Clone(cloneUrl, workingDir, co);

            using var repo = new Repository(path);
            var reference = ResolveRef(repo, gitRef, credentialToken, log);
            Commands.Checkout(repo, reference);

            var commit = repo.Head.Tip ?? reference.Tip;
            var branch = CheckedOutName(repo, reference, gitRef);
            log.Report($"Checked out {branch} @ {commit.Sha[..7]}");
            return new GitCheckout(
                commit.Sha,
                commit.MessageShort,
                commit.Author.Name,
                // Repository.Clone returns the path to the .git DIRECTORY, with a trailing separator.
                // Path.GetDirectoryName only strips that empty trailing segment, so it yielded
                // ".../.git" — the metadata folder, not the source. Every Git deploy then looked for
                // a Dockerfile (or a buildpack marker) inside .git and found nothing. Ask libgit2 for
                // the working tree instead of deriving it from a string.
                repo.Info.WorkingDirectory?
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                ?? workingDir,
                branch);
        }, ct);
    }

    /// <summary>
    /// The name of what is now checked out. Not <c>repo.Head.FriendlyName</c>: a checkout of a
    /// remote-tracking branch detaches HEAD, and that reads "(no branch)". A tag stays what was asked for.
    /// </summary>
    private static string CheckedOutName(Repository repo, Branch reference, string gitRef)
    {
        if (reference.IsRemote)
        {
            var name = reference.FriendlyName;
            var remote = reference.RemoteName;
            return name.StartsWith(remote + "/", StringComparison.Ordinal) ? name[(remote.Length + 1)..] : name;
        }

        return repo.Info.IsHeadDetached ? gitRef : reference.FriendlyName;
    }

    public Task<IReadOnlyList<GitRef>> ListRefsAsync(string cloneUrl, string? credentialToken, CancellationToken ct)
    {
        return Task.Run<IReadOnlyList<GitRef>>(() =>
        {
            var results = new List<GitRef>();
            foreach (var r in Repository.ListRemoteReferences(cloneUrl, BuildCredentials(credentialToken)))
            {
                if (r.CanonicalName.StartsWith("refs/heads/"))
                    results.Add(new GitRef(r.CanonicalName["refs/heads/".Length..], "branch", r.TargetIdentifier));
                else if (r.CanonicalName.StartsWith("refs/tags/") && !r.CanonicalName.EndsWith("^{}"))
                    results.Add(new GitRef(r.CanonicalName["refs/tags/".Length..], "tag", r.TargetIdentifier));
            }
            return results;
        }, ct);
    }

    private static Branch ResolveRef(Repository repo, string gitRef, string? token, IProgress<string> log)
    {
        // Blank asks for the default branch outright, which is what a fresh clone is standing on.
        if (string.IsNullOrWhiteSpace(gitRef)) return repo.Head;

        // Prefer a matching branch; fall back to a tag; else default branch.
        var branch = repo.Branches[$"origin/{gitRef}"] ?? repo.Branches[gitRef];
        if (branch is not null) return branch;

        var tag = repo.Tags[gitRef];
        if (tag is not null)
        {
            // Checkout the tag's commit into a detached HEAD via its branch tip lookup.
            Commands.Checkout(repo, (Commit)tag.PeeledTarget);
            return repo.Head;
        }
        log.Report($"Ref '{gitRef}' not found; using default branch.");
        return repo.Head;
    }

    private static CredentialsHandler? BuildCredentials(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        return (_, _, _) => new UsernamePasswordCredentials { Username = token, Password = string.Empty };
    }

    private static void DeleteDirectory(string path)
    {
        // .git contains read-only files on Windows; clear the attribute before delete.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }
}
