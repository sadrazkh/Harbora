using Harbora.NodeAgent.Auditing;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.Security;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Tunnels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Database;

/// <summary>A grant as the node stores it. The password is encrypted; nothing else here is secret.</summary>
public sealed record GrantRecord
{
    public required string GrantId { get; init; }
    public required string TenantId { get; init; }
    public required string WorkloadId { get; init; }
    public required string Engine { get; init; }
    public required DatabaseAccessMode Mode { get; init; }
    public required DatabaseAccessState State { get; init; }

    public required string Container { get; init; }
    public required int Port { get; init; }
    public string? DatabaseName { get; init; }

    public required string Username { get; init; }

    /// <summary>AES-GCM blob from <see cref="LocalSecretVault"/>. Never returned to a status read.</summary>
    public required string ProtectedPassword { get; init; }

    public bool ReadOnly { get; init; }
    public IReadOnlyList<string> IpAllowlist { get; init; } = [];
    public int MaxConnections { get; init; }
    public int MaxConnectionsPerMinute { get; init; }
    public bool RequireMutualTls { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public string? RevokedReason { get; init; }
    public string? Endpoint { get; init; }

    public string? ActorName { get; init; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiry && now >= expiry;

    public override string ToString() =>
        $"Grant {GrantId} [{Engine}/{Mode}] {State} user={Username}";
}

public sealed record GrantStoreState
{
    public IReadOnlyList<GrantRecord> Grants { get; init; } = [];
}

/// <summary>
/// Owns external database access on this node: minting a scoped credential, publishing it through
/// an outbound tunnel, expiring it, rotating it and revoking it.
///
/// <para>
/// The expiry is enforced here rather than by the control plane. A control plane that goes away
/// mid-grant must not leave the door open, and the only party that can guarantee it closes is the
/// one holding the socket.
/// </para>
/// </summary>
public sealed class DatabaseAccessManager(
    IOptions<NodeAgentOptions> options,
    JsonFileStore<GrantStoreState> store,
    JsonFileStore<NodeState> nodeState,
    WorkloadRegistry workloads,
    DatabaseEngineOperations engines,
    TunnelSupervisor tunnels,
    NodeIdentityStore identities,
    LocalSecretVault vault,
    SecretRedactor redactor,
    NodeAuditLog audit,
    NodeMetrics metrics,
    INodeEventPublisher events,
    TimeProvider clock,
    ILogger<DatabaseAccessManager> log)
{
    /// <summary>Longest a temporary grant may live. Beyond a week it is a persistent grant in denial.</summary>
    public static readonly TimeSpan MaxTemporaryTtl = TimeSpan.FromDays(7);

    public static readonly TimeSpan MinTemporaryTtl = TimeSpan.FromMinutes(1);

    private readonly NodeAgentOptions _options = options.Value;
    private readonly Lock _gate = new();

    public sealed class GrantException(NodeErrorCode code, string message) : Exception(message)
    {
        public NodeErrorCode Code { get; } = code;
    }

    public IReadOnlyList<GrantRecord> All()
    {
        lock (_gate) return store.Load()?.Grants ?? [];
    }

    public GrantRecord? Find(string grantId, string? tenantId)
    {
        lock (_gate)
            return All().FirstOrDefault(g => g.GrantId == grantId && (tenantId is null || g.TenantId == tenantId));
    }

    public int ActiveCount => All().Count(g => g.State == DatabaseAccessState.Active);

    /// <summary>Mint a credential and publish it through a tunnel.</summary>
    public async Task<DatabaseAccessGrantState> CreateAsync(DatabaseAccessGrantSpec spec, CancellationToken ct)
    {
        Validate(spec);

        if (Find(spec.GrantId, spec.TenantId) is { State: DatabaseAccessState.Active })
            throw new GrantException(NodeErrorCode.ValidationFailed, $"Grant '{spec.GrantId}' is already active on this node.");

        var workload = workloads.Find(spec.WorkloadId, spec.TenantId)
            ?? throw new GrantException(NodeErrorCode.GrantNotFound,
                $"No workload '{spec.WorkloadId}' is deployed for this tenant on this node.");

        var container = workload.Spec.Containers.FirstOrDefault(c => c.Name == spec.TargetContainer)
            ?? throw new GrantException(NodeErrorCode.ValidationFailed,
                $"Workload '{workload.Name}' has no container named '{spec.TargetContainer}'.");

        var admin = AdminFor(spec.Engine, container)
            ?? throw new GrantException(NodeErrorCode.CredentialRotationFailed,
                $"Could not find admin credentials for {spec.Engine} in the workload's specification. " +
                "The node mints grants using the database's own admin login, which must be part of the deployed spec.");

        var username = LocalSecretVault.GenerateUsername($"harbora_{(spec.Mode == DatabaseAccessMode.Temporary ? "tmp" : "ext")}");
        var password = LocalSecretVault.GeneratePassword();
        redactor.Register(password);

        var containerName = workload.ContainerName(container.Name);
        var port = spec.TargetPort ?? DatabaseEngines.DefaultPort(spec.Engine);

        try
        {
            await engines.CreateUserAsync(
                spec.Engine, containerName, admin, username, password, spec.DatabaseName, spec.ReadOnly, ct);
        }
        catch (DatabaseEngineOperations.EngineException e)
        {
            throw new GrantException(e.Code, e.Message);
        }

        var now = clock.GetUtcNow();

        var record = new GrantRecord
        {
            GrantId = spec.GrantId,
            TenantId = spec.TenantId,
            WorkloadId = spec.WorkloadId,
            Engine = spec.Engine.ToLowerInvariant(),
            Mode = spec.Mode,
            State = DatabaseAccessState.Pending,
            Container = containerName,
            Port = port,
            DatabaseName = spec.DatabaseName,
            Username = username,
            ProtectedPassword = vault.Protect(password),
            ReadOnly = spec.ReadOnly,
            IpAllowlist = spec.IpAllowlist,
            MaxConnections = spec.MaxConnections,
            MaxConnectionsPerMinute = spec.MaxConnectionsPerMinute,
            RequireMutualTls = spec.RequireMutualTls,
            CreatedAt = now,
            ExpiresAt = spec.Mode == DatabaseAccessMode.Temporary
                ? now + TimeSpan.FromSeconds(spec.TtlSeconds!.Value)
                : null,
            ActorName = spec.Audit?.ActorName,
        };

        Save(record);

        var tunnel = await PublishAsync(record, ct);

        var published = record with
        {
            State = tunnel.Status == TunnelStatus.Connected ? DatabaseAccessState.Active : DatabaseAccessState.Failed,
            Endpoint = tunnel.PublicEndpoint,
        };

        Save(published);

        audit.Write(new NodeAuditEntry
        {
            Action = "database-access.create",
            Outcome = published.State.ToString().ToLowerInvariant(),
            TargetType = "grant",
            TargetId = spec.GrantId,
            TenantId = spec.TenantId,
            ActorId = spec.Audit?.ActorId,
            ActorName = spec.Audit?.ActorName,
            SourceIp = spec.Audit?.SourceIp,
            Reason = spec.Audit?.Reason,
            Detail = $"{spec.Engine} user {username}, {(spec.ReadOnly ? "read-only" : "read-write")}, " +
                     $"{(published.ExpiresAt is { } expiry ? $"expires {expiry:u}" : "persistent")}, " +
                     $"allowlist [{string.Join(", ", spec.IpAllowlist)}]",
        });

        metrics.GrantCreated(published.Engine, published.Mode);
        metrics.ActiveGrants(ActiveCount);

        log.LogInformation("{Grant} published at {Endpoint}.", published, published.Endpoint ?? "(not published)");

        // The password is returned exactly once. Every later read of this grant reports the
        // username and the endpoint, never the credential.
        return ToState(published, password, tunnel);
    }

    /// <summary>Close the tunnel, drop the engine user, and mark the grant revoked.</summary>
    public async Task<DatabaseAccessGrantState> RevokeAsync(
        string grantId, string? tenantId, string? reason, bool dropEngineUser, CancellationToken ct)
    {
        var record = Find(grantId, tenantId)
            ?? throw new GrantException(NodeErrorCode.GrantNotFound, $"No grant '{grantId}' on this node.");

        // The tunnel first. Dropping the user while a session is open leaves that session working
        // on some engines, so the socket is the thing that actually ends access.
        await tunnels.StopAsync(grantId);

        if (dropEngineUser) await DropUserAsync(record, ct);

        var revoked = record with
        {
            State = DatabaseAccessState.Revoked,
            RevokedAt = clock.GetUtcNow(),
            RevokedReason = reason,
            Endpoint = null,
        };

        Save(revoked);
        ForgetPassword(record);

        audit.Write(new NodeAuditEntry
        {
            Action = "database-access.revoke",
            Outcome = "revoked",
            TargetType = "grant",
            TargetId = grantId,
            TenantId = record.TenantId,
            Reason = reason,
            Detail = $"{record.Engine} user {record.Username}",
        });

        metrics.GrantEnded(record.Engine, "revoked");
        metrics.ActiveGrants(ActiveCount);

        await events.PublishAsync(new NodeEvent
        {
            Kind = NodeEventKinds.DatabaseGrantRevoked,
            Message = $"Grant {grantId} revoked{(reason is null ? string.Empty : $": {reason}")}",
            Data = new Dictionary<string, string> { ["grantId"] = grantId, ["engine"] = record.Engine },
        }, ct);

        return ToState(revoked, password: null, tunnels.StateFor(grantId));
    }

    /// <summary>Issue a new password for an existing grant, keeping the same user and endpoint.</summary>
    public async Task<DatabaseAccessGrantState> RotateAsync(
        string grantId, string? tenantId, int overlapSeconds, CancellationToken ct)
    {
        var record = Find(grantId, tenantId)
            ?? throw new GrantException(NodeErrorCode.GrantNotFound, $"No grant '{grantId}' on this node.");

        if (record.State is DatabaseAccessState.Revoked or DatabaseAccessState.Expired)
            throw new GrantException(NodeErrorCode.GrantRevoked, $"Grant '{grantId}' is {record.State} and cannot be rotated.");

        var admin = AdminForRecord(record)
            ?? throw new GrantException(NodeErrorCode.CredentialRotationFailed,
                "Could not find admin credentials for this engine in the workload's specification.");

        var password = LocalSecretVault.GeneratePassword();
        redactor.Register(password);

        // No engine among the four supports two live passwords for one user, so an overlap cannot
        // be honoured. Saying so beats accepting the parameter and quietly ignoring it.
        if (overlapSeconds > 0)
            log.LogWarning(
                "Grant {GrantId} asked for a {Overlap}s credential overlap; {Engine} cannot hold two passwords for one user, so the change is immediate.",
                grantId, overlapSeconds, record.Engine);

        try
        {
            await engines.RotatePasswordAsync(record.Engine, record.Container, admin, record.Username, password, ct);
        }
        catch (DatabaseEngineOperations.EngineException e)
        {
            throw new GrantException(e.Code, e.Message);
        }

        ForgetPassword(record);

        var rotated = record with { ProtectedPassword = vault.Protect(password) };
        Save(rotated);

        audit.Write(new NodeAuditEntry
        {
            Action = "database-access.rotate",
            Outcome = "rotated",
            TargetType = "grant",
            TargetId = grantId,
            TenantId = record.TenantId,
            Detail = $"{record.Engine} user {record.Username}",
        });

        log.LogInformation("Rotated the credential for grant {GrantId}.", grantId);

        return ToState(rotated, password, tunnels.StateFor(grantId));
    }

    /// <summary>
    /// Expire what is due and re-publish what is not.
    ///
    /// <para>
    /// Called on a timer and at startup. The startup case is the one that matters: a node that was
    /// powered off through a grant's expiry must close it on the way back up, not resume it.
    /// </para>
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        foreach (var record in All().Where(g => g.State is DatabaseAccessState.Active or DatabaseAccessState.Pending))
        {
            ct.ThrowIfCancellationRequested();

            if (!record.IsExpired(now)) continue;

            log.LogInformation("Grant {GrantId} expired at {Expiry:u}; closing it.", record.GrantId, record.ExpiresAt);

            await tunnels.StopAsync(record.GrantId);
            await DropUserAsync(record, ct);

            Save(record with { State = DatabaseAccessState.Expired, Endpoint = null });
            ForgetPassword(record);

            audit.Write(new NodeAuditEntry
            {
                Action = "database-access.expire",
                Outcome = "expired",
                TargetType = "grant",
                TargetId = record.GrantId,
                TenantId = record.TenantId,
                Detail = $"{record.Engine} user {record.Username}",
            });

            metrics.GrantEnded(record.Engine, "expired");

            await events.PublishAsync(new NodeEvent
            {
                Kind = NodeEventKinds.DatabaseGrantExpired,
                Message = $"Grant {record.GrantId} expired",
                Data = new Dictionary<string, string> { ["grantId"] = record.GrantId, ["engine"] = record.Engine },
            }, ct);
        }

        metrics.ActiveGrants(ActiveCount);
    }

    /// <summary>Re-open tunnels for grants that are still valid after a restart.</summary>
    public async Task RestoreAsync(CancellationToken ct)
    {
        await SweepAsync(ct);

        foreach (var record in All().Where(g => g.State == DatabaseAccessState.Active))
        {
            try
            {
                var tunnel = await PublishAsync(record, ct);
                Save(record with { Endpoint = tunnel.PublicEndpoint });

                log.LogInformation("Re-published grant {GrantId} at {Endpoint}.", record.GrantId, tunnel.PublicEndpoint);
            }
            catch (Exception e) when (e is GrantException or IOException)
            {
                log.LogWarning(e, "Could not re-publish grant {GrantId} after restart.", record.GrantId);
            }
        }

        metrics.ActiveGrants(ActiveCount);
    }

    /// <summary>Status for a grant, with the password deliberately absent.</summary>
    public DatabaseAccessGrantState? StatusOf(string grantId, string? tenantId)
    {
        var record = Find(grantId, tenantId);
        return record is null ? null : ToState(record, password: null, tunnels.StateFor(grantId));
    }

    // --- internals ---

    private void Validate(DatabaseAccessGrantSpec spec)
    {
        if (!DatabaseEngines.IsSupported(spec.Engine))
            throw new GrantException(NodeErrorCode.UnsupportedDatabaseEngine,
                $"Engine '{spec.Engine}' is not one this node can mint access for ({string.Join(", ", DatabaseEngines.All)}).");

        if (spec.Mode == DatabaseAccessMode.Persistent)
        {
            // Consent is required, not inferred. A persistent grant is an open door with a name on
            // it, and the node refusing to create one nobody confirmed is the last check there is.
            if (!spec.OperatorConfirmed)
                throw new GrantException(NodeErrorCode.ValidationFailed,
                    "A persistent grant requires explicit operator confirmation. Turn it on in the panel first.");

            if (spec.IpAllowlist.Count == 0)
                throw new GrantException(NodeErrorCode.ValidationFailed,
                    "A persistent grant requires an IP allowlist. Publishing a database to every address on the internet is not something this node will do.");
        }

        if (spec.Mode == DatabaseAccessMode.Temporary)
        {
            if (spec.TtlSeconds is not { } ttl)
                throw new GrantException(NodeErrorCode.ValidationFailed,
                    "A temporary grant needs a ttlSeconds; without one it is a persistent grant with better branding.");

            var lifetime = TimeSpan.FromSeconds(ttl);

            if (lifetime < MinTemporaryTtl || lifetime > MaxTemporaryTtl)
                throw new GrantException(NodeErrorCode.ValidationFailed,
                    $"ttlSeconds must be between {MinTemporaryTtl.TotalSeconds:0} and {MaxTemporaryTtl.TotalSeconds:0}.");

            if (spec.IpAllowlist.Count == 0)
                log.LogWarning(
                    "Grant {GrantId} is temporary but has no IP allowlist; it will be reachable from any address until it expires.",
                    spec.GrantId);
        }

        foreach (var entry in spec.IpAllowlist)
            if (!IsUsableAllowlistEntry(entry))
                throw new GrantException(NodeErrorCode.ValidationFailed,
                    $"'{entry}' is not an address or CIDR block the gateway can enforce.");

        if (spec.MaxConnections is < 1 or > 1000)
            throw new GrantException(NodeErrorCode.ValidationFailed, "maxConnections must be between 1 and 1000.");

        if (!vault.IsAvailable)
            throw new GrantException(NodeErrorCode.NodeNotReady,
                "The node has no identity yet, so a grant credential could not be stored safely.");
    }

    /// <summary>
    /// Accepts an address or a CIDR block. Rejects the ones that only look like a restriction —
    /// <c>0.0.0.0/0</c> allows everything, and an allowlist that allows everything is a field the
    /// panel can point at while nothing is actually restricted.
    /// </summary>
    internal static bool IsUsableAllowlistEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false;

        var parts = entry.Split('/');

        if (!System.Net.IPAddress.TryParse(parts[0], out var address)) return false;

        if (parts.Length == 1) return true;
        if (parts.Length > 2) return false;

        if (!int.TryParse(parts[1], out var prefix)) return false;

        var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefix < 0 || prefix > maxPrefix) return false;

