namespace Harbora.Application.Abstractions;

/// <summary>Clones/fetches sources and resolves refs for the build engine.</summary>
public interface IGitService
{
    /// <summary>Clone (or update) <paramref name="cloneUrl"/> at <paramref name="gitRef"/> into a working dir; returns the resolved commit.</summary>
    Task<GitCheckout> CheckoutAsync(string cloneUrl, string gitRef, string? credentialToken, string workingDir, IProgress<string> log, CancellationToken ct);

    Task<IReadOnlyList<GitRef>> ListRefsAsync(string cloneUrl, string? credentialToken, CancellationToken ct);
}

public record GitCheckout(string CommitSha, string CommitMessage, string CommitAuthor, string LocalPath);
public record GitRef(string Name, string Type, string Sha); // Type: branch | tag
