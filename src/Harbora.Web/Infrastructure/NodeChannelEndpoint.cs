using Harbora.Infrastructure.Nodes;
using Harbora.NodeAgent.Contracts;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// The persistent node channel, mapped at the path the contract fixes.
///
/// <para>
/// A raw WebSocket rather than a SignalR hub. SignalR would bring its own handshake, its own
/// reconnect semantics and its own message protocol on top of the one the contract already
/// specifies — and the agent would have to take a dependency on a client library to speak it. The
/// frame envelope here is the contract's, unmodified.
/// </para>
/// </summary>
public static class NodeChannelEndpoint
{
    public static IEndpointRouteBuilder MapNodeChannel(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/" + NodeContract.ChannelPath, async (
            HttpContext context,
            NodeChannelSession session,
            NodeClientCertificateResolver certificates,
            ILoggerFactory loggerFactory) =>
        {
            var log = loggerFactory.CreateLogger("NodeChannel");

            if (!context.WebSockets.IsWebSocketRequest)
            {
                // A browser or a health check hitting the channel URL should get an explanation
                // rather than a protocol error.
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "This endpoint is the Harbora node channel and speaks WebSocket only.",
                    contract = "contracts/node-agent/v1",
                });
                return;
            }

            var certificate = certificates.Resolve(context);

            if (certificate is null)
            {
                log.LogWarning(
                    "A node channel connection from {Ip} presented no client certificate. Either Kestrel is not " +
                    "configured to ask for one, or Traefik is not forwarding it and " +
                    "NodeAgent:TrustForwardedClientCertificate is off.",
                    context.Connection.RemoteIpAddress);

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using (certificate)
            using (var socket = await context.WebSockets.AcceptWebSocketAsync())
            {
                // RequestAborted, not a fresh token: the channel's lifetime is the request's, and a
                // shutdown must be able to end it.
                await session.RunAsync(socket, certificate, context.RequestAborted);
            }
        })
        .AllowAnonymous()
        .WithName("NodeAgentChannel");

        return endpoints;
    }
}
