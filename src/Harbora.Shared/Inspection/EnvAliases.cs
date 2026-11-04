namespace Harbora.Shared.Inspection;

/// <summary>The other spellings of what an attachment provides, and what each name in an example file says about the app.</summary>
internal static class EnvAliases
{
    private const DetectedServiceKind Postgres = DetectedServiceKind.Postgres;
    private const DetectedServiceKind MySql = DetectedServiceKind.MySql;
    private const DetectedServiceKind MariaDb = DetectedServiceKind.MariaDb;
    private const DetectedServiceKind Redis = DetectedServiceKind.Redis;
    private const DetectedServiceKind MongoDb = DetectedServiceKind.MongoDb;
    private const DetectedServiceKind RabbitMq = DetectedServiceKind.RabbitMq;
    private const DetectedServiceKind Nats = DetectedServiceKind.Nats;
    private const DetectedServiceKind Meilisearch = DetectedServiceKind.Meilisearch;

    /// <summary>Names that can only mean one kind of service.</summary>
    public static readonly IReadOnlyDictionary<string, (DetectedServiceKind Kind, EnvAliasRole Role)> Specific =
        new Dictionary<string, (DetectedServiceKind, EnvAliasRole)>(StringComparer.Ordinal)
        {
            ["MONGO_URI"] = (MongoDb, EnvAliasRole.Url),
            ["MONGO_URL"] = (MongoDb, EnvAliasRole.Url),
            ["MONGODB_URL"] = (MongoDb, EnvAliasRole.Url),
            ["MONGO_DB"] = (MongoDb, EnvAliasRole.Database),
            ["MONGODB_DATABASE"] = (MongoDb, EnvAliasRole.Database),

            ["POSTGRES_URL"] = (Postgres, EnvAliasRole.Url),
            ["POSTGRESQL_URL"] = (Postgres, EnvAliasRole.Url),
            ["PG_URL"] = (Postgres, EnvAliasRole.Url),
            ["POSTGRES_HOST"] = (Postgres, EnvAliasRole.Host),
            ["POSTGRES_PORT"] = (Postgres, EnvAliasRole.Port),
            ["POSTGRES_USER"] = (Postgres, EnvAliasRole.User),
            ["POSTGRES_PASSWORD"] = (Postgres, EnvAliasRole.Password),
            ["POSTGRES_DB"] = (Postgres, EnvAliasRole.Database),

            ["MYSQL_URL"] = (MySql, EnvAliasRole.Url),
            ["MYSQL_DB"] = (MySql, EnvAliasRole.Database),

            ["MARIADB_URL"] = (MariaDb, EnvAliasRole.Url),
            ["MARIADB_HOST"] = (MariaDb, EnvAliasRole.Host),
            ["MARIADB_PORT"] = (MariaDb, EnvAliasRole.Port),
            ["MARIADB_USER"] = (MariaDb, EnvAliasRole.User),
            ["MARIADB_PASSWORD"] = (MariaDb, EnvAliasRole.Password),
            ["MARIADB_DATABASE"] = (MariaDb, EnvAliasRole.Database),

            ["REDIS_URI"] = (Redis, EnvAliasRole.Url),
            ["REDIS_CONNECTION"] = (Redis, EnvAliasRole.Url),
            ["CACHE_URL"] = (Redis, EnvAliasRole.Url),

            ["RABBITMQ_URI"] = (RabbitMq, EnvAliasRole.Url),
            ["CLOUDAMQP_URL"] = (RabbitMq, EnvAliasRole.Url),

            ["NATS_SERVER"] = (Nats, EnvAliasRole.Url),
            ["NATS_SERVERS"] = (Nats, EnvAliasRole.Url),

            // Each of these holds a whole address in the SDKs that read them, not a bare host name.
            ["MEILISEARCH_URL"] = (Meilisearch, EnvAliasRole.Url),
            ["MEILISEARCH_HOST"] = (Meilisearch, EnvAliasRole.Url),
            ["MEILI_HTTP_ADDR"] = (Meilisearch, EnvAliasRole.Url),

            // Without these the screen would ask a person for a key Harbora generated itself.
            ["MEILISEARCH_API_KEY"] = (Meilisearch, EnvAliasRole.Password),
            ["MEILISEARCH_MASTER_KEY"] = (Meilisearch, EnvAliasRole.Password),
            ["MEILI_API_KEY"] = (Meilisearch, EnvAliasRole.Password)
        };

