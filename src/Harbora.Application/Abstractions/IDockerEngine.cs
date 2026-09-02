namespace Harbora.Application.Abstractions;

/// <summary>
/// The single seam through which the platform touches the container runtime. Every Docker
/// operation goes through here (backed by Docker.DotNet) — no shell strings anywhere else,
/// which removes a whole class of command-injection risks.
/// </summary>
public interface IDockerEngine
{
    Task<string> BuildImageAsync(DockerBuildRequest request, IProgress<string> log, CancellationToken ct);

    /// <summary>
    /// Pulls an image, optionally authenticating to its registry first.
    /// </summary>
    /// <param name="credential">
    /// 1.3 (2026-09 market-gaps round two): the workspace's stored credential for the image's own
    /// registry host, or null when none is configured (which is the ordinary case for a public
    /// image). Resolved by the caller — <c>DeploymentPipeline</c> — by matching the image's registry
    /// host against <c>RegistryCredential.RegistryHost</c>; this seam only ever carries the one
    /// credential that already won that match, never a set to choose between.
    /// </param>
    Task PullImageAsync(string image, IProgress<string> log, CancellationToken ct, RegistryPullCredential? credential = null);

    /// <summary>Tagged images present on this node, optionally filtered to those whose tag starts with a prefix.</summary>
    Task<IReadOnlyList<ImageInfo>> ListImagesAsync(string? tagPrefix, CancellationToken ct);

    /// <summary>
    /// Whether an image reference resolves on this node. Artifact rollback re-releases a prior
    /// image, so this is what makes "instant rollback" checkable before we promise it.
    /// </summary>
    Task<bool> ImageExistsAsync(string imageRef, CancellationToken ct);

    /// <summary>Best-effort image removal. An image still in use by a container is left alone.</summary>
    Task RemoveImageAsync(string imageRef, CancellationToken ct);

    /// <summary>
    /// The container ports an image declares (its <c>EXPOSE</c> lines). Empty when it declares none,
    /// which is common and means "we cannot tell" rather than "it listens nowhere".
    /// </summary>
    Task<IReadOnlyList<int>> GetImagePortsAsync(string imageRef, CancellationToken ct);

    Task<string> RunContainerAsync(DockerRunRequest request, CancellationToken ct);

    /// <summary>
    /// <see cref="RunContainerAsync"/> split into its own "create" half — the container exists (Docker
    /// would list it, in a created-not-running state) but its main process has not run. This is the
    /// one gap C2 (2026-08-22 config-delivery plan) needs: a config-file override must be written
    /// before the app's own process starts, or the app has already read whatever the image baked in.
    ///
    /// <para>
    /// Defaults to <see cref="NotSupportedException"/> — the same idiom as <see cref="ExecAsync"/> and
    /// <see cref="GetLogsSinceAsync"/>: an engine that cannot honestly split create from start must say
    /// so, rather than silently starting the container anyway. Today only the local engine
    /// (<c>DockerEngine</c>) overrides this; a remote node's caller checks <c>server.IsLocal</c> itself
    /// and fails the deployment with an actionable message before ever reaching here.
    /// </para>
    /// </summary>
    Task<string> CreateContainerAsync(DockerRunRequest request, CancellationToken ct) =>
        throw new NotSupportedException($"{GetType().Name} does not support creating a container without starting it.");

    /// <summary>Starts a container previously created with <see cref="CreateContainerAsync"/>.</summary>
    Task StartContainerAsync(string containerId, CancellationToken ct) =>
        throw new NotSupportedException($"{GetType().Name} does not support starting a previously created container.");

    Task StopContainerAsync(string containerId, CancellationToken ct);
    Task RemoveContainerAsync(string containerId, bool force, CancellationToken ct);
    Task RestartContainerAsync(string containerId, CancellationToken ct);

    Task StreamLogsAsync(string containerId, IProgress<string> sink, CancellationToken ct);

    /// <summary>Non-following snapshot of the last <paramref name="tailLines"/> log lines.</summary>
    Task<string> GetLogsAsync(string containerId, int tailLines, CancellationToken ct);

