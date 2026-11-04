namespace Harbora.Infrastructure.Verification;

/// <summary>Serialised by name into <c>AppVerification.ChecksJson</c>; append only.</summary>
public enum AppCheckStatus
{
    Passed = 0,
    Failed = 1,

    /// <summary>It works, but something the person should read: an error in the app's own log, for example.</summary>
    Warning = 2,

    /// <summary>
    /// Not asked, and <see cref="AppCheck.Detail"/> says why — a broker has no shell client, a v1 node
    /// cannot run a one-off. Never counted as a pass: "we could not check" and "it works" are different
    /// sentences, and the second one is what this codebase keeps saying by accident.
    /// </summary>
    Skipped = 3,

    /// <summary>Not settled yet (a certificate still being issued). Becomes Failed at the give-up time.</summary>
    Pending = 4
}

/// <summary>One thing that was actually checked, and what it found, in both languages.</summary>
/// <param name="Key">Stable: <c>running</c>, <c>restarts</c>, <c>service:{id}</c>, <c>url</c>, <c>logs</c>.</param>
/// <param name="Fix">What to do about a failure, in plain words. Null when it passed.</param>
/// <param name="LinkPath">Where to go to do it, a site-relative path.</param>
public sealed record AppCheck(
    string Key,
    AppCheckStatus Status,
    string Title,
    string TitleFa,
    string? Detail = null,
    string? DetailFa = null,
    string? Fix = null,
    string? FixFa = null,
    string? LinkPath = null);

public sealed record AppVerificationReport(
    Guid AppId,
    Guid? DeploymentId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<AppCheck> Checks)
{
    public bool IsComplete => Checks.All(c => c.Status != AppCheckStatus.Pending);

    /// <summary>Only what failed. Not whether the app works, which takes more than that: see <c>LaunchAdvance.VerdictOn</c>.</summary>
    public bool HasFailures => Checks.Any(c => c.Status == AppCheckStatus.Failed);
}

/// <summary>
/// "Does this app really work": the question a green deployment does not answer. A deployment proves
/// a container started and answered one probe; this asks whether it stayed up, whether every database
/// it is attached to answers its credentials from its own network, and whether its address works from
/// outside.
/// </summary>
public interface IAppVerifier
{
    /// <summary>
    /// Runs the checks and stores the result as this app's latest. Safe with no session: the launch
    /// tick calls it. Safe to call repeatedly: a check that passed in <paramref name="previous"/> is
    /// not repeated when repeating it costs a container.
    /// </summary>
    /// <param name="previous">The last report of this run, or null to start one.</param>
    /// <param name="giveUpAt">Until then a check that has not settled comes back Pending; after it, Failed.</param>
    Task<AppVerificationReport> RunAsync(
        Guid appId, AppVerificationReport? previous, DateTimeOffset giveUpAt, CancellationToken ct);

    /// <summary>The last stored result, or null when this app has never been checked or is not this workspace's.</summary>
    Task<AppVerificationReport?> GetLatestAsync(Guid appId, CancellationToken ct);
}
