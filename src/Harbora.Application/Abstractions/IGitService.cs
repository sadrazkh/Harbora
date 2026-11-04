namespace Harbora.Application.Abstractions;

/// <summary>Clones/fetches sources and resolves refs for the build engine.</summary>
public interface IGitService
{
    /// <summary>
    /// Clone (or update) <paramref name="cloneUrl"/> at <paramref name="gitRef"/> into a working dir; returns the resolved commit.
    /// A blank <paramref name="gitRef"/> means the repository's default branch.
    /// </summary>
    Task<GitCheckout> CheckoutAsync(string cloneUrl, string gitRef, string? credentialToken, string workingDir, IProgress<string> log, CancellationToken ct);

    Task<IReadOnlyList<GitRef>> ListRefsAsync(string cloneUrl, string? credentialToken, CancellationToken ct);
}

/// <param name="Branch">
/// What was actually checked out: the branch's short name, or the tag when a tag was asked for. An app
/// created tracking this finds its source again on a repository whose default branch is not <c>main</c>.
/// </param>
public record GitCheckout(string CommitSha, string CommitMessage, string CommitAuthor, string LocalPath, string? Branch = null);
public record GitRef(string Name, string Type, string Sha); // Type: branch | tag