    /// <summary>
    /// A snapshot of log lines no older than <paramref name="since"/>, each carrying the moment the
    /// container produced it. <see cref="GetLogsAsync"/>'s tail carries no such thing — nothing before
    /// a time-window search ever asked Docker for one.
    ///
    /// <para>
    /// Not every transport this platform speaks can supply a real per-line timestamp for a stream it
    /// does not capture itself. The default throws <see cref="NotSupportedException"/> rather than
    /// quietly handing back an untimed tail dressed up as a time-scoped one — the same rule
    /// <see cref="ExecAsync"/> already states for a shell an engine cannot honestly offer: an engine
    /// that cannot do this must be able to say so, so a caller asking for a window knows which hosts
    /// could honor it and which could not, instead of being told nothing matched a window that was
    /// never actually applied.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<TimedLogLine>> GetLogsSinceAsync(
        string containerId, DateTimeOffset since, int maxLines, CancellationToken ct) =>
        throw new NotSupportedException(
            $"{GetType().Name} cannot attach real timestamps to its log lines, so it cannot honor a time window.");

    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(string? labelFilter, CancellationToken ct);
    Task<ContainerStats?> GetStatsAsync(string containerId, CancellationToken ct);

    /// <summary>One container in detail, or null when the engine has no such container.</summary>
    Task<ContainerDetail?> InspectAsync(string containerNameOrId, CancellationToken ct);

    /// <summary>
    /// How long this container has run and how often it has restarted, or null when the engine has no
    /// such container or declines to answer.
    ///
    /// <para>
    /// Separate from <see cref="InspectAsync"/> deliberately, and narrower on purpose: a v1 node
    /// refuses <c>InspectAsync</c> outright because it cannot honestly fill the image digest and the
    /// tri-state health <see cref="ContainerDetail"/> promises (see <c>NodeWorkloadEngine.InspectAsync</c>'s
    /// own doc comment for why). But it already carries both of these two figures on every
    /// workload-status answer, so the uptime/restart series does not have to wait on that larger
    /// question — it asks for exactly the two fields every engine can answer honestly today.
    /// </para>
    /// </summary>
    Task<ContainerLifecycle?> GetLifecycleAsync(string containerNameOrId, CancellationToken ct);

    Task EnsureNetworkAsync(string name, CancellationToken ct);
    /// <summary>Attach an existing container to a network (idempotent) — used to give Traefik ingress into per-tenant networks.</summary>
    Task ConnectNetworkAsync(string containerNameOrId, string network, CancellationToken ct);
    Task EnsureVolumeAsync(string name, CancellationToken ct);
    Task RemoveVolumeAsync(string name, CancellationToken ct);

    /// <summary>
    /// Every volume that actually exists on this node's disk — not the ones the database has a row
    /// for, the ones the daemon itself would list with <c>docker volume ls</c>.
    ///
    /// <para>
    /// HARBORA-0033's other half. <see cref="EnsureVolumeAsync"/> and <see cref="RemoveVolumeAsync"/>
    /// only ever name a volume the caller already knows about; nothing before this let the platform
    /// ask a server "what is actually there" and compare the answer against what the database
    /// believes. No default implementation is offered — the same reason <see cref="ExecAsync"/> and
    /// <see cref="RunOneOffAsync"/> have none: an engine that cannot honestly enumerate a node's disk
    /// must say so itself, in its own words, rather than silently inherit a generic refusal or — worse
    /// — a silent empty list that would read as "this machine has nothing to report" instead of "this
    /// machine was never asked".
    /// </para>
    /// </summary>
    Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct);

    /// <summary>
    /// Runs a short-lived container to completion (used by the backup engine to tar/untar
    /// volumes) and returns its exit code. The container is removed afterwards.
    /// </summary>
    Task<int> RunOneOffAsync(DockerOneOffRequest request, IProgress<string>? log, CancellationToken ct);

    Task<HostInfo> GetHostInfoAsync(CancellationToken ct);

    /// <summary>
    /// Attaches a shell to a running container and returns the two-way stream.
    ///
    /// Behind the seam on purpose, and not because the panel might one day speak to something other
    /// than docker: an engine that cannot offer this must be able to <b>say so</b>. A node engine
    /// throws <see cref="NotSupportedException"/> rather than returning something that looks like a
    /// terminal and never carries a byte, which is the failure this codebase keeps finding.
    /// </summary>
    Task<IContainerExec> ExecAsync(
        string containerId, IReadOnlyList<string> command, int columns, int rows, CancellationToken ct);
}

