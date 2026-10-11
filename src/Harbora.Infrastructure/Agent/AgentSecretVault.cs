using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Features;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// Every value the vault holds for one person, decrypted — for redaction by value. Anything going to the
/// model, or stored where the conversation can be read, is scrubbed of these, so a tool that echoed a
/// value it applied (an error message, say) still cannot carry it to the provider.
/// </summary>
public interface IAgentSecretValues
{
    IReadOnlyList<string> LiveValues(Guid workspaceId, Guid userId);
}

/// <summary>
/// The secrets a person gave the pane's lock button, held in this process's memory only — encrypted with
/// the panel's own <see cref="ISecretProtector"/> while held, for <see cref="AgentSecretHandles.Lifetime"/>,
/// resolvable only by the person who made them, in the workspace they made them in.
///
/// <para>
/// In memory on purpose: a value nobody wrote to disk cannot be found there later, and a restart
/// forgetting every handle is the safe failure — a step whose handle is gone is refused ("enter the
/// secret again"), never applied with something else. A singleton for the same reason: it is the store.
/// </para>
///
/// <para>
/// Expired entries are swept whenever the vault is used, and one person holds at most
/// <see cref="MaxPerPerson"/>; past that, their oldest handle is forgotten to make room, rather than the
/// lock button refusing them — a plan still pointing at it is then refused at execution, which says so.
/// </para>
/// </summary>
public sealed class InMemoryAgentSecretVault(ISecretProtector protector, ISystemClock clock)
    : IAgentSecretVault, IAgentSecretValues
{
    public const int MaxPerPerson = 50;

    private sealed record Entry(Guid WorkspaceId, Guid UserId, string Name, string Protected, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _admission = new();

    public AgentSecretView Put(Guid workspaceId, Guid userId, string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var now = clock.UtcNow;
        var entry = new Entry(workspaceId, userId, name, protector.Protect(value), now, now + AgentSecretHandles.Lifetime);
        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

        // Admission is serialised per vault — counting a person's entries and adding one must not race
        // another Put for the same person past the cap. Resolve never takes this lock.
        lock (_admission)
        {
            Sweep(now);
            var theirs = _entries.Where(e => e.Value.WorkspaceId == workspaceId && e.Value.UserId == userId)
                .OrderBy(e => e.Value.CreatedAt).ToList();
            foreach (var oldest in theirs.Take(Math.Max(0, theirs.Count - (MaxPerPerson - 1))))
                _entries.TryRemove(oldest.Key, out _);
            _entries[id] = entry;
        }

        return new AgentSecretView(AgentSecretHandles.Format(id), name, entry.ExpiresAt);
    }

    public string? Resolve(Guid workspaceId, Guid userId, string handle)
    {
        if (!AgentSecretHandles.IsHandle(handle)) return null;
        var trimmed = handle.Trim();
        var id = trimmed["{{secret:".Length..^"}}".Length];

        if (!_entries.TryGetValue(id, out var entry)) return null;
        if (clock.UtcNow >= entry.ExpiresAt)
        {
            _entries.TryRemove(id, out _);
            return null;
        }

        // Somebody else's handle is answered exactly like a handle that does not exist.
        if (entry.WorkspaceId != workspaceId || entry.UserId != userId) return null;
        return Unprotect(entry.Protected);
    }

    public IReadOnlyList<string> LiveValues(Guid workspaceId, Guid userId)
    {
        var now = clock.UtcNow;
        return _entries.Values
            .Where(e => e.WorkspaceId == workspaceId && e.UserId == userId && now < e.ExpiresAt)
            .Select(e => Unprotect(e.Protected))
            .OfType<string>()
            .ToList();
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var expired in _entries.Where(e => now >= e.Value.ExpiresAt).Select(e => e.Key).ToList())
            _entries.TryRemove(expired, out _);
    }

    private string? Unprotect(string stored)
    {
        try { return protector.Unprotect(stored); }
        catch (Exception) { return null; }
    }
}

/// <summary>What holding a secret produced: its handle, or why it was refused.</summary>
/// <param name="Code">"invalid" or "feature_not_enabled" when refused.</param>
public sealed record AgentSecretOutcome(AgentSecretView? View, string? Code = null, string? MessageEn = null, string? MessageFa = null)
{
    public bool Ok => View is not null;
}

/// <summary>
/// <c>POST /assistant/secrets</c> behind the controller: checks what the lock button sent, holds it in the
/// vault and audits that it did — by name only. The value is never logged, audited or returned.
/// </summary>
public sealed class AgentSecretService(IAgentSecretVault vault, IFeatureGate features, IAuditLogger audit)
{
    public const string SecretHeldAction = "assistant.secret_held";
    public const int MaxNameLength = 100;
    public const int MaxValueLength = 8000;

    public async Task<AgentSecretOutcome> HoldAsync(AgentActor actor, AgentSecretRequest? request, CancellationToken ct)
    {
        if (!(await features.EvaluateAsync(actor.WorkspaceId, PlatformFeatures.Assistant, ct)).IsEnabled)
            return new(null, "feature_not_enabled",
                "The assistant is not enabled for this workspace.", "دستیار برای این فضای کاری فعال نیست.");

        var name = request?.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            return new(null, "invalid",
                $"Give the secret a name of 1 to {MaxNameLength} characters, such as the variable it is for.",
                $"برای سکرت نامی بین ۱ تا {MaxNameLength} نویسه بگذارید، مثلاً نام متغیری که برای آن است.");

        // The value is taken exactly as typed — a secret's spaces may be part of it.
        var value = request!.Value;
        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength)
            return new(null, "invalid",
                $"The secret must be 1 to {MaxValueLength} characters.",
                $"سکرت باید بین ۱ تا {MaxValueLength} نویسه باشد.");

        var view = vault.Put(actor.WorkspaceId, actor.UserId, name, value);

        await audit.LogAsync(SecretHeldAction, targetType: "agent_secret", targetId: null, ipAddress: actor.IpAddress,
            metadataJson: JsonSerializer.Serialize(new { name, expiresAt = view.ExpiresAt }),
            workspaceId: actor.WorkspaceId, ct: ct);

        return new(view);
    }
}
