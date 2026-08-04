namespace Harbora.Application.Abstractions;

/// <summary>
/// Reading a public container registry: which tags exist, and what a tag currently points at.
///
/// Read-only by design. Harbora never pushes, and an interface that cannot push is one that cannot
/// be talked into pushing by a bug.
/// </summary>
public interface IContainerRegistry
{
    /// <summary>
    /// Every tag the repository lists, or an empty list when it cannot be read.
    ///
    /// Empty rather than an exception: a registry being unreachable is an ordinary Tuesday, and a
    /// discovery run that throws on the first unreachable repository never reaches the rest.
    /// </summary>
    Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct);

    /// <summary>
    /// The digest a tag points at right now, or null when it cannot be resolved.
    ///
    /// Null matters: a version without a digest cannot be deployed, so one is never created from a
    /// tag whose digest is unknown. Storing it anyway would produce a row that looks like an option
    /// and refuses every time it is chosen.
    /// </summary>
    Task<string?> ResolveDigestAsync(string repository, string tag, CancellationToken ct);
}
