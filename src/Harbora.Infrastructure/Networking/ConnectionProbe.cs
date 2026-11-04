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
    /// <summary>What <c>TestConnectionAsync</c> answers for a row it cannot find.</summary>
    public const string ServiceGone = "That database no longer exists.";

    private const string MongoShellChanged =
        "MongoDB's shell changed name between the versions Harbora offers, so its connection is " +
        "not tested automatically yet.";

    private const string BrokerHasNoShell =
        "A message broker speaks its own protocol rather than answering a shell client, so " +
        "Harbora does not test its connection automatically.";

    private const string NameDoesNotResolve =
        "The database's name does not resolve from this service, which means they are not " +
        "on the same private network. Redeploy the service.";

    private const string PasswordRefused =
        "The database refused the password. If it was rotated, redeploy the services that " +
        "use it so they pick up the new one.";

    private const string NothingListening =
        "Nothing is listening on that address. The database container is most likely stopped.";

    private const string ConnectionFailedPrefix = "The connection failed.";

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

        // Meilisearch's own /health needs no key at all, so hitting it would prove only that the
        // container is listening — not the thing this probe exists to catch ("wrong credentials are
        // the common failure"). /keys does authenticate: its own docs say plainly "you must have the
        // master key ... to access the keys route", so a 200 there proves the STORED key is the key
        // the server will actually accept. The image carries curl (verified against the real
        // Dockerfile — see ServiceCatalog's own entry), so this runs the same way every other probe
        // here does: inside a one-off container of the service's own image, key never on argv.
        ManagedServiceType.Meilisearch => new ProbePlan(
            ["sh", "-c",
             $"code=$(curl -s -m 5 -o /dev/null -w '%{{http_code}}' " +
             $"-H \"Authorization: Bearer $MEILI_PROBE_KEY\" {Shell($"http://{creds.Host}:{creds.Port}/keys")}); " +
             "echo \"HTTP $code\"; [ \"$code\" = \"200\" ]"],
            new Dictionary<string, string> { ["MEILI_PROBE_KEY"] = creds.Password }),

        _ => null
    };

    /// <summary>Why an engine cannot be probed, for the screen that would otherwise show nothing.</summary>
    public static string? WhyUnsupported(ManagedServiceType type) => type switch
    {
        ManagedServiceType.MongoDb => MongoShellChanged,

        // A broker's clients speak AMQP and NATS, not a shell. Probing one means opening a real
        // protocol connection, which is a client library rather than a command — said plainly here
        // rather than left as a button that returns nothing.
        ManagedServiceType.RabbitMq or ManagedServiceType.Nats => BrokerHasNoShell,
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
            return NameDoesNotResolve;

        if (text.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WRONGPASS", StringComparison.OrdinalIgnoreCase))
            return PasswordRefused;

        if (text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            return NothingListening;

        // The Meilisearch probe above reports its own outcome as "HTTP $code" rather than through a
        // client's own wording, since curl -f discards the response body a real message would come
        // from. 000 is curl's own code for "no HTTP response arrived at all" (DNS failure, refused
        // connection, timeout) — not a status Meilisearch itself ever sends.
        if (text.Contains("HTTP 401", StringComparison.Ordinal))
            return PasswordRefused;
        if (text.Contains("HTTP 000", StringComparison.Ordinal))
            return NothingListening;

        return $"{ConnectionFailedPrefix} {text}".Trim();
    }

    /// <summary>
    /// The Persian of a sentence <see cref="WhyUnsupported"/> or <see cref="Explain"/> wrote.
    ///
    /// <para>
    /// A lookup over the sentence, not a second return value, because
    /// <c>IManagedServiceEngine.TestConnectionAsync</c> hands back one string and by then the reason it
    /// was chosen is gone. It lives here, beside the English, so the two are changed together. Text
    /// nobody wrote here (a client's own output, after the prefix) stays as it arrived: inventing a
    /// translation of a message we did not write would be worse than showing it.
    /// </para>
    /// </summary>
    public static string InPersian(string sentence) => sentence switch
    {
        MongoShellChanged => "پوستهٔ MongoDB بین نسخه‌هایی که هاربورا ارائه می‌دهد نامش عوض شده، پس اتصالش هنوز خودکار آزمایش نمی‌شود.",
        BrokerHasNoShell => "صف پیام با پروتکل خودش حرف می‌زند و به کلاینت خط فرمان جواب نمی‌دهد، پس هاربورا اتصالش را خودکار آزمایش نمی‌کند.",
        NameDoesNotResolve => "نام دیتابیس از اینجا پیدا نمی‌شود، یعنی این دو روی یک شبکهٔ خصوصی نیستند. سرویس را دوباره مستقر کن.",
        PasswordRefused => "دیتابیس رمز را نپذیرفت. اگر رمز عوض شده، سرویس‌هایی را که از آن استفاده می‌کنند دوباره مستقر کن تا رمز جدید را بگیرند.",
        NothingListening => "چیزی روی این آدرس گوش نمی‌دهد. به احتمال زیاد کانتینر دیتابیس خاموش است.",
        ServiceGone => "این دیتابیس دیگر وجود ندارد.",
        _ when sentence.StartsWith(ConnectionFailedPrefix, StringComparison.Ordinal) =>
            $"اتصال برقرار نشد.{sentence[ConnectionFailedPrefix.Length..]}",
        _ => sentence
    };

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
