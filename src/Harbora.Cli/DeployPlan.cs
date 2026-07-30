namespace Harbora.Cli;

/// <summary>How a `harbora deploy` invocation should get the code to the server.</summary>
public enum DeployMode
{
    /// <summary>Pack the working folder and upload it.</summary>
    PushFolder,
    /// <summary>Upload an archive the caller already produced.</summary>
    PushTarball,
    /// <summary>Archive a git branch's committed content and upload that.</summary>
    PushGitBranch,
    /// <summary>Release an existing image; nothing is built.</summary>
    Image,
    /// <summary>Ask the server to pull from the app's Git remote.</summary>
    ServerGit
}

/// <summary>
/// Chooses the deploy mode from flags and config. Kept separate from the command so the precedence
/// rules are testable — they are the part users will argue with, and getting them wrong means
/// deploying something other than what was asked for.
/// </summary>
public static class DeployPlan
{
    public sealed record Choice(DeployMode Mode, string? Value, string Reason);

    /// <param name="image">--image</param>
    /// <param name="tar">--tar</param>
    /// <param name="branch">--branch</param>
    /// <param name="gitRef">--ref / --tag</param>
    /// <param name="push">--push</param>
    /// <param name="config">harbora.yml, already loaded</param>
    /// <param name="folderIsGitRepo">whether the folder has a .git directory</param>
    /// <param name="serverCanPull">
    /// Whether the app on the server actually has a repository it could pull from. Null when it could
    /// not be determined (an older server, or an app that was not listed), in which case the old
    /// local-only guess is used.
    /// </param>
    public static Choice Decide(
        string? image, string? tar, string? branch, string? gitRef, bool push,
        ProjectConfig config, bool folderIsGitRepo, bool? serverCanPull = null)
    {
        // Explicit flags win, most specific first. Each names exactly one source, so there is no
        // ambiguity to resolve — only an order to state.
        if (!string.IsNullOrWhiteSpace(image))
            return new(DeployMode.Image, image, "--image was given");

        if (!string.IsNullOrWhiteSpace(tar))
            return new(DeployMode.PushTarball, tar, "--tar was given");

        if (!string.IsNullOrWhiteSpace(branch))
            return new(DeployMode.PushGitBranch, branch, "--branch was given");

        // Asking for a ref or tag is an unambiguous "deploy what the server can pull".
        if (!string.IsNullOrWhiteSpace(gitRef))
            return new(DeployMode.ServerGit, gitRef, "--ref/--tag was given");

        if (push)
            return new(DeployMode.PushFolder, null, "--push was given");

        // Config supplies a default when no flag did.
        if (!string.IsNullOrWhiteSpace(config.Image))
            return new(DeployMode.Image, config.Image, "image: in harbora.yml");

        if (!string.IsNullOrWhiteSpace(config.Branch))
            return new(DeployMode.PushGitBranch, config.Branch, "branch: in harbora.yml");

        // A local .git used to be taken as "let the server pull". It is not: whether the server has
        // anything to pull is a fact about the app, not about this folder. An app created without a
        // repository — the CapRover-style flow this CLI exists for — accepted the deployment and then
        // failed with "no source archive was uploaded", from a folder that was a git checkout.
        if (serverCanPull is false)
            return new(DeployMode.PushFolder, null, "this app has no Git remote on the server, so the folder is uploaded");

        return folderIsGitRepo
            ? new(DeployMode.ServerGit, null, "folder is a git repo — server pulls from its remote")
            : new(DeployMode.PushFolder, null, "no .git here, so the server has nothing to pull");
    }
}
