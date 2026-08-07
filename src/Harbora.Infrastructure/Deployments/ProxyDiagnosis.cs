using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Turns a refused proxy cutover into a sentence an operator can act on.
///
/// This exists because the pipeline used to write one "⚠ Proxy apply failed…" line into the deploy
/// log and then report the deployment Succeeded. The container really was running, so nothing looked
/// wrong — while traffic still pointed at the old upstream, or at nothing. The wording lives here,
/// beside <see cref="HealthDiagnosis"/> and for the same reason: it is a decision about what to say,
/// so it belongs in one testable place rather than inside a 1,100-line pipeline.
/// </summary>
public static class ProxyDiagnosis
{
    /// <summary>
    /// Why the routing was not switched, quoting the engine's own error and saying whether the
    /// config file on disk was put back. Those are two different situations to walk into: a rolled
    /// back file still describes what is running, and one that was not may not.
    /// </summary>
    public static string ExplainApplyFailure(ProxyApplyResult result) =>
        "The proxy configuration could not be applied, so this version was never given traffic and " +
        $"the previous one is still serving. The proxy reported: {Reason(result.Error)} " +
        (result.RolledBack
            ? "Its configuration file was rolled back to the previous version, so the routes that " +
              "were already live are unchanged."
            : "Its configuration file was not rolled back, so it may no longer match what is " +
              "running — check it before deploying again.");

    /// <summary>
    /// The post-cutover verification found the proxy accepting the config but not serving the
    /// domain. Says which domain, what was asked and what came back, because "the proxy is broken"
    /// is not something anyone can act on.
    /// </summary>
    public static string ExplainUnreachable(string host, string probeUrl, string error) =>
        $"The proxy accepted the new configuration, but nothing answered for {host} through it " +
        $"({probeUrl}): {Reason(error)} That domain is not being served, so this deployment is " +
        "reported as failed rather than as working.";

    /// <summary>
    /// An engine that fails without saying why is rare but possible, and "reported: ." reads like
    /// the message was lost rather than never given.
    /// </summary>
    private static string Reason(string? error)
    {
        var text = error?.Trim();
        if (string.IsNullOrEmpty(text)) return "no reason.";
        return text.EndsWith('.') ? text : text + ".";
    }
}
