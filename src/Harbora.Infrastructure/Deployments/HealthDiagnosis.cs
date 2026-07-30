namespace Harbora.Infrastructure.Deployments;

/// <summary>Why the health gate refused to switch traffic to a new container.</summary>
public enum HealthFailure
{
    None = 0,
    /// <summary>The container was gone from the runtime moments after it was started.</summary>
    Vanished = 1,
    /// <summary>The container ran and stopped — nearly always a crash on startup.</summary>
    Exited = 2,
    /// <summary>
    /// The container crashed and Docker restarted it, repeatedly. This — not <see cref="Exited"/> —
    /// is what a crash on startup actually looks like here, because app containers run under
    /// <c>unless-stopped</c>: the runtime revives them before anyone can observe them stopped.
    /// </summary>
    CrashLooping = 5,
    /// <summary>Still not running when the gate gave up.</summary>
    NeverStarted = 3,
    /// <summary>Running, but the health path never answered successfully.</summary>
    NoHealthyResponse = 4
}

/// <summary>
/// What the health gate observed. Kept as facts, so the wording lives in one testable place.
/// </summary>
/// <param name="Failure">Which of the four distinct failures happened.</param>
/// <param name="ContainerStatus">The runtime's own status line — carries the exit code.</param>
/// <param name="LogTail">The container's last output, which is where the real cause usually is.</param>
/// <param name="ProbeUrl">The URL that was probed, when the failure was an unanswered health path.</param>
public sealed record HealthReport(
    HealthFailure Failure,
    string? ContainerStatus = null,
    string? LogTail = null,
    string? ProbeUrl = null)
{
    public static readonly HealthReport Healthy = new(HealthFailure.None);
    public bool IsHealthy => Failure == HealthFailure.None;
}

/// <summary>
/// Turns a failed health gate into a sentence that names the cause.
///
/// Previously all four failures threw "Container failed its health check." — true, and useless. The
/// most common deploy failure by far is an image that starts and immediately dies (a missing
/// environment variable, usually), and the container's own last lines say exactly why. Not showing
/// them meant the one place the answer existed was the one place the user couldn't see.
/// </summary>
public static class HealthDiagnosis
{
    /// <summary>
    /// Enough of the container's output to carry the cause without flooding the field.
    ///
    /// Sized from a real failure rather than a guess: Postgres prints its one-line error and then
    /// several hundred characters explaining the alternatives. A 600-character window kept the
    /// explanation and cut the error, which is precisely backwards.
    /// </summary>
    public const int MaxTailChars = 1500;

    public static string Explain(HealthReport report, string containerName) => report.Failure switch
    {
        HealthFailure.None => "Healthy.",

        HealthFailure.Exited =>
            $"The container exited before it could serve traffic{Status(report)}. " +
            "A container that stops on startup is usually missing an environment variable or a " +
            "required service." + Tail(report),

        HealthFailure.CrashLooping =>
            $"The container keeps crashing and being restarted{Status(report)}. " +
            "It is failing during startup — usually a missing environment variable or a service it " +
            "cannot reach." + Tail(report),

        HealthFailure.Vanished =>
            $"The container {containerName} disappeared moments after it was started. " +
            "Something outside this deployment removed it — check for another process managing " +
            "containers on this host." + Tail(report),

        HealthFailure.NeverStarted =>
            $"The container never reached the running state{Status(report)}. " +
            "It is likely still pulling, restarting, or blocked on a resource limit." + Tail(report),

        HealthFailure.NoHealthyResponse =>
            $"The container is running but {report.ProbeUrl ?? "its health path"} never returned a " +
            "success response before the timeout. Check that the app listens on the configured port " +
            "and that the health check path is correct." + Tail(report),

        _ => "The health check did not pass."
    };

    private static string Status(HealthReport report) =>
        string.IsNullOrWhiteSpace(report.ContainerStatus) ? "" : $" ({report.ContainerStatus.Trim()})";

    /// <summary>
    /// The container's own words, last lines first in usefulness. Truncated from the front: a crash
    /// message sits at the end of the log, so keeping the tail keeps the cause.
    /// </summary>
    private static string Tail(HealthReport report)
    {
        var tail = report.LogTail?.Trim();
        if (string.IsNullOrEmpty(tail)) return " The container produced no output.";

        if (tail.Length > MaxTailChars) tail = "…" + tail[^MaxTailChars..];
        return $" Its last output was: {tail}";
    }
}
