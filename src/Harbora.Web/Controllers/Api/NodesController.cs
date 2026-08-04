using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Nodes;
using Harbora.Infrastructure.Nodes;
using Harbora.NodeAgent.Contracts;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers.Api;

/// <summary>
/// Managing enrolled nodes: minting enrollment tokens, seeing what is out there, and the two
/// node-admin operations — drain and update.
///
/// <para>
/// Deliberately not a passthrough for arbitrary commands. The node's allowlist is what makes a
/// compromised panel survivable, and a "send any command" endpoint here would put the panel's own
/// API surface in front of it — moving the boundary from "twenty-one verbs" to "twenty-one verbs
/// plus whatever this endpoint accepts". Workload operations belong to the deployment pipeline,
/// which knows what it is asking for.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/nodes")]
[Authorize(AuthenticationSchemes = TokenAuthenticationHandler.SchemeName, Policy = Capabilities.ServersManage)]
public sealed class NodesController(
    HarboraDbContext db,
    NodeEnrollmentService enrollment,
    NodeCommandService commands,
    NodeChannelRegistry registry,
    ICurrentUser currentUser,
    ILogger<NodesController> log) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        // IgnoreQueryFilters: nodes are platform infrastructure and belong to the provider, not to
        // the caller's workspace. Filtering them would show an admin an empty fleet.
        var nodes = await db.Nodes.IgnoreQueryFilters()
            .OrderBy(n => n.Name)
            .ToListAsync(ct);

        return Ok(nodes.Select(Summarise));
    }

    [HttpGet("{nodeId}")]
    public async Task<IActionResult> Get(string nodeId, CancellationToken ct)
    {
        var node = await db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);
        if (node is null) return NotFound(new { error = "No such node." });

        var recentCommands = await db.NodeCommands.IgnoreQueryFilters()
            .Where(c => c.NodeId == nodeId)
            .OrderByDescending(c => c.IssuedAt)
            .Take(25)
            .Select(c => new
            {
                c.CommandId, c.Command, status = c.Status.ToString(),
                c.IssuedAt, c.CompletedAt, c.ErrorCode, c.ErrorMessage,
                c.IdempotentReplay, actor = c.IssuedByName,
            })
            .ToListAsync(ct);

        var recentEvents = await db.NodeEvents.IgnoreQueryFilters()
            .Where(e => e.NodeId == nodeId)
            .OrderByDescending(e => e.At)
            .Take(50)
            .Select(e => new { e.Kind, e.Message, e.WorkloadId, e.At })
            .ToListAsync(ct);

        return Ok(new
        {
            node = Summarise(node),
            inventory = Raw(node.InventoryJson),
            capabilities = Raw(node.CapabilitiesJson),
            scopes = Raw(node.GrantedScopesJson),
            commands = recentCommands,
            events = recentEvents,
        });
    }

    /// <summary>
    /// Mint an enrollment token. The plaintext is in this response and nowhere else — the panel
    /// stores only a hash, so it cannot show it again and neither can a database dump.
    /// </summary>
    [HttpPost("tokens")]
    public async Task<IActionResult> MintToken([FromBody] MintTokenRequest? body, CancellationToken ct)
    {
        try
        {
            var token = await enrollment.MintTokenAsync(
                currentUser.UserId ?? Guid.Empty,
                body?.NodeName,
                body?.Region,
                body?.Environment,
                body?.Labels,
                body?.Scopes,
                body?.LifetimeMinutes is { } minutes and > 0 ? TimeSpan.FromMinutes(minutes) : null,
                ct);

            return Ok(new
            {
                token = token.Token,
                prefix = token.Prefix,
                expiresAt = token.ExpiresAt,
                // The exact command an operator should run, so the token does not have to be
                // pasted into a half-remembered one.
                install = $"curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/node-agent/install.sh " +
                          $"| bash -s -- --control-plane {Request.Scheme}://{Request.Host} --token {token.Token} " +
                          $"--name {body?.NodeName ?? "<node-name>"}",
            });
        }
        catch (ArgumentException e)
        {
            return BadRequest(new { error = e.Message });
        }
    }

    [HttpGet("tokens")]
    public async Task<IActionResult> ListTokens(CancellationToken ct)
    {
        var tokens = await db.NodeEnrollmentTokens.IgnoreQueryFilters()
            .OrderByDescending(t => t.CreatedAt)
            .Take(50)
            .Select(t => new
            {
                t.Prefix, t.ExpiresAt, t.UsedAt, t.UsedByNodeId, t.RevokedAt,
                t.NodeNameHint, t.Region, t.Environment,
            })
            .ToListAsync(ct);

        return Ok(tokens);
    }

    /// <summary>
    /// Withdraw a node's credential. It cannot renew and its next connection is refused.
    ///
    /// <para>
    /// The node is not asked to do anything — a revoked node may be one that stopped answering, and
    /// revocation that only works with the node's cooperation is not revocation.
    /// </para>
    /// </summary>
    [HttpPost("{nodeId}/revoke")]
    public async Task<IActionResult> Revoke(string nodeId, [FromBody] RevokeRequest? body, CancellationToken ct)
    {
        var revoked = await enrollment.RevokeAsync(
            nodeId, body?.Reason, currentUser.UserId, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);

        if (!revoked) return NotFound(new { error = "No such node, or it was already revoked." });

        // Hang up on the live session too. The node row is what refuses the next connection; this is
        // what ends the current one.
        if (registry.Get(nodeId) is { } connection)
            await connection.CloseAsync("credential revoked");

        return Ok(new { revoked = true, nodeId });
    }

    /// <summary>Take a node out of service, or put it back.</summary>
    [HttpPost("{nodeId}/drain")]
    public async Task<IActionResult> Drain(string nodeId, [FromBody] DrainRequest? body, CancellationToken ct)
    {
        var request = new DrainNodeRequest
        {
            Drain = body?.Drain ?? true,
            StopWorkloads = body?.StopWorkloads ?? false,
            TimeoutSeconds = body?.TimeoutSeconds ?? 300,
            Reason = body?.Reason,
        };

        return await SendAsync(nodeId, NodeCommands.DrainNode, request,
            // Stable per intent, so a retried drain is recognised as the same request rather than
            // draining twice — which would be harmless here but is not for every verb.
            idempotencyKey: $"drain:{nodeId}:{request.Drain}:{request.StopWorkloads}",
            reason: body?.Reason, ct);
    }

    /// <summary>
    /// Update a node's agent. The checksum is mandatory: the node refuses an artifact it cannot
    /// verify, and sending one without a checksum would only produce a slower refusal.
    /// </summary>
    [HttpPost("{nodeId}/update-agent")]
    public async Task<IActionResult> UpdateAgent(string nodeId, [FromBody] UpdateAgentRequest? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.TargetVersion))
            return BadRequest(new { error = "targetVersion is required." });

        if (string.IsNullOrWhiteSpace(body.Sha256))
            return BadRequest(new { error = "sha256 is required. A node will not install an artifact it cannot verify." });

        if (string.IsNullOrWhiteSpace(body.DownloadUrl) ||
            !body.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "downloadUrl must be an https URL." });

        var request = new AgentUpdateRequest
        {
            TargetVersion = body.TargetVersion,
            DownloadUrl = body.DownloadUrl,
            Sha256 = body.Sha256,
            DrainFirst = body.DrainFirst ?? true,
            DrainTimeoutSeconds = body.DrainTimeoutSeconds ?? 300,
            VerifyTimeoutSeconds = body.VerifyTimeoutSeconds ?? 120,
        };

        log.LogWarning("Updating node {NodeId} to agent {Version}.", nodeId, request.TargetVersion);

        return await SendAsync(nodeId, NodeCommands.UpdateAgent, request,
            idempotencyKey: $"agent-update:{nodeId}:{request.TargetVersion}",
            reason: $"update to {request.TargetVersion}", ct);
    }

    /// <summary>What a node says about one workload right now.</summary>
    [HttpGet("{nodeId}/workloads/{workloadId}")]
    public async Task<IActionResult> WorkloadStatus(string nodeId, string workloadId, CancellationToken ct)
    {
        var request = new WorkloadRequest
        {
            WorkloadId = workloadId,
            TenantId = currentUser.WorkspaceId?.ToString() ?? string.Empty,
        };

        return await SendAsync(nodeId, NodeCommands.GetWorkloadStatus, request,
            idempotencyKey: $"status:{workloadId}:{DateTimeOffset.UtcNow:yyyyMMddHHmm}",
            reason: null, ct);
    }

    private async Task<IActionResult> SendAsync(
        string nodeId, string command, object payload, string idempotencyKey, string? reason, CancellationToken ct)
    {
        try
        {
            var outcome = await commands.SendAsync(
                nodeId, command, payload, idempotencyKey, reason,
                sourceIp: HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

            var body = new
            {
                commandId = outcome.CommandId,
                status = outcome.Status.ToString(),
                result = outcome.Result,
                errorCode = outcome.ErrorCode?.ToString(),
                error = outcome.ErrorMessage,
                idempotentReplay = outcome.IdempotentReplay,
            };

            // A refusal by the node is not a server error here — the request reached it and got an
            // answer. 409 says "the node declined", which is what happened.
            return outcome.Succeeded ? Ok(body) : Conflict(body);
        }
        catch (NodeNotFoundException)
        {
            return NotFound(new { error = "No such node." });
        }
        catch (NodeNotConnectedException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "The node is not connected to this panel instance.",
            });
        }
    }

    private object Summarise(Node node) => new
    {
        node.NodeId,
        node.Name,
        status = node.Status.ToString(),
        node.Health,
        node.Draining,
        connected = registry.IsConnected(node.NodeId),
        node.AgentVersion,
        node.ProtocolVersion,
        node.Region,
        node.Environment,
        node.Architecture,
        node.OsName,
        node.OsVersion,
        node.ContainerRuntime,
        node.ContainerRuntimeVersion,
        node.CpuCores,
        node.TotalMemoryBytes,
        node.FreeMemoryBytes,
        node.TotalDiskBytes,
        node.FreeDiskBytes,
        node.Load1,
        node.RunningWorkloads,
        node.ActiveDatabaseGrants,
        node.ActiveTunnels,
        node.LastHeartbeatAt,
        node.LastConnectedAt,
        node.EnrolledAt,
        node.CertificateNotAfter,
        revoked = node.IsRevoked,
        node.RevokedReason,
        labels = Raw(node.LabelsJson),
        ipAddresses = Raw(node.IpAddressesJson),
    };

    /// <summary>Pass stored JSON through as JSON rather than as a string containing JSON.</summary>
    private static JsonElement? Raw(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed record MintTokenRequest(
        string? NodeName,
        string? Region,
        string? Environment,
        Dictionary<string, string>? Labels,
        List<string>? Scopes,
        int? LifetimeMinutes);

    public sealed record RevokeRequest(string? Reason);

    public sealed record DrainRequest(bool? Drain, bool? StopWorkloads, int? TimeoutSeconds, string? Reason);

    public sealed record UpdateAgentRequest(
        string TargetVersion,
        string DownloadUrl,
        string Sha256,
        bool? DrainFirst,
        int? DrainTimeoutSeconds,
        int? VerifyTimeoutSeconds);
}
