using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// Picks the engine for a server: the in-process one for the local machine, a v1 node agent over its
/// control channel, or the older inbound agent over HTTP. The remote credential (agent bearer token)
/// is stored encrypted on the Server row and decrypted here for outbound calls.
///
/// <para>
/// The order of those checks is the point of this class. A server backed by a v1 node has no
/// <c>AgentEndpoint</c> — that absence is exactly how <see cref="Nodes.NodeServerLink"/> marks it —
/// so a factory that tested for an endpoint first and fell back to the local engine would deploy a
/// customer's application onto the panel's own Docker daemon. The node lookup therefore happens
/// before that fallback, and a non-local server with neither an endpoint nor a node now fails loudly
/// instead of landing somewhere convenient.
/// </para>
/// </summary>
public sealed class ServerEngineFactory(
    IDockerEngine local,
    HarboraDbContext db,
    ISecretProtector protector,
    IHttpClientFactory httpFactory,
    NodeCommandService nodeCommands,
    ImageDigestResolver digests,
    NodeHostFacts nodeFacts,
    ILogger<NodeWorkloadEngine> nodeLog,
    ILogger<ServerEngineFactory> log) : IServerEngineFactory
{
    public IDockerEngine Local => local;

    public async Task<IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct)
    {
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId, ct);

        // The local server genuinely is this machine.
        if (server is null || server.IsLocal) return local;

        // Before the endpoint check, deliberately. See the class comment.
        if (await nodeFacts.ForServerAsync(serverId, ct) is { } node)
        {
            if (node.IsRevoked)
                throw new InvalidOperationException(
                    $"Node {node.NodeId} ('{node.Name}') was revoked and takes no commands. " +
                    "Re-enroll it, or move this server's workloads elsewhere.");

            return new NodeWorkloadEngine(node.NodeId, nodeCommands, digests, nodeFacts, nodeLog);
        }

        if (string.IsNullOrWhiteSpace(server.AgentEndpoint))
        {
            log.LogError(
                "Server {ServerId} ('{Name}') is not local, has no agent endpoint and no enrolled node stands behind it. " +
                "Refusing to fall back to this panel's own Docker.",
                server.Id, server.Name);

            throw new InvalidOperationException(
                $"Server '{server.Name}' cannot be reached: it has no agent endpoint and no node is enrolled on it. " +
                "Install the node agent on that machine, or remove the server.");
        }

        var token = string.IsNullOrEmpty(server.AgentTokenHash) ? "" : SafeUnprotect(server.AgentTokenHash);

        System.Security.Cryptography.X509Certificates.X509Certificate2? clientCert = null;
        if (server.AgentUseMtls && !string.IsNullOrEmpty(server.AgentClientCertPfx))
        {
            try
            {
                var pfx = Convert.FromBase64String(SafeUnprotect(server.AgentClientCertPfx));
                clientCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(pfx, password: null);
            }
            catch { /* fall back to token-only if the cert can't be loaded */ }
        }

        return new RemoteDockerEngine(httpFactory, server.AgentEndpoint!, token, clientCert);
    }

    private string SafeUnprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch { return value; }
    }
}
