using Harbora.Domain.Common;
using Harbora.Shared.Inspection;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Which <see cref="ServiceCatalog"/> attach variable holds which part of a connection, per engine.
/// An example env file that says <c>DB_HOST</c> names a role; this is what turns that role into the
/// variable the attachment really sets, so the alias is computed with the rest rather than copied.
/// </summary>
public static class AttachEnvRoles
{
    /// <summary>
    /// The attach variable holding <paramref name="role"/> on this engine, or null when the engine has
    /// no such part: Redis has no user, a broker has no database name. Pinned against
    /// <see cref="ServiceCatalog.All"/> for every engine, so an answer is always a key that exists.
    /// </summary>
    public static string? SourceKeyFor(ManagedServiceType type, EnvAliasRole role) => (type, role) switch
    {
        (ManagedServiceType.PostgreSql, EnvAliasRole.Url) => "DATABASE_URL",
        (ManagedServiceType.PostgreSql, EnvAliasRole.Host) => "PGHOST",
        (ManagedServiceType.PostgreSql, EnvAliasRole.Port) => "PGPORT",
        (ManagedServiceType.PostgreSql, EnvAliasRole.User) => "PGUSER",
        (ManagedServiceType.PostgreSql, EnvAliasRole.Password) => "PGPASSWORD",
        (ManagedServiceType.PostgreSql, EnvAliasRole.Database) => "PGDATABASE",

        (ManagedServiceType.MySql, EnvAliasRole.Url) => "DATABASE_URL",
        (ManagedServiceType.MySql, EnvAliasRole.Host) => "MYSQL_HOST",
        (ManagedServiceType.MySql, EnvAliasRole.Port) => "MYSQL_PORT",
        (ManagedServiceType.MySql, EnvAliasRole.User) => "MYSQL_USER",
        (ManagedServiceType.MySql, EnvAliasRole.Password) => "MYSQL_PASSWORD",
        (ManagedServiceType.MySql, EnvAliasRole.Database) => "MYSQL_DATABASE",

        (ManagedServiceType.MariaDb, EnvAliasRole.Url) => "DATABASE_URL",
        (ManagedServiceType.MariaDb, EnvAliasRole.Host) => "DB_HOST",
        (ManagedServiceType.MariaDb, EnvAliasRole.Port) => "DB_PORT",
        (ManagedServiceType.MariaDb, EnvAliasRole.User) => "DB_USER",
        (ManagedServiceType.MariaDb, EnvAliasRole.Password) => "DB_PASSWORD",
        (ManagedServiceType.MariaDb, EnvAliasRole.Database) => "DB_DATABASE",

        (ManagedServiceType.Redis, EnvAliasRole.Url) => "REDIS_URL",
        (ManagedServiceType.Redis, EnvAliasRole.Host) => "REDIS_HOST",
        (ManagedServiceType.Redis, EnvAliasRole.Port) => "REDIS_PORT",
        (ManagedServiceType.Redis, EnvAliasRole.Password) => "REDIS_PASSWORD",

        (ManagedServiceType.MongoDb, EnvAliasRole.Url) => "MONGODB_URI",
        (ManagedServiceType.MongoDb, EnvAliasRole.Host) => "MONGO_HOST",
        (ManagedServiceType.MongoDb, EnvAliasRole.Port) => "MONGO_PORT",
        (ManagedServiceType.MongoDb, EnvAliasRole.User) => "MONGO_USER",
        (ManagedServiceType.MongoDb, EnvAliasRole.Password) => "MONGO_PASSWORD",
        (ManagedServiceType.MongoDb, EnvAliasRole.Database) => "MONGO_DATABASE",

        (ManagedServiceType.RabbitMq, EnvAliasRole.Url) => "RABBITMQ_URL",
        (ManagedServiceType.RabbitMq, EnvAliasRole.Host) => "RABBITMQ_HOST",
        (ManagedServiceType.RabbitMq, EnvAliasRole.Port) => "RABBITMQ_PORT",
        (ManagedServiceType.RabbitMq, EnvAliasRole.User) => "RABBITMQ_USER",
        (ManagedServiceType.RabbitMq, EnvAliasRole.Password) => "RABBITMQ_PASSWORD",

        (ManagedServiceType.Nats, EnvAliasRole.Url) => "NATS_URL",
        (ManagedServiceType.Nats, EnvAliasRole.Host) => "NATS_HOST",
        (ManagedServiceType.Nats, EnvAliasRole.Port) => "NATS_PORT",
        (ManagedServiceType.Nats, EnvAliasRole.User) => "NATS_USER",
        (ManagedServiceType.Nats, EnvAliasRole.Password) => "NATS_PASSWORD",

        (ManagedServiceType.Meilisearch, EnvAliasRole.Url) => "MEILI_URL",
        (ManagedServiceType.Meilisearch, EnvAliasRole.Host) => "MEILI_HOST",
        (ManagedServiceType.Meilisearch, EnvAliasRole.Port) => "MEILI_PORT",
        // The one secret Meilisearch has: there is no user beside it to make it a "password" pair, but
        // it is what an app that spells it MEILI_API_KEY means.
        (ManagedServiceType.Meilisearch, EnvAliasRole.Password) => "MEILI_MASTER_KEY",

        _ => null
    };

    /// <summary>
    /// The domain's engine for the inspector's own enum — two enums because the inspector runs in the
    /// CLI, which has no domain.
    /// </summary>
    public static ManagedServiceType TypeFor(DetectedServiceKind kind) => kind switch
    {
        DetectedServiceKind.Postgres => ManagedServiceType.PostgreSql,
        DetectedServiceKind.MySql => ManagedServiceType.MySql,
        DetectedServiceKind.MariaDb => ManagedServiceType.MariaDb,
        DetectedServiceKind.Redis => ManagedServiceType.Redis,
        DetectedServiceKind.MongoDb => ManagedServiceType.MongoDb,
        DetectedServiceKind.RabbitMq => ManagedServiceType.RabbitMq,
        DetectedServiceKind.Nats => ManagedServiceType.Nats,
        DetectedServiceKind.Meilisearch => ManagedServiceType.Meilisearch,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "An inspected service with no engine.")
    };
}
