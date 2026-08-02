using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;

namespace Harbora.Infrastructure.Networking;

/// <summary>Everything the diagram needs, already arranged.</summary>
public sealed record ArchitecturePicture(IReadOnlyList<PlacedNode> Nodes, IReadOnlyList<GraphEdge> Edges);

/// <summary>
/// Turns a project's services, databases and domains into a diagram.
///
/// The boxes carry only what Harbora actually knows: what a thing is, whether it is running, and the
/// internal name other services reach it by. The mockups this is drawn from show a CPU and memory
/// sparkline inside every node; per-service metrics are collected for apps but not for managed
/// services, and a graph that draws a flat line for "we did not measure this" is a graph that lies
/// in the most reassuring direction. So the second line says what is true and stops.
///
/// Edges come from the connection strings a service actually holds, worked out where the protector
/// is — the values are encrypted, so nothing here can match a hostname against a stored value.
/// </summary>
public static class ArchitectureGraph
{
    public static ArchitecturePicture Build(
        IReadOnlyList<App> services,
        IReadOnlyList<ManagedService> databases,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> connections)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        // Domains first: they are where traffic enters, and the diagram reads downwards from there.
        foreach (var service in services)
        {
            if (!ServicePlan.HasPublicTraffic(service.Kind)) continue;

            foreach (var domain in service.Domains.Where(d => !string.IsNullOrWhiteSpace(d.Host)))
            {
                var id = $"domain:{domain.Id}";
                nodes.Add(new GraphNode(
                    id, domain.Host, "external", "globe",
                    domain.SslEnabled ? "ok" : "warn",
                    domain.SslEnabled ? "HTTPS" : "HTTP"));
                edges.Add(new GraphEdge(id, NodeId(service)));
            }
        }

        foreach (var service in services)
        {
            nodes.Add(new GraphNode(
                NodeId(service),
                service.Name,
                Tier(service.Kind),
                Icon(service.Kind),
                Tone(service.Status),
                // The name other services reach it by — the one fact a diagram is asked for most.
                ServicePlan.JoinsInternalNetwork(service.Kind) ? service.Slug : null));
        }

        // Matched by container name, which is what a connection string contains.
        var byContainer = databases
            .Where(d => !string.IsNullOrWhiteSpace(d.ContainerName))
            .GroupBy(d => d.ContainerName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var database in databases)
        {
            nodes.Add(new GraphNode(
                NodeId(database),
                database.Name,
                "data",
                database.Type.ToString().ToLowerInvariant(),
                Tone(database.Status),
                string.IsNullOrWhiteSpace(database.Version) ? null : $"{database.Type} {database.Version}"));
        }

        foreach (var (serviceId, hosts) in connections)
        {
            var service = services.FirstOrDefault(s => s.Id == serviceId);
            if (service is null) continue;

            foreach (var host in hosts)
            {
                if (byContainer.TryGetValue(host, out var database))
                    edges.Add(new GraphEdge(NodeId(service), NodeId(database)));
            }
        }

        return new ArchitecturePicture(GraphLayout.Arrange(nodes, edges), edges);
    }

    private static string NodeId(App service) => $"app:{service.Id}";
    private static string NodeId(ManagedService database) => $"db:{database.Id}";

    /// <summary>What kind of box to draw — colour and icon only, never position.</summary>
    private static string Tier(ServiceKind kind) => kind switch
    {
        ServiceKind.Web or ServiceKind.Static => "web",
        ServiceKind.Worker or ServiceKind.Cron => "work",
        _ => "service"
    };

    private static string Icon(ServiceKind kind) => kind switch
    {
        ServiceKind.Web or ServiceKind.Static => "globe",
        ServiceKind.Worker => "cpu",
        ServiceKind.Cron => "clock",
        _ => "boxes"
    };

    /// <summary>
    /// A colour for a state. Anything not clearly running or clearly broken is "idle" rather than
    /// green: a diagram whose boxes are green by default is one nobody checks.
    /// </summary>
    private static string Tone(AppStatus status) => status switch
    {
        AppStatus.Running => "ok",
        AppStatus.Failed or AppStatus.Crashed => "error",
        AppStatus.Deploying => "warn",
        _ => "idle"
    };

    private static string Tone(ServiceStatus status) => status switch
    {
        ServiceStatus.Running => "ok",
        ServiceStatus.Failed => "error",
        ServiceStatus.Provisioning => "warn",
        _ => "idle"
    };
}
