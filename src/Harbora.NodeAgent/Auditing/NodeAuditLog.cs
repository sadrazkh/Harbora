using System.Text.Json;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Security;
using Harbora.NodeAgent.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Auditing;

/// <summary>One entry in the node's own audit trail.</summary>
public sealed record NodeAuditEntry
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public required string Action { get; init; }
    public required string Outcome { get; init; }
    public string? CommandId { get; init; }
    public string? CorrelationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? TenantId { get; init; }
    public string? ActorId { get; init; }
    public string? ActorName { get; init; }
    public string? SourceIp { get; init; }
    public string? Reason { get; init; }
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }
    public string? ErrorCode { get; init; }
    public string? Detail { get; init; }
    public long? DurationMs { get; init; }
}

/// <summary>
/// Append-only, local, redacted.
///
/// <para>
/// Local is the point. The control plane already logs what it asked for; this records what the
/// node actually did, and it survives the control plane being unreachable, wrong, or the thing
/// under investigation. An operator with SSH to the box can answer "what happened here" without
/// asking the system that may be the problem.
/// </para>
/// </summary>
public sealed class NodeAuditLog(
    IOptions<NodeAgentOptions> options,
    SecretRedactor redactor,
    ILogger<NodeAuditLog> log)
{
    /// <summary>Rotated at this size so a chatty week cannot fill a small VPS's disk.</summary>
    private const long MaxBytes = 32 * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly string _path = options.Value.AuditLogPath;

    public void Write(NodeAuditEntry entry)
    {
        try
        {
            var json = JsonSerializer.Serialize(entry, NodeContract.Json);
            var line = redactor.Redact(json);

            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                FilePermissions.RestrictDirectory(directory);

                RotateIfLarge();

                File.AppendAllText(_path, line + System.Environment.NewLine);
                FilePermissions.RestrictFile(_path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never let auditing break the action being audited. A failure to record is a warning;
            // a failure to deploy because recording failed would be an outage caused by bookkeeping.
            log.LogWarning(e, "Could not append to the node audit log at {Path}.", _path);
        }
    }

    /// <summary>Record the admission or rejection of a command, before it runs.</summary>
    public void CommandReceived(CommandEnvelope envelope, string outcome, NodeError? error = null) =>
        Write(new NodeAuditEntry
        {
            Action = $"command.{envelope.Command}",
            Outcome = outcome,
            CommandId = envelope.CommandId,
            CorrelationId = envelope.CorrelationId,
            IdempotencyKey = envelope.IdempotencyKey,
            TenantId = envelope.Audit?.TenantId,
            ActorId = envelope.Audit?.ActorId,
            ActorName = envelope.Audit?.ActorName,
            SourceIp = envelope.Audit?.SourceIp,
            Reason = envelope.Audit?.Reason,
            ErrorCode = error?.Code.ToString(),
            Detail = error?.Message,
        });

    /// <summary>Record what a command produced.</summary>
    public void CommandCompleted(CommandEnvelope envelope, CommandResult result, long durationMs) =>
        Write(new NodeAuditEntry
        {
            Action = $"command.{envelope.Command}",
            Outcome = result.Status.ToString().ToLowerInvariant(),
            CommandId = envelope.CommandId,
            CorrelationId = envelope.CorrelationId,
            IdempotencyKey = envelope.IdempotencyKey,
            TenantId = envelope.Audit?.TenantId,
            ActorId = envelope.Audit?.ActorId,
            ActorName = envelope.Audit?.ActorName,
            SourceIp = envelope.Audit?.SourceIp,
            ErrorCode = result.Error?.Code.ToString(),
            Detail = result.Error?.Message,
            DurationMs = durationMs,
        });

    /// <summary>Entries currently on disk, newest last. For the troubleshooting tooling and tests.</summary>
    public IReadOnlyList<NodeAuditEntry> Read(int maxEntries = 200)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return [];

            return File.ReadLines(_path)
                .TakeLast(maxEntries)
                .Select(TryParse)
                .OfType<NodeAuditEntry>()
                .ToList();
        }
    }

    private static NodeAuditEntry? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<NodeAuditEntry>(line, NodeContract.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void RotateIfLarge()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < MaxBytes) return;

        var archived = _path + ".1";
        if (File.Exists(archived)) File.Delete(archived);

        File.Move(_path, archived);
        FilePermissions.RestrictFile(archived);
    }
}