/// <summary>
/// A live shell inside a container: bytes in, bytes out, and a size.
///
/// Deliberately bytes rather than lines. A terminal is not line-oriented — a keystroke matters
/// before the newline, and the escape sequences that draw the screen are not lines at all.
/// </summary>
public interface IContainerExec : IAsyncDisposable
{
    /// <summary>Reads whatever the shell has produced. Zero means it has ended.</summary>
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>Sends keystrokes.</summary>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>Tells the shell how big the window is, so anything full-screen draws correctly.</summary>
    Task ResizeAsync(uint columns, uint rows, CancellationToken ct);
}

public record DockerOneOffRequest(
    string Image,
    IReadOnlyList<string> Command,
    IReadOnlyList<(string Source, string Target, bool ReadOnly)> Binds,
    /// <summary>Environment for the helper — a database dump needs the password here.</summary>
    IReadOnlyDictionary<string, string>? Env = null,
    /// <summary>
    /// Docker network mode. Tar helpers need none, but a helper that must reach another container
    /// does: <c>container:harbora-panel</c> gives it exactly the panel's own connectivity, so a
    /// hostname that works for the panel works here without restating any network configuration.
    /// </summary>
    string? NetworkMode = null);

public record DockerBuildRequest(
    string ContextPath,
    string Dockerfile,
    string ImageTag,
    IReadOnlyDictionary<string, string> BuildArgs,
    /// <summary>
    /// Images already on the node whose layers this build may reuse — the classic builder's
    /// <c>--cache-from</c>. Null or empty is a cold build, and is what every caller that has nothing
    /// to reuse passes: an unchanged <c>npm ci</c> / <c>dotnet restore</c> / <c>pip install</c> layer
    /// can only be reused if a previous build of the SAME app is still on this node to reuse it from.
    ///
    /// <para>
    /// Never a registry reference and never another app's tag. The daemon resolves these against
    /// images it already has, so naming something that is not there wastes the parameter rather than
    /// pulling anything — and naming a stranger's image would be a cross-tenant read of layer
    /// contents. <see cref="Harbora.Infrastructure.Deployments.BuildCache"/> is the only thing in the
    /// platform that decides this value; see its own doc for the two guarantees it holds.
    /// </para>
    /// </summary>
    IReadOnlyList<string>? CacheFrom = null,
    /// <summary>
    /// The classic builder's <c>--no-cache</c>: bypass the daemon's own layer cache as well as
    /// <see cref="CacheFrom"/>, so every instruction actually re-runs. <see cref="CacheFrom"/> alone
    /// is not enough to guarantee a genuinely cold build — the daemon may still have unrelated local
    /// layers from an earlier build of this exact Dockerfile that a plain build would happily reuse
    /// even with no cache source named. Set from the deploy UI's "no cache" control for the failures
    /// only a truly fresh build can prove: a base image that changed underneath a stale local layer,
    /// a flaky dependency a cached <c>RUN</c> keeps hiding. False on every ordinary deploy.
    /// </summary>
    bool NoCache = false);

public record DockerRunRequest(
    string Image,
    string ContainerName,
    string NetworkName,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<(string VolumeName, string MountPath, bool ReadOnly)> Volumes,
    int? ContainerPort,
    long MemoryLimitBytes,
    double CpuLimit,
    string? HealthCheckPath,
    IReadOnlyList<string>? Command = null,
    int? PublishToHostPort = null,
    /// <summary>
    /// Extra DNS names this container answers to on its network. Compose stacks need them: a service
    /// written to connect to <c>db:5432</c> must resolve <c>db</c>, not the versioned container name
    /// that lets old and new coexist during a cutover.
    /// </summary>
    IReadOnlyList<string>? NetworkAliases = null,
    /// <summary>Additional TCP container-to-host port publications for multi-protocol services.</summary>
    IReadOnlyDictionary<int, int>? AdditionalPublishedPorts = null);

