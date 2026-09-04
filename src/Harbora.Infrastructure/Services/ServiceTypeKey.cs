using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// The catalogue key a managed service's mark is drawn from.
///
/// It is a rule rather than a local function because it was written twice — once on the database
/// list and once on the database page — and a service whose mark is right in the list and generic
/// on its own page looks like two different things. The default matters most: an engine added to
/// the catalogue and forgotten here falls back to a plain database mark, which is wrong for a
/// message broker and silently so.
/// </summary>
public static class ServiceTypeKey
{
    /// <summary>The key under <c>wwwroot/img/apps</c>, or the generic mark when there is none.</summary>
    public static string For(ManagedServiceType type) => type switch
    {
        ManagedServiceType.PostgreSql => "postgres",
        ManagedServiceType.MySql => "mysql",
        ManagedServiceType.MariaDb => "mariadb",
        ManagedServiceType.Redis => "redis",
        ManagedServiceType.MongoDb => "mongodb",
        ManagedServiceType.RabbitMq => "rabbitmq",
        ManagedServiceType.Nats => "nats",
        ManagedServiceType.Meilisearch => "meilisearch",
        _ => "database"
    };

    /// <summary>
    /// Whether this engine is a message broker rather than a store.
    ///
    /// Asked by the screens that talk about data — backups, dumps, storage — because the answer for
    /// a broker is "there is nothing here to back up", and offering those controls anyway is the
    /// dead-button pattern this codebase keeps removing.
    /// </summary>
    public static bool IsBroker(ManagedServiceType type) =>
        type is ManagedServiceType.RabbitMq or ManagedServiceType.Nats;
}
