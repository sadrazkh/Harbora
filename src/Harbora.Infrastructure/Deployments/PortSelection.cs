namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Which port inside the container the proxy and the health gate should actually talk to.
///
/// The app carries a configured port, but the image knows where it listens — and when those disagree
/// the configured one is simply wrong. Seen live: a stock ASP.NET Core 8 project pushed with
/// `harbora deploy` built cleanly, started cleanly, logged "Application started", and then failed its
/// health check, because the app was created with port 80 while .NET 8 listens on 8080 and the image
/// says so (<c>EXPOSE 8080</c>). Nothing was broken except the number Harbora was probing.
/// </summary>
public static class PortSelection
{
    /// <summary>
    /// Ports recognisable as "somewhere HTTP is served". Used to avoid picking a metrics, admin or
    /// debug port when an image exposes several.
    /// </summary>
    private static readonly int[] CommonWebPorts = [80, 3000, 4200, 5000, 5173, 8000, 8080];

    public sealed record Choice(int Port, string? Reason)
    {
        /// <summary>True when the configured port was overridden by what the image declares.</summary>
        public bool Changed => Reason is not null;
    }

    /// <summary>
    /// Picks the port to use.
    ///
    /// The configured value wins whenever it is plausible — an image that declares nothing, or that
    /// declares the configured port, leaves the decision alone. Only a direct contradiction, where the
    /// image lists ports and the configured one is not among them, is overridden: continuing would
    /// probe an address nothing can answer, which is a guaranteed failure rather than a risk.
    /// </summary>
    public static Choice Choose(int configured, IReadOnlyCollection<int> exposed)
    {
        if (exposed.Count == 0 || exposed.Contains(configured)) return new(configured, null);

        // A recognised web port wins over an unrecognised one; among several, the lowest, so the
        // choice is deterministic rather than an artefact of how the list happens to be ordered.
        var chosen = exposed.Where(CommonWebPorts.Contains).DefaultIfEmpty(exposed.Min()).Min();

        return new(chosen,
            $"the image listens on {string.Join(", ", exposed.OrderBy(p => p))}, not on the configured " +
            $"port {configured} — using {chosen}");
    }
}
