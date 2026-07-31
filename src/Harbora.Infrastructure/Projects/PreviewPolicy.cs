using Harbora.Domain.Apps;

namespace Harbora.Infrastructure.Projects;

/// <summary>What a preview inherits, and what it deliberately does not.</summary>
/// <param name="Copied">Variables the preview is created with.</param>
/// <param name="SkippedSecrets">
/// Names of secrets that were left behind. Named, not silently dropped: a preview that will not
/// start because it has no database password should say which one it is missing.
/// </param>
public sealed record PreviewConfig(
    IReadOnlyList<(string Key, string Value)> Copied,
    IReadOnlyList<string> SkippedSecrets);

/// <summary>
/// Whether a branch gets an environment of its own, and what goes in it.
///
/// The decision that matters is the second one. A preview is created by anybody who can push a
/// branch, so copying the parent's secrets into it hands production's database password to every
/// branch in the repository — and to whoever can read the environment of a throwaway service. So
/// secrets are not copied, and the ones left behind are named, because a preview that silently
/// cannot start is worse than one that explains itself.
/// </summary>
public static class PreviewPolicy
{
    /// <summary>
    /// How long a preview lives without a push. A branch nobody deletes would otherwise leave a
    /// service running for ever, quietly eating the tenant's quota.
    /// </summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether this ref should get a preview.
    ///
    /// Never the tracked branch — that one already has a real environment, and previewing it would
    /// deploy the same commit twice — and never a tag, which is a release rather than work in
    /// progress.
    /// </summary>
    public static bool ShouldPreview(bool previewsEnabled, string refName, bool isTag, string? trackedBranch)
    {
        if (!previewsEnabled || isTag || string.IsNullOrWhiteSpace(refName)) return false;

        return !string.Equals(refName.Trim(), trackedBranch?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The configuration a preview starts with: everything that is not a secret, and a list of the
    /// secrets that were not brought along.
    /// </summary>
    public static PreviewConfig ConfigFor(IEnumerable<EnvironmentVariable> parentVariables)
    {
        var copied = new List<(string, string)>();
        var skipped = new List<string>();

        foreach (var variable in parentVariables.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            if (variable.IsSecret) skipped.Add(variable.Key);
            else copied.Add((variable.Key, variable.Value));
        }

        return new PreviewConfig(copied, skipped);
    }

    /// <summary>
    /// What to tell someone looking at a preview. Silence about the missing half is how a preview
    /// that cannot possibly work looks like a broken build.
    /// </summary>
    public static string? Advice(PreviewConfig config) =>
        config.SkippedSecrets.Count == 0
            ? null
            : $"Secrets are not copied into a preview: {string.Join(", ", config.SkippedSecrets)}. " +
              "Set the ones this branch needs, or point it at a test service.";

    /// <summary>Whether a preview has gone quiet for long enough to remove.</summary>
    public static bool HasExpired(DateTimeOffset lastPushedAt, DateTimeOffset now) =>
        now - lastPushedAt >= IdleLifetime;
}
