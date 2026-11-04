namespace Harbora.Shared.Inspection;

/// <summary>
/// The exact names an attachment sets for each kind of service. A copy of what
/// <c>ServiceCatalog.All[type].AttachEnv</c> returns, since this project cannot see that one, and
/// public so that <c>SourceInspectorParityTests</c> can hold the two together for every service type:
/// a name added there fails a test here instead of being asked of a customer for nothing.
/// </summary>
public static class AttachEnvNames
{
    private static readonly string[] ConnectionStrings = ["DATABASE_DSN", "ConnectionStrings__DefaultConnection"];

    public static IReadOnlyDictionary<DetectedServiceKind, IReadOnlySet<string>> All { get; } =
        new Dictionary<DetectedServiceKind, IReadOnlySet<string>>
        {
            [DetectedServiceKind.Postgres] = Names(
                ["DATABASE_URL", "PGHOST", "PGPORT", "PGUSER", "PGPASSWORD", "PGDATABASE", .. ConnectionStrings]),
            [DetectedServiceKind.MySql] = Names(
                ["DATABASE_URL", "MYSQL_HOST", "MYSQL_PORT", "MYSQL_USER", "MYSQL_PASSWORD", "MYSQL_DATABASE", .. ConnectionStrings]),
            [DetectedServiceKind.MariaDb] = Names(
                ["DATABASE_URL", "DB_HOST", "DB_PORT", "DB_USER", "DB_PASSWORD", "DB_DATABASE", .. ConnectionStrings]),
            [DetectedServiceKind.Redis] = Names(
                ["REDIS_URL", "REDIS_HOST", "REDIS_PORT", "REDIS_PASSWORD"]),
            [DetectedServiceKind.MongoDb] = Names(
                ["MONGODB_URI", "MONGO_HOST", "MONGO_PORT", "MONGO_USER", "MONGO_PASSWORD", "MONGO_DATABASE"]),
            [DetectedServiceKind.RabbitMq] = Names(
                ["AMQP_URL", "RABBITMQ_URL", "RABBITMQ_HOST", "RABBITMQ_PORT", "RABBITMQ_USER", "RABBITMQ_PASSWORD"]),
            [DetectedServiceKind.Nats] = Names(
                ["NATS_URL", "NATS_HOST", "NATS_PORT", "NATS_USER", "NATS_PASSWORD"]),
            [DetectedServiceKind.Meilisearch] = Names(
                ["MEILI_URL", "MEILI_HOST", "MEILI_PORT", "MEILI_MASTER_KEY"])
        };

    private static HashSet<string> Names(string[] names) => new(names, StringComparer.Ordinal);
}