        return prefix != 0;
    }

    private async Task<TunnelState> PublishAsync(GrantRecord record, CancellationToken ct)
    {
        // Enrollment is the normal source; configuration overrides it for a development gateway.
        var gateway = _options.TunnelGatewayUrl ?? nodeState.Load()?.TunnelGatewayUrl
            ?? throw new GrantException(NodeErrorCode.TunnelUnavailable,
                "This node has no TCP gateway configured, so a database cannot be published. " +
                "The control plane supplies the gateway address at enrollment.");

        var identity = identities.Load()
            ?? throw new GrantException(NodeErrorCode.NodeNotReady, "The node has no credential to authenticate the tunnel with.");

        var registration = new TunnelRegistration
        {
            NodeId = record.GrantId,
            TunnelId = $"tun-{record.GrantId}",
            GrantId = record.GrantId,
            TenantId = record.TenantId,
            IpAllowlist = record.IpAllowlist,
            MaxConnections = record.MaxConnections,
            MaxConnectionsPerMinute = record.MaxConnectionsPerMinute,
            RequireMutualTls = record.RequireMutualTls,
        };

        return await tunnels.StartAsync(
            gateway, identity, registration,
            new TunnelTarget(record.Container, record.Port),
            TimeSpan.FromSeconds(30), ct);
    }

    private async Task DropUserAsync(GrantRecord record, CancellationToken ct)
    {
        var admin = AdminForRecord(record);

        if (admin is null)
        {
            log.LogWarning(
                "Could not find admin credentials to drop {User} on {Engine}; the tunnel is closed but the engine user remains.",
                record.Username, record.Engine);
            return;
        }

        await engines.DropUserAsync(record.Engine, record.Container, admin, record.Username, record.DatabaseName, ct);
    }

    private EngineAdmin? AdminForRecord(GrantRecord record)
    {
        var workload = workloads.Find(record.WorkloadId, record.TenantId);
        var container = workload?.Spec.Containers.FirstOrDefault(c => record.Container.EndsWith($"-{c.Name}-" + workload.ReleaseId, StringComparison.Ordinal))
                        ?? workload?.Spec.Containers.FirstOrDefault();

        return container is null ? null : AdminFor(record.Engine, container);
    }

    /// <summary>
    /// Admin credentials come from the workload's own spec — the node already holds them, because
    /// it is what deployed the database. No second credential store, and nothing extra for the
    /// control plane to send at grant time.
    /// </summary>
    private static EngineAdmin? AdminFor(string engine, ContainerSpec container)
    {
        var environment = new Dictionary<string, string>(container.Env, StringComparer.Ordinal);

        foreach (var secret in container.Secrets.Where(s => s.MountAs == SecretMount.Environment))
            environment[secret.Name] = secret.Value;

        return DatabaseEngineOperations.AdminFrom(engine, environment);
    }

    private DatabaseAccessGrantState ToState(GrantRecord record, string? password, TunnelState? tunnel) => new()
    {
        GrantId = record.GrantId,
        State = record.State,
        Engine = record.Engine,
        Mode = record.Mode,
        CreatedAt = record.CreatedAt,
        ExpiresAt = record.ExpiresAt,
        RevokedAt = record.RevokedAt,
        RevokedReason = record.RevokedReason,
        Username = record.Username,
        Password = password,
        Endpoint = record.Endpoint,
        Tunnel = tunnel,
        ActiveConnections = tunnel?.ActiveConnections ?? 0,
        LastConnectionAt = tunnel?.LastActivityAt,
    };

    private void Save(GrantRecord record)
    {
        lock (_gate)
        {
            var state = store.Load() ?? new GrantStoreState();
            store.Save(state with
            {
                Grants = state.Grants.Where(g => g.GrantId != record.GrantId).Append(record).ToList(),
            });
        }
    }

    /// <summary>Stop scrubbing a password the node no longer holds, so the redactor stays bounded.</summary>
    private void ForgetPassword(GrantRecord record)
    {
        try
        {
            redactor.Forget(vault.Unprotect(record.ProtectedPassword));
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException
                                  or LocalSecretVault.VaultUnavailableException)
        {
            // Not being able to decrypt a retired password is harmless; it just stays in the
            // redactor's set until the process restarts.
        }
    }
}
