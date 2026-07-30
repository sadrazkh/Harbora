namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// What counts as passing the HTTP health probe.
///
/// The gate used to demand a status below 400 from whatever path was configured. For an app that has
/// a real health endpoint that is exactly right. But a newly created app has no configured path, so
/// the probe goes to <c>/</c> — and an API with no root route answers 404. Seen live: a working
/// ASP.NET Core service, built and started by `harbora deploy`, refused deployment because the root
/// of an API returned "not found".
///
/// So the two cases are separated. A path someone chose is an assertion about health and is held to
/// it. The root is only ever asking "is anything serving here?", and a 404 answers that as clearly as
/// a 200 — while a 5xx does not, because that is the app itself failing rather than a missing route.
/// </summary>
public static class HealthProbeRule
{
    /// <summary>True when no specific health path was configured.</summary>
    public static bool IsRoot(string? healthPath) =>
        string.IsNullOrWhiteSpace(healthPath) || healthPath.Trim() == "/";

    /// <summary>Whether this response should let the deployment proceed.</summary>
    public static bool Accepts(string? healthPath, int statusCode) =>
        statusCode < 400 || (IsRoot(healthPath) && statusCode < 500);

    /// <summary>
    /// How the log should describe accepting a response that is not a success. Null when the status
    /// speaks for itself, so a normal deploy stays quiet.
    /// </summary>
    public static string? ExplainAcceptance(string? healthPath, int statusCode) =>
        statusCode < 400 || !Accepts(healthPath, statusCode)
            ? null
            : $"answered {statusCode} — no health path is configured, so this only had to prove " +
              "something is serving. Set one on the app to check more than that.";
}
