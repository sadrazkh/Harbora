namespace Harbora.Shared.Inspection;

/// <summary>Which libraries mean which service. A row per service, a column per language; read it as a table.</summary>
internal static class ServiceTable
{
    private sealed record Row(
        DetectedServiceKind Kind,
        string[] Node,
        string[] Python,
        string[] DotNet,
        string[] Go,
        string[] Php);

    // Keys are lower case, in the form each language's Key is held in. A trailing * in the .NET column
    // is a prefix, because every Npgsql integration package names the driver first. The Go column is
    // matched by path segment, since the host is usually there and a /v5 usually follows.
    private static readonly Row[] Rows =
    [
        new(DetectedServiceKind.Postgres,
            ["pg", "postgres", "pg-promise"],
            ["psycopg2", "psycopg2-binary", "psycopg", "asyncpg"],
            ["npgsql*"],
            ["lib/pq", "jackc/pgx"],
            ["ext-pgsql"]),
        new(DetectedServiceKind.MySql,
            ["mysql", "mysql2"],
            ["mysqlclient", "pymysql", "aiomysql", "mysql-connector-python"],
            ["mysqlconnector", "mysql.data", "pomelo.entityframeworkcore.mysql"],
            ["go-sql-driver/mysql"],
            ["ext-mysqli", "ext-pdo_mysql"]),
        new(DetectedServiceKind.MariaDb,
            ["mariadb"],
            ["mariadb"],
            [],
            [],
            []),
        new(DetectedServiceKind.Redis,
            ["redis", "ioredis", "bull", "bullmq"],
            ["redis", "aioredis", "rq"],
            ["stackexchange.redis"],
            ["redis/go-redis", "gomodule/redigo"],
            ["predis/predis"]),
        new(DetectedServiceKind.MongoDb,
            ["mongoose", "mongodb"],
            ["pymongo", "motor", "mongoengine", "beanie"],
            ["mongodb.driver"],
            ["go.mongodb.org/mongo-driver"],
            ["mongodb/mongodb"]),
        new(DetectedServiceKind.RabbitMq,
            ["amqplib", "amqp-connection-manager"],
            ["pika", "aio-pika"],
            ["rabbitmq.client", "masstransit.rabbitmq"],
            ["rabbitmq/amqp091-go", "streadway/amqp"],
            ["php-amqplib/php-amqplib"]),
        new(DetectedServiceKind.Nats,
            ["nats"],
            ["nats-py"],
            ["nats.client", "nats.net"],
            ["nats-io/nats.go"],
            []),
        new(DetectedServiceKind.Meilisearch,
            ["meilisearch"],
            ["meilisearch"],
            ["meilisearch"],
            ["meilisearch/meilisearch-go"],
            ["meilisearch/meilisearch-php"])
    ];

    private static readonly HashSet<string> SqliteNode = new(StringComparer.Ordinal) { "sqlite3", "better-sqlite3" };
    private static readonly HashSet<string> SqliteDotNet = new(StringComparer.Ordinal) { "microsoft.entityframeworkcore.sqlite" };
    private static readonly HashSet<string> SqlServerNode = new(StringComparer.Ordinal) { "mssql" };
    private static readonly HashSet<string> SqlServerDotNet = new(StringComparer.Ordinal)
    {
        "microsoft.entityframeworkcore.sqlserver", "microsoft.data.sqlclient"
    };

    /// <summary>The services this library means, which for all but a few names is none.</summary>
    public static IEnumerable<DetectedServiceKind> KindsFor(Dependency dependency) =>
        Rows.Where(row => dependency.Ecosystem switch
            {
                Ecosystem.Node => row.Node.Contains(dependency.Key),
                Ecosystem.Python => row.Python.Contains(dependency.Key),
                Ecosystem.DotNet => row.DotNet.Any(p => MatchesDotNet(dependency.Key, p)),
                Ecosystem.Go => row.Go.Any(p => MatchesGo(dependency.Key, p)),
                Ecosystem.Php => row.Php.Contains(dependency.Key),
                _ => false
            })
            .Select(row => row.Kind);

    public static bool UsesSqlite(ProjectFacts facts) =>
        facts.HasRuntime(Ecosystem.Node, SqliteNode) || facts.HasRuntime(Ecosystem.DotNet, SqliteDotNet);

    public static bool UsesSqlServer(ProjectFacts facts) =>
        facts.HasRuntime(Ecosystem.Node, SqlServerNode) || facts.HasRuntime(Ecosystem.DotNet, SqlServerDotNet);

    private static bool MatchesDotNet(string key, string pattern) =>
        pattern.EndsWith('*') ? key.StartsWith(pattern[..^1], StringComparison.Ordinal) : key == pattern;

    // By whole segments, so github.com/not-lib/pq does not read as lib/pq.
    private static bool MatchesGo(string module, string path) =>
        module == path ||
        module.StartsWith(path + "/", StringComparison.Ordinal) ||
        module.EndsWith("/" + path, StringComparison.Ordinal) ||
        module.Contains("/" + path + "/", StringComparison.Ordinal);
}
