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
                AttachEnv = c => new()
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
                AttachEnv = c => new()
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
                AttachEnv = c => new()
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
            [ManagedServiceType.MongoDb] = new()
            {
                Type = ManagedServiceType.MongoDb, DisplayName = "MongoDB", DisplayNameFa = "MongoDB",
                ImageRepo = "mongo", Versions = ["7", "6"], Port = 27017, DataMountPath = "/data/db",
                Env = c => new() { ["MONGO_INITDB_ROOT_USERNAME"] = c.User, ["MONGO_INITDB_ROOT_PASSWORD"] = c.Password },
                Conn = c => ($"mongodb://{c.User}:{c.Password}@{c.Host}:{c.Port}/{c.Database}?authSource=admin",
                             $"mongodb://{c.User}:{Mask(c.Password)}@{c.Host}:{c.Port}/{c.Database}?authSource=admin"),
                AttachEnv = c => new()
                {
                    ["MONGODB_URI"] = $"mongodb://{c.User}:{c.Password}@{c.Host}:{c.Port}/{c.Database}?authSource=admin"
                }
            },
        };
}
