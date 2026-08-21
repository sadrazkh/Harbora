namespace Harbora.Application.Abstractions;

/// <summary>
/// Takes a generated function host's own after-the-fact report of a public call and writes the
/// <c>FunctionInvocation</c> row the panel could never write itself — it was never on that call's
/// path (2026-08-21 functions-and-services plan follow-up, reversing F1's original "the panel cannot
/// observe a public call, so say so honestly rather than fabricate a row" decision: the honest answer
/// turned out to be the host reporting what it actually did, not silence).
///
/// <para>
/// The counterpart of <see cref="ICustomEventIngestService"/>: same anonymous-door shape (an app id in
/// the URL pins the scope, the app's own invoke secret proves the caller), reused rather than
/// reinvented, because the trap is identical — this call has no session, so the tenant filter would
/// hide the very app whose own secret is about to prove it.
/// </para>
/// </summary>
public interface IFunctionInvocationReportService
{
    Task<FunctionInvocationReportOutcome> ReportAsync(
        Guid appId, string? providedSecret, FunctionInvocationReportRequest request, CancellationToken ct);
}

/// <summary>What the generated host posted about the call it just answered.</summary>
public sealed record FunctionInvocationReportRequest(
    string? Slug, int? StatusCode, int? DurationMs, string? Error);

public enum FunctionInvocationReportOutcome
{
    /// <summary>Written as a <c>FunctionInvocation</c> row with
    /// <c>FunctionInvocationOrigin.PublicCall</c>.</summary>
    Accepted,

    /// <summary>No app answers this id, or its secret does not match what was presented — deliberately
    /// not distinguished in the HTTP response, the same choice <c>EventsIngestController</c> and
    /// <c>WebhooksController</c> already make for their own equivalent pair of failure reasons.</summary>
    Unauthorized,

    /// <summary>The app authenticated, but no function in it has the posted slug — most likely the
    /// function was renamed or deleted after the container that made this call was built.</summary>
    UnknownFunction
}