    /// <summary>Names that mean whichever one relational or Mongo database the app has.</summary>
    public static readonly IReadOnlyDictionary<string, EnvAliasRole> Generic =
        new Dictionary<string, EnvAliasRole>(StringComparer.Ordinal)
        {
            ["DB_URL"] = EnvAliasRole.Url,
            ["DB_URI"] = EnvAliasRole.Url,
            ["DB_HOST"] = EnvAliasRole.Host,
            ["DATABASE_HOST"] = EnvAliasRole.Host,
            ["DB_PORT"] = EnvAliasRole.Port,
            ["DATABASE_PORT"] = EnvAliasRole.Port,
            ["DB_USER"] = EnvAliasRole.User,
            ["DB_USERNAME"] = EnvAliasRole.User,
            ["DATABASE_USER"] = EnvAliasRole.User,
            ["DB_PASSWORD"] = EnvAliasRole.Password,
            ["DB_PASS"] = EnvAliasRole.Password,
            ["DATABASE_PASSWORD"] = EnvAliasRole.Password,
            ["DB_NAME"] = EnvAliasRole.Database,
            ["DB_DATABASE"] = EnvAliasRole.Database,
            ["DATABASE_NAME"] = EnvAliasRole.Database
        };

    /// <summary>Names whose value says which engine it is, when it has a scheme.</summary>
    public static readonly IReadOnlySet<string> DatabaseUrlNames =
        new HashSet<string>(StringComparer.Ordinal) { "DATABASE_URL", "DB_URL", "DB_URI" };

    /// <summary>
    /// Named outright as certain whatever they hold: nothing else is called this, and unlike a
    /// <c>DATABASE_URL</c> the kind is in the name, so an example file leaving it blank to fill in is
    /// no weaker a statement than one showing <c>redis://localhost</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> CertainByName =
        new HashSet<string>(StringComparer.Ordinal) { "REDIS_URL", "AMQP_URL", "NATS_URL" };

    /// <summary>Every name that can only belong to one kind: the aliases, and each attach name no other engine shares.</summary>
    private static readonly IReadOnlyDictionary<string, DetectedServiceKind> Implied = BuildImplied();

    private static readonly IReadOnlyDictionary<DetectedServiceKind, string[]> Schemes =
        new Dictionary<DetectedServiceKind, string[]>
        {
            [Postgres] = ["postgres", "postgresql"],
            [MySql] = ["mysql", "mysql2"],
            [MariaDb] = ["mariadb", "mysql"],
            [Redis] = ["redis", "rediss"],
            [MongoDb] = ["mongodb", "mongodb+srv"],
            [RabbitMq] = ["amqp", "amqps"],
            [Nats] = ["nats", "tls"],
            [Meilisearch] = []
        };

    /// <summary>The kind a name points to. A value with some other scheme takes that back: <c>CACHE_URL=memcached://…</c> is not Redis.</summary>
    public static DetectedServiceKind? ImpliedBy(string name, string? scheme)
    {
        if (!Implied.TryGetValue(name, out var kind)) return null;
        if (scheme is null || kind == Meilisearch || Schemes[kind].Contains(scheme)) return kind;
        return null;
    }

    /// <summary>Whether the value's own scheme confirms the kind. Meilisearch has none of its own: it is plain HTTP, which confirms nothing.</summary>
    public static bool SchemeConfirms(DetectedServiceKind kind, string? scheme) =>
        scheme is not null && Schemes[kind].Contains(scheme);

    /// <summary>The scheme of a URL-shaped value, lower case, or null when it is not one.</summary>
    public static string? SchemeOf(string? value)
    {
        if (value is null) return null;
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0) return null;

        var scheme = value[..separator].Trim().ToLowerInvariant();
        return scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.') ? scheme : null;
    }

    private static Dictionary<string, DetectedServiceKind> BuildImplied()
    {
        var implied = Specific.ToDictionary(p => p.Key, p => p.Value.Kind, StringComparer.Ordinal);

        var attachNames = AttachEnvNames.All.SelectMany(p => p.Value.Select(name => (Name: name, Kind: p.Key)));
        foreach (var group in attachNames.GroupBy(n => n.Name, StringComparer.Ordinal))
        {
            // DATABASE_URL belongs to three engines and DB_HOST means any of them, even though only
            // MariaDB's attachment happens to spell it that way. Neither says which engine this is.
            if (group.Count() == 1 && !Generic.ContainsKey(group.Key)) implied[group.Key] = group.First().Kind;
        }

        return implied;
    }
}
