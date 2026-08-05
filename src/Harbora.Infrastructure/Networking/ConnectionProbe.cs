using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;

namespace Harbora.Infrastructure.Networking;

/// <summary>A command that tries to actually use a database, and the environment it needs.</summary>
public sealed record ProbePlan(IReadOnlyList<string> Command, IReadOnlyDictionary<string, string> Env);

/// <summary>
/// Whether a service can really reach its database.
///
/// Everything else on the screen is configuration: a hostname that is written down, a password that
/// is stored. Those can all be right while the connection still fails — the wrong network, a
/// container that never came back after a restart, a password rotated in one place and not the
/// other. The only answer worth showing is the one obtained by connecting.
///
/// So the probe authenticates rather than opening a socket. A port that accepts TCP proves nothing
/// about credentials, and wrong credentials are the common failure.
/// </summary>
public static class ConnectionProbe
{
    public static ProbePlan? For(ManagedServiceType type, ServiceCreds creds) => type switch
    {
        ManagedServiceType.PostgreSql => new ProbePlan(
            ["sh", "-c",
             $"psql -h {Shell(creds.Host)} -p {creds.Port} -U {Shell(creds.User)} " +
             $"-d {Shell(creds.Database)} -v ON_ERROR_STOP=1 -c 'SELECT 1'"],
            new Dictionary<string, string> { ["PGPASSWORD"] = creds.Password }),

        ManagedServiceType.MySql or ManagedServiceType.MariaDb => new ProbePlan(
            ["sh", "-c",
             $"mysql -h {Shell(creds.Host)} -P {creds.Port} -u {Shell(creds.User)} " +
             $"{Shell(creds.Database)} -e 'SELECT 1'"],
            new Dictionary<string, string> { ["MYSQL_PWD"] = creds.Password }),

        ManagedServiceType.Redis => new ProbePlan(
            // The password goes in through the environment redis-cli reads, not as an argument:
            // -a warns loudly and puts it on the command line.
            ["sh", "-c", $"redis-cli -h {Shell(creds.Host)} -p {creds.Port} PING"],
            new Dictionary<string, string> { ["REDISCLI_AUTH"] = creds.Password }),

        _ => null
    };

    /// <summary>Why an engine cannot be probed, for the screen that would otherwise show nothing.</summary>
    public static string? WhyUnsupported(ManagedServiceType type) => type switch
    {
        ManagedServiceType.MongoDb =>
            "MongoDB's shell changed name between the versions Harbora offers, so its connection is " +
            "not tested automatically yet.",

        // A broker's clients speak AMQP and NATS, not a shell. Probing one means opening a real
        // protocol connection, which is a client library rather than a command — said plainly here
        // rather than left as a button that returns nothing.
        ManagedServiceType.RabbitMq or ManagedServiceType.Nats =>
            "A message broker speaks its own protocol rather than answering a shell client, so " +
            "Harbora does not test its connection automatically.",
        _ => null
    };

    /// <summary>
    /// What to tell someone when the probe fails. The exit code alone means nothing to them, and the
    /// client's own message is usually about a symptom rather than the cause.
    /// </summary>
    public static string Explain(ManagedServiceType type, string? output)
    {
        var text = output ?? "";

        if (text.Contains("could not translate host name", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Unknown MySQL server host", StringComparison.OrdinalIgnoreCase))
            return "The database's name does not resolve from this service, which means they are not " +
                   "on the same private network. Redeploy the service.";

        if (text.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WRONGPASS", StringComparison.OrdinalIgnoreCase))
            return "The database refused the password. If it was rotated, redeploy the services that " +
                   "use it so they pick up the new one.";

        if (text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            return "Nothing is listening on that address. The database container is most likely stopped.";

        return $"The connection failed. {text}".Trim();
    }

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
