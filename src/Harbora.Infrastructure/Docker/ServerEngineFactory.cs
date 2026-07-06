using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// Picks the local in-process engine or a remote agent engine per server. The remote credential
/// (agent bearer token) is stored encrypted on the Server row and decrypted here for outbound calls.
/// </summary>
public sealed class ServerEngineFactory(
    IDockerEngine local,
    HarboraDbContext db,
    ISecretProtector protector,
    IHttpClientFactory httpFactory) : IServerEngineFactory
{
    public IDockerEngine Local => local;

    public async Task<IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct)
    {
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId, ct);
        if (server is null || server.IsLocal || string.IsNullOrWhiteSpace(server.AgentEndpoint))
            return local;

        var token = string.IsNullOrEmpty(server.AgentTokenHash) ? "" : SafeUnprotect(server.AgentTokenHash);
        return new RemoteDockerEngine(httpFactory, server.AgentEndpoint!, token);
    }

    private string SafeUnprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch { return value; }
    }
}
