using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Services;

/// <summary>Credentials + address used to template env vars and connection strings.</summary>
public sealed record ServiceCreds(string Host, int Port, string User, string Password, string Database);

/// <summary>
/// One backing-service type described as data: image, port, data path, and functions that
/// build the container env, optional command args, and connection strings. Adding a new
/// service type is a new entry here — no changes to the engine.
/// </summary>
public sealed class ServiceDefinition
{
    public required ManagedServiceType Type { get; init; }
    public required string DisplayName { get; init; }
    public required string DisplayNameFa { get; init; }
    public required string ImageRepo { get; init; }
    public required string[] Versions { get; init; }
    public required int Port { get; init; }
    public required string DataMountPath { get; init; }
    public bool HasDatabaseName { get; init; } = true;

    /// <summary>Container environment that seeds credentials on first boot.</summary>
    public required Func<ServiceCreds, Dictionary<string, string>> Env { get; init; }

    /// <summary>Optional container command (e.g. Redis `--requirepass`).</summary>
    public Func<ServiceCreds, IReadOnlyList<string>?> Command { get; init; } = _ => null;

    /// <summary>Full + masked connection strings for the details screen.</summary>
    public required Func<ServiceCreds, (string Full, string Masked)> Conn { get; init; }

    /// <summary>Env vars injected into an app when this service is attached.</summary>
    public required Func<ServiceCreds, Dictionary<string, string>> AttachEnv { get; init; }
}

public static class ServiceCatalog
{
    private static string Mask(string s) => "••••••";

    /// <summary>
    /// The connection string in the shape an ADO.NET provider parses, under the two names a
    /// framework finds on its own.
    ///
    /// <c>DATABASE_URL</c> is a URI, and every ADO.NET provider — Npgsql, MySqlConnector,
    /// SqlClient — parses only <c>keyword=value</c>. So an attach used to hand a .NET application
    /// five variables it does not read and none it does: it fell back to appsettings.json, which in
    /// a container says <c>Host=localhost</c>, and died at the health check with a stack trace that
    /// named neither the database nor the attach.
    ///
    /// <c>ConnectionStrings__DefaultConnection</c> is not a convention Harbora invented: .NET's
    /// configuration reads environment variables and maps <c>__</c> to <c>:</c>, so this overrides
    /// <c>ConnectionStrings:DefaultConnection</c> with no code change, no rebuild, and no secret
    /// written into a file. An application whose key is spelled differently reads
    /// <c>DATABASE_DSN</c> instead, or copies it under its own name — which is what the app page
    /// now says out loud.
    /// </summary>
    private static Dictionary<string, string> Dsn(string value) => new()
    {
        ["DATABASE_DSN"] = value,
        ["ConnectionStrings__DefaultConnection"] = value
    };

    /// <summary>Npgsql spelling. Host/Username — not Server/User ID, which it rejects.</summary>
    private static Dictionary<string, string> PostgresDsn(ServiceCreds c) =>
        Dsn($"Host={c.Host};Port={c.Port};Database={c.Database};Username={c.User};Password={c.Password}");

    /// <summary>MySqlConnector spelling, shared by MySQL and MariaDB.</summary>
    private static Dictionary<string, string> MySqlDsn(ServiceCreds c) =>
        Dsn($"Server={c.Host};Port={c.Port};Database={c.Database};User ID={c.User};Password={c.Password}");

