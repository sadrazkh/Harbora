using System.Security.Cryptography;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Nodes;
using Harbora.NodeAgent.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Nodes;

/// <summary>What a command did, from the control plane's side.</summary>
public sealed record NodeCommandOutcome(
    string CommandId,
    NodeCommandStatus Status,
    JsonElement? Result,
    NodeErrorCode? ErrorCode,
    string? ErrorMessage,
    bool IdempotentReplay)
{
    public bool Succeeded => Status == NodeCommandStatus.Succeeded;

    /// <summary>Read the result as the type the verb's contract says it is.</summary>
    public T? ResultAs<T>() =>
        Result is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } value
            ? value.Deserialize<T>(NodeContract.Json)
            : default;
}

/// <summary>
/// Issues commands to nodes and records what happened.
///
/// <para>
/// Every command is written to the database before its frame goes out. A command that was issued
/// has to be on record even if the panel dies between sending and hearing back — otherwise the
/// panel's idea of what it asked for is reconstructed from what it happened to receive, which is
/// exactly wrong when the thing that failed is the receiving.
/// </para>
/// </summary>
public sealed class NodeCommandService(
    HarboraDbContext db,
    NodeChannelRegistry registry,
    IAuditLogger audit,
    ICurrentUser currentUser,
    IOptions<NodeAgentControlPlaneOptions> options,
    TimeProvider clock,
    ILogger<NodeCommandService> log)
{
    private readonly NodeAgentControlPlaneOptions _options = options.Value;

    /// <summary>
    /// Send a command and wait for its outcome.
    ///
    /// <para>
    /// <paramref name="idempotencyKey"/> is the caller's promise about identity: two calls with the
    /// same key are the same logical operation, and a node that has already done one will replay its
    /// answer rather than repeat the work. Callers that pass a fresh key per attempt get a fresh
    /// execution per attempt, which for a deploy means deploying twice.
    /// </para>
    /// </summary>
    public async Task<NodeCommandOutcome> SendAsync(
        string nodeId,
        string command,
        object payload,
        string idempotencyKey,
        string? reason = null,
        TimeSpan? timeout = null,
        string? sourceIp = null,
        CancellationToken ct = default)
    {
        if (!NodeCommandCatalog.TryGet(command, out var descriptor))
            throw new ArgumentException($"'{command}' is not a node command.", nameof(command));

        var node = await db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.NodeId == nodeId, ct)
            ?? throw new NodeNotFoundException(nodeId);

        if (node.IsRevoked)
            return Refused(node, command, NodeErrorCode.CredentialRevoked,
                $"Node {nodeId} was revoked and takes no commands.");

        var granted = Deserialize<List<string>>(node.GrantedScopesJson) ?? [];

        if (!granted.Contains(descriptor.RequiredScope, StringComparer.Ordinal))
            // Checked here as well as on the node. The node's refusal is the one that matters for
            // safety; this one exists so an operator finds out before the round trip.
            return Refused(node, command, NodeErrorCode.Unauthorized,
                $"Node {nodeId} was not enrolled with the '{descriptor.RequiredScope}' scope.");

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(
            descriptor.DefaultTimeoutSeconds > 0 ? descriptor.DefaultTimeoutSeconds : _options.DefaultCommandTimeoutSeconds);

        var envelope = new CommandEnvelope
        {
            CommandId = Guid.CreateVersion7().ToString("n"),
            Command = command,
            IdempotencyKey = idempotencyKey,
            // Fresh on every send, including a retry that reuses the idempotency key. That pairing
            // is what makes a retry safe and a replay detectable at the same time.
            Nonce = RandomNumberGenerator.GetHexString(32, lowercase: true),
            IssuedAt = clock.GetUtcNow(),
            CorrelationId = Guid.CreateVersion7().ToString("n"),
            RequiredScope = descriptor.RequiredScope,
            TimeoutSeconds = (int)effectiveTimeout.TotalSeconds,
            Audit = new AuditMetadata
            {
                ActorId = currentUser.UserId?.ToString(),
                ActorName = currentUser.Email,
                TenantId = currentUser.WorkspaceId?.ToString(),
                SourceIp = sourceIp,
                Reason = reason,
            },
            Payload = JsonSerializer.SerializeToElement(payload, NodeContract.Json),
        };

        var record = new NodeCommandRecord
        {
            NodeRowId = node.Id,
            NodeId = node.NodeId,
            CommandId = envelope.CommandId,
            Command = command,
            IdempotencyKey = idempotencyKey,
            CorrelationId = envelope.CorrelationId,
            Nonce = envelope.Nonce,
            RequiredScope = descriptor.RequiredScope,
            PayloadJson = envelope.Payload.GetRawText(),
            Status = NodeCommandStatus.Queued,
            IssuedAt = envelope.IssuedAt,
            TimeoutSeconds = envelope.TimeoutSeconds ?? 0,
            IssuedByUserId = currentUser.UserId,
            IssuedByName = currentUser.Email,
            WorkspaceId = currentUser.WorkspaceId,
            SourceIp = sourceIp,
            Reason = reason,
        };

        db.NodeCommands.Add(record);
        await db.SaveChangesAsync(ct);

        var connection = registry.Get(nodeId);

        if (connection is null)
        {
            record.Status = NodeCommandStatus.Failed;
            record.ErrorCode = nameof(NodeErrorCode.NodeNotReady);
            record.ErrorMessage = "The node is not connected to this panel instance.";
            record.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            log.LogWarning("Cannot send {Command} to node {NodeId}: it is not connected.", command, nodeId);

            return new NodeCommandOutcome(envelope.CommandId, NodeCommandStatus.Failed, null,
                NodeErrorCode.NodeNotReady, record.ErrorMessage, false);
        }

        record.Status = NodeCommandStatus.Sent;
        record.SentAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        await audit.LogAsync($"node.command.{command}", "node", nodeId, sourceIp,
            metadataJson: JsonSerializer.Serialize(new
            {
                envelope.CommandId, idempotencyKey, reason,
            }, NodeContract.Json), ct: ct);

        CommandResult result;
        try
        {
            result = await connection.SendCommandAsync(envelope, effectiveTimeout, ct);
        }
        catch (NodeNotConnectedException)
        {
            result = new CommandResult
            {
                CommandId = envelope.CommandId,
                Status = CommandStatus.Failed,
                Error = NodeError.From(NodeErrorCode.NodeNotReady, "The node disconnected before the command was sent.", retryable: true),
                StartedAt = clock.GetUtcNow(),
                CompletedAt = clock.GetUtcNow(),
            };
        }

        // The session loop has usually written this already; doing it again from the caller's own
        // context makes the outcome durable even when the loop died between receiving and writing.
        await ApplyResultAsync(record, result, ct);

        log.LogInformation(
            "Node {NodeId} answered {Command} ({CommandId}) with {Status}{Replay}.",
            nodeId, command, envelope.CommandId, result.Status,
            result.IdempotentReplay ? " (replayed from its ledger)" : string.Empty);

        return new NodeCommandOutcome(
            envelope.CommandId,
            record.Status,
            result.Result,
            result.Error?.Code,
            result.Error?.Message,
            result.IdempotentReplay);
    }

    /// <summary>
    /// Ask a node to abandon a command. Best effort — some steps cannot be interrupted, and the
    /// node's own result is still the authoritative answer.
    /// </summary>
    public async Task<bool> CancelAsync(string nodeId, string commandId, string? reason, CancellationToken ct)
    {
        var connection = registry.Get(nodeId);
        if (connection is null) return false;

        await connection.SendAsync(ControlFrames.Cancel,
            new CommandCancel { CommandId = commandId, Reason = reason }, commandId, ct);

        await audit.LogAsync("node.command.cancel", "node", nodeId,
            metadataJson: JsonSerializer.Serialize(new { commandId, reason }), ct: ct);

        return true;
    }

    /// <summary>
    /// Stream a workload's logs, yielding chunks as the node produces them.
    ///
    /// <para>
    /// The subscription is registered before the command is sent, for the same reason a result
    /// waiter is: a node that starts streaming immediately would otherwise have its first lines
    /// arrive before anything was listening.
    /// </para>
    /// </summary>
    public async Task<NodeCommandOutcome> StreamLogsAsync(
        string nodeId, StreamLogsRequest request, Func<LogChunk, Task> onChunk,
        TimeSpan timeout, CancellationToken ct)
    {
        var connection = registry.Get(nodeId)
            ?? throw new NodeNotConnectedException(nodeId);

        var commandId = Guid.CreateVersion7().ToString("n");
        using var subscription = connection.SubscribeLogs(commandId, onChunk);

        return await SendPreparedAsync(nodeId, commandId, NodeCommands.StreamLogs, request,
            $"logs:{request.WorkloadId}:{commandId}", timeout, ct);
    }

    /// <summary>Send with a command id the caller has already used to subscribe to something.</summary>
    private async Task<NodeCommandOutcome> SendPreparedAsync(
        string nodeId, string commandId, string command, object payload,
        string idempotencyKey, TimeSpan timeout, CancellationToken ct)
    {
        var node = await db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.NodeId == nodeId, ct)
            ?? throw new NodeNotFoundException(nodeId);

        NodeCommandCatalog.TryGet(command, out var descriptor);

        var envelope = new CommandEnvelope
        {
            CommandId = commandId,
            Command = command,
            IdempotencyKey = idempotencyKey,
            Nonce = RandomNumberGenerator.GetHexString(32, lowercase: true),
            IssuedAt = clock.GetUtcNow(),
            CorrelationId = commandId,
            RequiredScope = descriptor.RequiredScope,
            TimeoutSeconds = (int)timeout.TotalSeconds,
            Audit = new AuditMetadata
            {
                ActorId = currentUser.UserId?.ToString(),
                ActorName = currentUser.Email,
                TenantId = currentUser.WorkspaceId?.ToString(),
            },
            Payload = JsonSerializer.SerializeToElement(payload, NodeContract.Json),
        };

        var record = new NodeCommandRecord
        {
            NodeRowId = node.Id,
            NodeId = node.NodeId,
            CommandId = commandId,
            Command = command,
            IdempotencyKey = idempotencyKey,
            CorrelationId = envelope.CorrelationId,
            Nonce = envelope.Nonce,
            RequiredScope = envelope.RequiredScope,
            PayloadJson = envelope.Payload.GetRawText(),
            Status = NodeCommandStatus.Sent,
            IssuedAt = envelope.IssuedAt,
            SentAt = clock.GetUtcNow(),
            TimeoutSeconds = envelope.TimeoutSeconds ?? 0,
            IssuedByUserId = currentUser.UserId,
            IssuedByName = currentUser.Email,
        };

        db.NodeCommands.Add(record);
        await db.SaveChangesAsync(ct);

        var connection = registry.Get(nodeId) ?? throw new NodeNotConnectedException(nodeId);
        var result = await connection.SendCommandAsync(envelope, timeout, ct);

        await ApplyResultAsync(record, result, ct);

        return new NodeCommandOutcome(commandId, record.Status, result.Result,
            result.Error?.Code, result.Error?.Message, result.IdempotentReplay);
    }

    private async Task ApplyResultAsync(NodeCommandRecord record, CommandResult result, CancellationToken ct)
    {
        record.Status = result.Status switch
        {
            CommandStatus.Succeeded => NodeCommandStatus.Succeeded,
            CommandStatus.Failed => NodeCommandStatus.Failed,
            CommandStatus.Cancelled => NodeCommandStatus.Cancelled,
            CommandStatus.TimedOut => NodeCommandStatus.TimedOut,
            _ => NodeCommandStatus.Rejected,
        };

        record.CompletedAt = result.CompletedAt == default ? clock.GetUtcNow() : result.CompletedAt;
        record.ResultJson = result.Result?.GetRawText();
        record.ErrorCode = result.Error?.Code.ToString();
        record.ErrorMessage = result.Error?.Message;
        record.IdempotentReplay = result.IdempotentReplay;

        await db.SaveChangesAsync(ct);
    }

    private NodeCommandOutcome Refused(Node node, string command, NodeErrorCode code, string message)
    {
        log.LogWarning("Refused to send {Command} to node {NodeId}: {Message}", command, node.NodeId, message);
        return new NodeCommandOutcome(string.Empty, NodeCommandStatus.Rejected, null, code, message, false);
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, NodeContract.Json); }
        catch (JsonException) { return null; }
    }
}

public sealed class NodeNotFoundException(string nodeId)
    : Exception($"No node '{nodeId}' is enrolled with this panel.")
{
    public string NodeId { get; } = nodeId;
}
