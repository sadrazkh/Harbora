using Harbora.Domain.Common;

namespace Harbora.Domain.Services;

/// <summary>
/// One logical database inside a <see cref="ManagedService"/> instance (D1, 2026-08-25
/// shared-databases plan) — its own name, its own login, its own password, so two apps attached to
/// the same PostgreSQL no longer have to share the one database and the one admin login the instance
/// itself was provisioned with.
///
/// <para>
/// <see cref="IsDefault"/> marks the one row every instance that has a database at all is guaranteed
/// to carry: the database (and login) the instance was actually provisioned with —
/// <see cref="ManagedService.DatabaseName"/>/<see cref="ManagedService.Username"/>/
/// <see cref="ManagedService.EncryptedPassword"/>, copied here rather than replaced by it. Every
/// one-off container Harbora runs against this instance — provisioning it, testing the connection,
/// creating another logical database — still connects as that same admin login against that same
/// database name, so this one row can never be deleted on its own without breaking every operation
/// the instance still needs to perform on itself. Deleting the whole instance is the only way to
/// remove it (<c>LogicalDatabaseService.DeleteAsync</c>).
/// </para>
///
/// <para>
/// An attachment made before this shipped, or made to an engine this cannot open a second database
/// on (Redis, RabbitMQ, NATS — see <c>DatabaseGrantSql.Supports</c>), carries no row here at all:
/// <see cref="AppManagedService.ManagedServiceDatabaseId"/> stays null and resolution falls back to
/// the instance's own admin credentials exactly as it always did (<c>AttachedServiceConnectionResolver</c>,
/// <c>Harbora.Infrastructure.Services.ManagedServiceAttachEnv</c>).
/// </para>
/// </summary>
public class ManagedServiceDatabase : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public Guid ManagedServiceId { get; set; }
    public ManagedService? ManagedService { get; set; }

    /// <summary>
    /// The actual name this database carries inside the engine. Unique per instance, and
    /// collision-proof by construction — see <see cref="LogicalDatabaseName.Resolve"/>, the same idiom
    /// <see cref="AppManagedServiceAlias.Resolve"/> already uses for an attachment's alias.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted at rest, exactly like <see cref="ManagedService.EncryptedPassword"/> — this is a live
    /// credential an attached app's own container needs in plaintext, not a one-time secret a human
    /// reads once, so unlike <c>DatabaseAccessGrant.PasswordHash</c> it cannot be a hash.
    /// </summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is the instance's own admin database rather than one created on request. See the
    /// type doc for why it cannot be deleted without deleting the whole instance.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>Apps attached to this specific logical database.</summary>
    public ICollection<AppManagedService> Apps { get; set; } = new List<AppManagedService>();

    /// <summary>
    /// Whether the pgvector extension is installed inside this specific logical database, as last
    /// confirmed by the engine itself (1.7, pgvector-as-option plan) — never a flag Harbora sets on
    /// its own belief. Null means never checked, the same "not measured" state
    /// <see cref="ManagedService.StorageBytes"/>/<see cref="ManagedService.StorageMeasuredAt"/>
    /// already uses this platform's idiom for; false means the engine was asked and either said no or
    /// refused the request outright. Meaningless for an instance whose engine
    /// <see cref="Harbora.Infrastructure.Services.DatabaseGrantSql.SupportsVectorExtension"/> does not
    /// name, and — because the extension lives inside the container's image, not inside any one
    /// database — may go stale if the instance is later rebuilt without pgvector support; only a
    /// fresh <c>LogicalDatabaseService.EnableVectorExtensionAsync</c> call re-confirms it.
    /// </summary>
    public bool? HasVectorExtension { get; set; }

    /// <summary>When <see cref="HasVectorExtension"/> was last confirmed against the engine, or null
    /// if never. A figure with no timestamp beside it is trusted for longer than it should be.</summary>
    public DateTimeOffset? VectorExtensionCheckedAt { get; set; }

    /// <summary>
    /// The default row a freshly created <see cref="ManagedService"/> gets alongside itself, for every
    /// engine that has a database name at all — every one of this platform's three creation paths
    /// (<c>DatabasesController.Create</c>, <c>EnvironmentCloner.CloneAsync</c>,
    /// <c>TemplateDeploymentService</c>) calls this once, right after adding the service itself, so a
    /// database created from here on always has somewhere for its first attachment to point.
    ///
    /// <para>
    /// The password is copied as ciphertext, not re-encrypted — <paramref name="service"/>'s own
    /// <see cref="ManagedService.EncryptedPassword"/> is already protected, and re-protecting it would
    /// need the plaintext this method deliberately never sees.
    /// </para>
    ///
    /// <para>
    /// Null for an engine with no database name at all (Redis, RabbitMQ, NATS —
    /// <c>ServiceDefinition.HasDatabaseName</c> is false), because there is no name to materialise and
    /// no logical-database story these engines support (<c>DatabaseGrantSql.Supports</c>).
    /// </para>
    /// </summary>
    public static ManagedServiceDatabase? DefaultFor(ManagedService service)
    {
        if (string.IsNullOrEmpty(service.DatabaseName)) return null;

        return new ManagedServiceDatabase
        {
            WorkspaceId = service.WorkspaceId,
            ManagedServiceId = service.Id,
            Name = service.DatabaseName,
            Username = service.Username,
            EncryptedPassword = service.EncryptedPassword,
            IsDefault = true
        };
    }
}
