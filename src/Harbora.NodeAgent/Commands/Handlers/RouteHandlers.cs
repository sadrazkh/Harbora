using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Tunnels;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Runtime;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Commands.Handlers;

/// <summary>
/// Publishes the endpoint behind an HTTP route.
///
/// <para>
/// The node does not terminate TLS or match hostnames — Harbora's Traefik does, next to the control
/// plane. What the node can answer is "where is this container reachable from outside", and it can
/// only answer it when the workload declared the port as published. Refusing loudly when it did not
/// is better than inventing an endpoint that will time out: the port cannot be published on a
/// running container without recreating it, so the honest fix is a redeploy.
/// </para>
/// </summary>
public sealed class RegisterHttpRouteHandler(
    WorkloadRegistry registry, RouteRegistry routes, IHostFacts host, TimeProvider clock, ILogger<RegisterHttpRouteHandler> log)
    : WorkloadHandlerBase(registry), INodeCommandHandler
{
    public string Command => NodeCommands.RegisterHttpRoute;

    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<RegisterHttpRouteRequest>();
        if (request?.Route is null)
            return Task.FromResult(context.Fail(NodeErrorCode.ValidationFailed, "The payload has no route."));

        if (CreateNetworkHandler.Mismatch(context, request.TenantId) is { } refusal) return Task.FromResult(refusal);

        if (Resolve(context, request.WorkloadId) is not { } record)
            return Task.FromResult(NotFound(context, request.WorkloadId));

        var route = request.Route;
        var key = $"{route.TargetContainer}:{route.TargetPort}";

        if (!record.AllocatedPorts.TryGetValue(key, out var hostPort))
            return Task.FromResult(context.Fail(
                NodeErrorCode.ValidationFailed,
                $"Container '{route.TargetContainer}' does not publish port {route.TargetPort} on this node. " +
                "Redeploy the workload with publishToHost set for that port, then register the route."));

        var endpoint = $"{PrimaryAddress()}:{hostPort}";

        routes.Save(new RouteRecord
        {
            RouteId = route.RouteId,
            TenantId = request.TenantId,
            WorkloadId = request.WorkloadId,
            Kind = "http",
            Endpoint = endpoint,
            Domain = route.Domain,
            RegisteredAt = clock.GetUtcNow(),
        });

        log.LogInformation("Route {RouteId} for {Domain} resolves to {Endpoint}.", route.RouteId, route.Domain, endpoint);

        return Task.FromResult(context.Ok(new RouteResult
        {
            RouteId = route.RouteId,
            Active = true,
            PublicEndpoint = endpoint,
        }));
    }

    private string PrimaryAddress() => host.IpAddresses().FirstOrDefault() ?? host.Hostname;
}

/// <summary>
/// Publishes the endpoint behind a TCP route.
///
/// <para>
/// Same shape as the HTTP case. A route that names a gateway port is recorded with it so the
/// control plane's TCP gateway can complete the mapping — the node's half is the local endpoint.
/// </para>
/// </summary>
public sealed class RegisterTcpRouteHandler(
    WorkloadRegistry registry, RouteRegistry routes, IHostFacts host, TimeProvider clock)
    : WorkloadHandlerBase(registry), INodeCommandHandler
{
    public string Command => NodeCommands.RegisterTcpRoute;

    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<RegisterTcpRouteRequest>();
        if (request?.Route is null)
            return Task.FromResult(context.Fail(NodeErrorCode.ValidationFailed, "The payload has no route."));

        if (CreateNetworkHandler.Mismatch(context, request.TenantId) is { } refusal) return Task.FromResult(refusal);

        if (Resolve(context, request.WorkloadId) is not { } record)
            return Task.FromResult(NotFound(context, request.WorkloadId));

        var route = request.Route;
        var key = $"{route.TargetContainer}:{route.TargetPort}";

        if (!record.AllocatedPorts.TryGetValue(key, out var hostPort))
            return Task.FromResult(context.Fail(
                NodeErrorCode.ValidationFailed,
                $"Container '{route.TargetContainer}' does not publish port {route.TargetPort} on this node. " +
                "Redeploy the workload with publishToHost set for that port, then register the route."));

        var endpoint = $"{host.IpAddresses().FirstOrDefault() ?? host.Hostname}:{hostPort}";

        routes.Save(new RouteRecord
        {
            RouteId = route.RouteId,
            TenantId = request.TenantId,
            WorkloadId = request.WorkloadId,
            Kind = "tcp",
            Endpoint = endpoint,
            GatewayPort = route.GatewayPort,
            RegisteredAt = clock.GetUtcNow(),
        });

        return Task.FromResult(context.Ok(new RouteResult
        {
            RouteId = route.RouteId,
            Active = true,
            PublicEndpoint = endpoint,
        }));
    }
}

/// <summary>
/// Turns this node's ingress tunnel on or off.
///
/// <para>
/// The verb exists because reachability is not something a node can work out about itself. It knows
/// which ports it published; it cannot know whether anything outside can open a socket to them. The
/// control plane can — it is the thing that tried — so the decision is made there and carried here.
/// </para>
/// </summary>
public sealed class ConfigureIngressHandler(IngressTunnel ingress, ILogger<ConfigureIngressHandler> log)
    : INodeCommandHandler
{
    public string Command => NodeCommands.ConfigureIngress;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<ConfigureIngressRequest>();
        if (request is null)
            return context.Fail(NodeErrorCode.ValidationFailed, "The payload does not say whether ingress is enabled.");

        log.LogInformation(
            "{Actor} is turning the ingress tunnel {State}.",
            context.Envelope.Audit?.ActorName ?? "the control plane", request.Enabled ? "on" : "off");

        var result = await ingress.ApplyAsync(request.Enabled, request.GatewayUrl, ct);

        // A tunnel that was asked for and did not come up is a failure, not an acknowledgement. The
        // control plane is about to point routes at it, and it should not do that on a maybe.
        return result is { Enabled: true, Status: not TunnelStatus.Connected }
            ? context.Fail(
                result.LastError?.Code ?? NodeErrorCode.TunnelUnavailable,
                result.LastError?.Message ?? "The ingress tunnel did not connect to the gateway.",
                retryable: true)
            : context.Ok(result);
    }
}

public sealed class RemoveRouteHandler(RouteRegistry routes, ILogger<RemoveRouteHandler> log) : INodeCommandHandler
{
    public string Command => NodeCommands.RemoveRoute;

    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<RemoveRouteRequest>();
        if (request is null)
            return Task.FromResult(context.Fail(NodeErrorCode.ValidationFailed, "The payload has no route id."));

        if (CreateNetworkHandler.Mismatch(context, request.TenantId) is { } refusal) return Task.FromResult(refusal);

        // Removal is reported from what actually happened, never assumed. A caller that logs a
        // removal it did not perform is the exact shape of "reports success, does nothing".
        var removed = routes.Remove(request.RouteId, request.TenantId);

        if (removed) log.LogInformation("Removed route {RouteId}.", request.RouteId);

        return Task.FromResult(context.Ok(new AcknowledgedResult
        {
            Applied = true,
            NoOp = !removed,
            Detail = removed ? "route removed" : "no such route on this node",
        }));
    }
}