public record ContainerInfo(string Id, string Name, string Image, string State, string Status, IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// One container, asked about directly.
///
/// <see cref="ContainerInfo"/> is what a listing can cheaply say — a state and a status line. This is
/// what an inspect adds: which image is actually running, how long it has been up, how often it has
/// restarted, and whether its health check is passing.
///
/// Every figure that a runtime may decline to report is nullable, and stays null rather than
/// defaulting. The reason is the same one <c>RuntimeContainerStats</c> states on the node side: a
/// zero is a specific claim, and making it because nobody answered is the panel asserting something
/// it does not know. <paramref name="Healthy"/> in particular is null when no health check is
/// configured — that is "we were not told how to ask", not "failing".
/// </summary>
public record ContainerDetail(
    string Id,
    string Name,
    string Image,
    string? ImageDigest,
    string State,
    string Status,
    bool? Healthy,
    int? RestartCount,
    DateTimeOffset? StartedAt);

/// <summary>
/// The two figures a lifecycle series tracks, and nothing else — see
/// <see cref="IDockerEngine.GetLifecycleAsync"/> for why this is not just a narrower
/// <see cref="ContainerDetail"/>. Both null-when-unknown for the same reason every figure on that
/// record is: a zero here is a specific claim, not a stand-in for "nobody answered".
/// </summary>
public record ContainerLifecycle(int? RestartCount, DateTimeOffset? StartedAt);

/// <summary>One line of container output, paired with the moment the container wrote it.</summary>
public record TimedLogLine(DateTimeOffset Timestamp, string Text);

public record ImageInfo(string Id, string Tag, DateTimeOffset CreatedAt, long SizeBytes);

/// <summary>
/// One volume as the daemon itself names it — <see cref="Name"/> is the exact docker volume name, the
/// same string <see cref="Domain.Apps.Volume.Name"/> stores when the platform made it. Nothing else
/// this report needs (mountpoint, driver, labels) is carried here on purpose: the one comparison this
/// exists for is "is this name in the database", and a wider shape would just be fields nobody reads.
/// <paramref name="CreatedAt"/> is null when the engine cannot honestly report it, not a stand-in for
/// "just now" — the same rule every other unmeasured figure in this interface follows.
/// </summary>
public record VolumeInfo(string Name, DateTimeOffset? CreatedAt);
public record ContainerStats(double CpuPercent, long MemoryUsedBytes, long MemoryLimitBytes, long NetRxBytes, long NetTxBytes);
/// <param name="Architecture">
/// What the host runs — <c>amd64</c>, <c>arm64</c>. Optional and last, so an agent that does not
/// report it still deserialises. Unknown stays unknown rather than defaulting to a guess: a wrong
/// guess here refuses images that would have run perfectly well.
/// </param>
public record HostInfo(
    int CpuCores, long TotalMemoryBytes, long TotalDiskBytes, long FreeDiskBytes,
    string DockerVersion, int ContainersRunning, string? Architecture = null);

/// <summary>
/// One registry's own username/secret, decrypted for exactly one pull and handed to whichever engine
/// is about to make it (1.3, 2026-09 market-gaps round two). Never persisted, logged or redacted
/// through this type — it lives only as long as the call that carries it, the same lifetime rule
/// every other decrypted secret in this codebase follows (e.g. <c>AttachedDatabaseCreds</c>).
/// </summary>
/// <param name="Registry">
/// The registry host this credential is for, in the same normalized shape
/// <c>ImageDigestResolver.Parse</c> produces (e.g. <c>ghcr.io</c>, <c>docker.io</c>) — carried here
/// purely so a failure message can name the registry without a second lookup.
/// </param>
public sealed record RegistryPullCredential(string Registry, string Username, string Secret);

/// <summary>
/// Why a registry pull failed, distinguished as far as the registry's own answer allows — the honest
/// alternative to reporting every failed pull as "image not found", which sends a customer looking for
/// a typo in a perfectly correct image name when the real cause is a missing or wrong credential.
/// </summary>
public enum RegistryPullFailureKind
{
    /// <summary>Credentials were supplied and the registry refused them.</summary>
    CredentialsRejected,

    /// <summary>The registry demanded authentication and no credential is configured for it.</summary>
    CredentialsMissing,

    /// <summary>The registry's answer says the image or tag itself does not exist.</summary>
    ImageNotFound,

    /// <summary>
    /// The registry's own answer does not distinguish a missing/incorrect credential from a
    /// nonexistent image — several registries deliberately blend the two (Docker Hub's own daemon
    /// message is "repository does not exist or may require 'docker login'") so an anonymous caller
    /// cannot use the difference to discover a private repository exists. Guessing which one it is
    /// would be inventing a fact the registry never gave up; saying so plainly is the honest answer.
    /// </summary>
    Indeterminate
}

/// <summary>Thrown when a registry pull fails, carrying <see cref="RegistryPullFailureKind"/> so a
/// caller (or a test) can tell the three-plus-one outcomes apart without parsing <see cref="Exception.Message"/>.</summary>
public sealed class RegistryPullException(RegistryPullFailureKind kind, string message) : Exception(message)
{
    public RegistryPullFailureKind Kind { get; } = kind;
}

/// <summary>
/// Turns a registry's raw error text into one of <see cref="RegistryPullFailureKind"/>'s named
/// outcomes, in words an operator can act on rather than a downstream "image not found".
///
/// <para>
/// Deliberately conservative: it only calls a failure "rejected credentials" or "image not found" when
/// the registry's own words say so unambiguously and don't also carry the other kind's language. Any
/// text it cannot confidently place — including no text at all, which happens when the daemon fails
/// before a registry ever answers — becomes <see cref="RegistryPullFailureKind.Indeterminate"/>. A
/// classifier that guesses under uncertainty is exactly the "0 printed where the truth is not measured"
/// defect this platform exists to remove, just spelled a different way.
/// </para>
/// </summary>
public static class RegistryPullDiagnostics
{
    private static readonly string[] AuthWords =
        ["unauthorized", "authentication required", "401", "403", "incorrect username or password", "denied:"];

    private static readonly string[] MissingWords =
        ["manifest unknown", "not found", "404", "no such image", "does not exist"];

    /// <summary>Phrases registries use that deliberately blend "no credential"/"wrong credential" with
    /// "does not exist" — Docker Hub's own daemon message is the canonical example.</summary>
    private static readonly string[] BlendedWords =
        ["may require", "pull access denied"];

    public static RegistryPullException Classify(string registryHost, bool credentialSupplied, string? rawMessage)
    {
        var text = (rawMessage ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();

        var mentionsAuth = AuthWords.Any(w => lower.Contains(w, StringComparison.Ordinal));
        var mentionsMissing = MissingWords.Any(w => lower.Contains(w, StringComparison.Ordinal));
        var blended = BlendedWords.Any(w => lower.Contains(w, StringComparison.Ordinal)) || (mentionsAuth && mentionsMissing);

        if (text.Length == 0)
            return new RegistryPullException(RegistryPullFailureKind.Indeterminate,
                $"{registryHost} refused to pull this image and gave no detail Harbora could read. " +
                (credentialSupplied
                    ? $"Credentials are configured for {registryHost} — check them, and confirm the image name and tag exist."
                    : $"No credentials are configured for {registryHost} — add some if this image is private, and confirm the image name and tag."));

        if (blended)
            return new RegistryPullException(RegistryPullFailureKind.Indeterminate,
                $"{registryHost} answered in a way that does not distinguish a missing/incorrect credential " +
                "from an image that does not exist (some registries deliberately blend the two so an " +
                "unauthenticated caller cannot use the difference to discover a private repository). " +
                (credentialSupplied
                    ? $"Credentials are configured for {registryHost} — double-check them."
                    : $"No credentials are configured for {registryHost} — add some if this image is private.") +
                $" Also confirm the image name and tag. The registry said: {text}");

        if (mentionsAuth)
            return credentialSupplied
                ? new RegistryPullException(RegistryPullFailureKind.CredentialsRejected,
                    $"{registryHost} rejected the credentials configured for it. Check the username and secret and save them again.")
                : new RegistryPullException(RegistryPullFailureKind.CredentialsMissing,
                    $"{registryHost} demanded authentication and no credentials are configured for it in this workspace. " +
                    "Add credentials for this registry and redeploy.");

        if (mentionsMissing)
            return new RegistryPullException(RegistryPullFailureKind.ImageNotFound,
                $"{registryHost} says this image does not exist — check the image name and tag.");

        return new RegistryPullException(RegistryPullFailureKind.Indeterminate,
            $"{registryHost} refused to pull this image and Harbora could not classify why from its answer: {text}");
    }
}
