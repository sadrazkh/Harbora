namespace Harbora.Application.Abstractions;

/// <summary>What one outside-in HTTP attempt found, before it is turned into a domain
/// <c>Harbora.Domain.Monitoring.UptimeCheckOutcome</c> — kept as its own small vocabulary here, the
/// same way <see cref="DomainReadiness"/> stays out of <c>Harbora.Domain</c> even though
/// <c>CertificateWatcher</c> maps it onto a domain <c>CertificateStatus</c> at the call site.</summary>
public enum ProbeOutcome
{
    /// <summary>The target answered with the expected status (and body match, if one was asked for).</summary>
    Up,

    /// <summary>The target answered, but not with what was expected — wrong status, a missing body
    /// match, a refused connection, or a timeout. All four are real, observed facts about the target
    /// from this vantage point, not a fault in the checker itself.</summary>
    Down,

    /// <summary>The attempt itself could not be made or could not be judged — an unexpected exception in
    /// the probing code, not a fact about whether the target is up. Never returned for a timeout, a
    /// refused connection, or a wrong status/body: those are all <see cref="Down"/>, because the checker
    /// did get to ask the question and the target's answer (or silence) is what decided the outcome.</summary>
    CouldNotRun
}

/// <summary>Raw result of one outside-in HTTP probe.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="HttpStatus">The status actually returned, or null when no response ever arrived.</param>
/// <param name="LatencyMs">How long the attempt took, measured regardless of outcome — a timeout's own
/// elapsed time is itself part of the reportable fact.</param>
/// <param name="Detail">Why, in words an operator can act on — never "operation failed".</param>
public sealed record UptimeProbeResult(ProbeOutcome Outcome, int? HttpStatus, long? LatencyMs, string Detail);

/// <summary>
/// Performs one outside-in HTTP check against a URL — the same "ask from outside, not from inside the
/// container" shape <c>IDomainInspector</c> already uses for a TLS handshake. Mockable so
/// <c>UptimeChecker</c>'s incident-open/resolve wiring can be tested without a real socket, the same
/// reasoning <c>FakeDomainInspector</c> gives in <c>CertificateWatcherIncidentTests</c>.
/// </summary>
public interface IUptimeProbe
{
    /// <summary>
    /// Never throws for an ordinary network failure (timeout, refused connection, wrong status) — those
    /// come back as <see cref="ProbeOutcome.Down"/>, not an exception, because <c>UptimeChecker</c> must
    /// keep moving to the next app's check regardless. <paramref name="timeout"/> is enforced here, not
    /// merely requested of the transport: a target that never answers must still return within
    /// (approximately) this bound, never hang the caller.
    /// </summary>
    Task<UptimeProbeResult> ProbeAsync(
        Uri url, int expectedStatus, string? bodyContains, TimeSpan timeout, CancellationToken ct);
}