    public static readonly IReadOnlyDictionary<ManagedServiceType, ServiceDefinition> All =
        new Dictionary<ManagedServiceType, ServiceDefinition>
        {
            [ManagedServiceType.PostgreSql] = new()
            {
                Type = ManagedServiceType.PostgreSql, DisplayName = "PostgreSQL", DisplayNameFa = "PostgreSQL",
                ImageRepo = "postgres", Versions = ["16-alpine", "15-alpine"], Port = 5432,
                DataMountPath = "/var/lib/postgresql/data",
                Env = c => new() { ["POSTGRES_USER"] = c.User, ["POSTGRES_PASSWORD"] = c.Password, ["POSTGRES_DB"] = c.Database },
                Conn = c => ($"postgresql://{c.User}:{c.Password}@{c.Host}:{c.Port}/{c.Database}",
                             $"postgresql://{c.User}:{Mask(c.Password)}@{c.Host}:{c.Port}/{c.Database}"),
                AttachEnv = c => new(PostgresDsn(c))
                {
                    ["DATABASE_URL"] = $"postgresql://{c.User}:{c.Password}@{c.Host}:{c.Port}/{c.Database}",
                    ["PGHOST"] = c.Host, ["PGPORT"] = c.Port.ToString(), ["PGUSER"] = c.User,
                    ["PGPASSWORD"] = c.Password, ["PGDATABASE"] = c.Database
                }
            },
            [ManagedServiceType.MySql] = new()
            {
                Type = ManagedServiceType.MySql, DisplayName = "MySQL", DisplayNameFa = "MySQL",
                ImageRepo = "mysql", Versions = ["8.4", "8.0"], Port = 3306, DataMountPath = "/var/lib/mysql",
                Env = c => new()
                {
                    ["MYSQL_ROOT_PASSWORD"] = c.Password, ["MYSQL_DATABASE"] = c.Database,
                    ["MYSQL_USER"] = c.User, ["MYSQL_PASSWORD"] = c.Password
                },
                Conn = c => ($"mysql://{c.User}:{c.Password}@{c.Host}:{c.Port}/{c.Database}",
                             $"mysql://{c.User}:{Mask(c.Password)}@{c.Host}:{c.Port}/{c.Database}"),
                AttachEnv = c => new(MySqlDsn(c))
                {
                    ["DATABASE_URL"] = $"mysql://{c.User}:{c.Password}@{c.Host}:{c.Port}/{c.Database}",
                    ["MYSQL_HOST"] = c.Host, ["MYSQL_PORT"] = c.Port.ToString(),
                    ["MYSQL_USER"] = c.User, ["MYSQL_PASSWORD"] = c.Password, ["MYSQL_DATABASE"] = c.Database
                }
            },
            [ManagedServiceType.MariaDb] = new()
            {
                Type = ManagedServiceType.MariaDb, DisplayName = "MariaDB", DisplayNameFa = "MariaDB",
                ImageRepo = "mariadb", Versions = ["11", "10.11"], Port = 3306, DataMountPath = "/var/lib/mysql",
                Env = c => new()
                {
                    ["MARIADB_ROOT_PASSWORD"] = c.Password, ["MARIADB_DATABASE"] = c.Database,
                    ["MARIADB_USER"] = c.User, ["MARIADB_PASSWORD"] = c.Password
                },
                Conn = c => ($"mysql://{c.User}:{c.Password}@{c.Host}:{c.Port}/{c.Database}",
                             $"mysql://{c.User}:{Mask(c.Password)}@{c.Host}:{c.Port}/{c.Database}"),
                AttachEnv = c => new(MySqlDsn(c))
                {
                    ["DATABASE_URL"] = $"mysql://{c.User}:{c.Password}@{c.Host}:{c.Port}/{c.Database}",
                    ["DB_HOST"] = c.Host, ["DB_PORT"] = c.Port.ToString(),
                    ["DB_USER"] = c.User, ["DB_PASSWORD"] = c.Password, ["DB_DATABASE"] = c.Database
                }
            },
            [ManagedServiceType.Redis] = new()
            {
                Type = ManagedServiceType.Redis, DisplayName = "Redis", DisplayNameFa = "Redis",
                ImageRepo = "redis", Versions = ["7-alpine", "6-alpine"], Port = 6379, DataMountPath = "/data",
                HasDatabaseName = false,
                Env = _ => new(),
                Command = c => ["redis-server", "--requirepass", c.Password, "--appendonly", "yes"],
                Conn = c => ($"redis://:{c.Password}@{c.Host}:{c.Port}",
                             $"redis://:{Mask(c.Password)}@{c.Host}:{c.Port}"),
                AttachEnv = c => new()
                {
                    ["REDIS_URL"] = $"redis://:{c.Password}@{c.Host}:{c.Port}",
                    ["REDIS_HOST"] = c.Host, ["REDIS_PORT"] = c.Port.ToString(), ["REDIS_PASSWORD"] = c.Password
                }
            },
            [ManagedServiceType.RabbitMq] = new()
            {
                Type = ManagedServiceType.RabbitMq, DisplayName = "RabbitMQ", DisplayNameFa = "RabbitMQ",
                // The management image, not the plain one: a broker whose queues cannot be looked at
                // is a broker nobody can debug, and the difference is one tag.
                ImageRepo = "rabbitmq", Versions = ["4-management-alpine", "3.13-management-alpine"],
                Port = 5672, DataMountPath = "/var/lib/rabbitmq", HasDatabaseName = false,
                Env = c => new()
                {
                    ["RABBITMQ_DEFAULT_USER"] = c.User,
                    ["RABBITMQ_DEFAULT_PASS"] = c.Password
                },
                Conn = c => ($"amqp://{c.User}:{c.Password}@{c.Host}:{c.Port}/",
                             $"amqp://{c.User}:{Mask(c.Password)}@{c.Host}:{c.Port}/"),
                AttachEnv = c => new()
                {
                    // Both spellings: the .NET and Java clients read AMQP_URL, most Node and Python
                    // libraries read RABBITMQ_URL, and an app that gets only the other one fails at
                    // startup with a message about a missing variable rather than about a broker.
                    ["AMQP_URL"] = $"amqp://{c.User}:{c.Password}@{c.Host}:{c.Port}/",
                    ["RABBITMQ_URL"] = $"amqp://{c.User}:{c.Password}@{c.Host}:{c.Port}/",
                    ["RABBITMQ_HOST"] = c.Host, ["RABBITMQ_PORT"] = c.Port.ToString(),
                    ["RABBITMQ_USER"] = c.User, ["RABBITMQ_PASSWORD"] = c.Password
                }
            },
            [ManagedServiceType.Nats] = new()
            {
                Type = ManagedServiceType.Nats, DisplayName = "NATS", DisplayNameFa = "NATS",
                ImageRepo = "nats", Versions = ["2.10-alpine", "2.9-alpine"],
                Port = 4222, DataMountPath = "/data", HasDatabaseName = false,
                Env = _ => new(),
                // NATS takes its credentials on the command line rather than from the environment,
                // and JetStream is off unless asked for — a broker that loses every message on
                // restart is not what somebody adding one to an environment expects.
                Command = c => ["--jetstream", "--store_dir", "/data", "--user", c.User, "--pass", c.Password],
                Conn = c => ($"nats://{c.User}:{c.Password}@{c.Host}:{c.Port}",
                             $"nats://{c.User}:{Mask(c.Password)}@{c.Host}:{c.Port}"),
                AttachEnv = c => new()
                {
                    ["NATS_URL"] = $"nats://{c.User}:{c.Password}@{c.Host}:{c.Port}",
                    ["NATS_HOST"] = c.Host, ["NATS_PORT"] = c.Port.ToString(),
                    ["NATS_USER"] = c.User, ["NATS_PASSWORD"] = c.Password
                }
            },
            [ManagedServiceType.MongoDb] = new()
            {
                Type = ManagedServiceType.MongoDb, DisplayName = "MongoDB", DisplayNameFa = "MongoDB",
                ImageRepo = "mongo", Versions = ["7", "6"], Port = 27017, DataMountPath = "/data/db",
                Env = c => new() { ["MONGO_INITDB_ROOT_USERNAME"] = c.User, ["MONGO_INITDB_ROOT_PASSWORD"] = c.Password },
                Conn = c => ($"mongodb://{c.User}:{c.Password}@{c.Host}:{c.Port}/{c.Database}?authSource=admin",
                             $"mongodb://{c.User}:{Mask(c.Password)}@{c.Host}:{c.Port}/{c.Database}?authSource=admin"),
                // C1 (2026-08-22 config-delivery plan): discrete parts added alongside the URI that
                // was already here — the same gap AttachConnectionStringTests documents Postgres and
                // MySQL as having closed already (MONGO_HOST/PORT/USER/PASSWORD/DATABASE, mirroring
                // PGHOST/PGPORT/PGUSER/PGPASSWORD/PGDATABASE's naming), for whatever driver or script
                // wants them instead of parsing a URI.
                AttachEnv = c => new()
                {
                    ["MONGODB_URI"] = $"mongodb://{c.User}:{c.Password}@{c.Host}:{c.Port}/{c.Database}?authSource=admin",
                    ["MONGO_HOST"] = c.Host, ["MONGO_PORT"] = c.Port.ToString(),
                    ["MONGO_USER"] = c.User, ["MONGO_PASSWORD"] = c.Password, ["MONGO_DATABASE"] = c.Database
                }
            },
        };
}
