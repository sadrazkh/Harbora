using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// What a deployment should do differently depending on what kind of service it is.
///
/// Everything Harbora has deployed so far was, implicitly, a web service: it published a port, was
/// probed over HTTP, and had traffic routed to it. A background worker has no port to probe and no
/// URL to route — waiting for one and then failing the deploy would be a bug, not a health check.
///
/// Kept pure so each rule is one testable statement rather than a condition buried in a 900-line
/// pipeline, and because getting it wrong means a working service reported as broken.
/// </summary>
public static class ServicePlan
{
    /// <summary>Whether anything should be routed to this service from outside.</summary>
    public static bool HasPublicTraffic(ServiceKind kind) =>
        kind is ServiceKind.Web or ServiceKind.Static;

    /// <summary>
    /// Whether the deployment should wait for an HTTP response before switching traffic.
    ///
    /// Only for services that serve HTTP. A worker that exits its startup and settles into a queue
    /// loop answers nothing, forever, and that is correct behaviour.
    /// </summary>
    public static bool HasHttpHealthCheck(ServiceKind kind) =>
        kind is ServiceKind.Web or ServiceKind.Static or ServiceKind.Private;

    /// <summary>
    /// Whether the container should be reachable by name inside the project's network.
    ///
    /// True for everything except one-shot tasks: a private service exists precisely to be called by
    /// its siblings, and a worker often exposes a metrics or health port to them.
    /// </summary>
    public static bool JoinsInternalNetwork(ServiceKind kind) =>
        kind is not ServiceKind.ReleaseTask;

    /// <summary>Whether the container is expected to keep running after it starts.</summary>
    public static bool IsLongRunning(ServiceKind kind) =>
        kind is not (ServiceKind.Cron or ServiceKind.ReleaseTask);

    /// <summary>
    /// Whether attaching a domain makes any sense. Used to keep the UI from offering something that
    /// cannot work, rather than accepting it and quietly doing nothing.
    /// </summary>
    public static bool CanHaveDomains(ServiceKind kind) => HasPublicTraffic(kind);

    /// <summary>
    /// The hostname a new service should be given, or null for none.
    ///
    /// Covers both routes to a domain — one typed into the form and one derived from the platform's
    /// root domain — because guarding only the first still left a worker with <c>{slug}.{root}</c>
    /// and a certificate nothing would ever answer on. Found by mutation testing, not by reading.
    /// </summary>
    public static string? HostFor(ServiceKind kind, string? requested, string? slug, string? rootDomain)
    {
        if (!CanHaveDomains(kind)) return null;

        var typed = requested?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(typed)) return typed;

        return string.IsNullOrWhiteSpace(rootDomain) || string.IsNullOrWhiteSpace(slug)
            ? null
            : $"{slug}.{rootDomain.Trim()}";
    }

    /// <summary>One line explaining the kind, for the form where someone picks it.</summary>
    public static string Describe(ServiceKind kind) => kind switch
    {
        ServiceKind.Web => "Serves HTTP. Gets a URL, a certificate and health checks.",
        ServiceKind.Private => "Reachable only by other services in this project. No public URL.",
        ServiceKind.Worker => "Runs continuously with no inbound traffic — queues, schedulers, consumers.",
        ServiceKind.Cron => "Runs on a schedule and exits.",
        ServiceKind.ReleaseTask => "Runs once before a release goes live. If it fails, the current version stays.",
        ServiceKind.Static => "Built to static files and served by the proxy.",
        _ => ""
    };
}
