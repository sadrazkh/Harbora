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

    private const string NoProbeYet =
        "Harbora has no way to test this kind of service's connection yet.";

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

        // Both versions on offer carry mongosh: the old `mongo` shell left the image at 6.0, which is
        // what used to make this engine untestable. listDatabases, because ping answers anybody: with
        // the root user the image creates, the server runs with access control, and this is refused
        // unless the login was accepted. The address goes in through the environment, as every other
        // password here does. directConnection, so that a name the server gives itself is not
        // followed somewhere else, and five seconds rather than the driver's thirty for a server that
        // is not there.
        ManagedServiceType.MongoDb => new ProbePlan(
            ["sh", "-c",
             "mongosh --quiet --norc \"$HARBORA_PROBE_URI\" --eval " +
             "'const r = db.adminCommand({ listDatabases: 1, nameOnly: true }); " +
             "print(\"MongoDB answered: ok=\" + r.ok); if (r.ok !== 1) quit(1);'"],
            new Dictionary<string, string>
            {
                ["HARBORA_PROBE_URI"] =
                    $"mongodb://{Uri.EscapeDataString(creds.User)}:{Uri.EscapeDataString(creds.Password)}@{creds.Host}:{creds.Port}" +
                    "/admin?authSource=admin&directConnection=true&serverSelectionTimeoutMS=5000&connectTimeoutMS=5000"
            }),

        // A broker has no shell client that logs in over its own protocol: rabbitmqctl talks to the
        // node over Erlang distribution, which proves nothing about what an app can do on port 5672.
        // But every RabbitMQ image is an Erlang runtime, so the start of a real AMQP 0-9-1 connection
        // is spoken from there: the protocol header, the broker's Start, a Start-Ok with PLAIN
        // credentials, and then either Tune, which a broker only sends to a login it accepted, or a
        // closed socket. The program travels in the environment so that no shell reads its quotes.
        ManagedServiceType.RabbitMq => new ProbePlan(
            ["sh", "-c", "erl -noshell -noinput -eval \"$HARBORA_PROBE_PROGRAM\""],
            new Dictionary<string, string>
            {
                ["HARBORA_PROBE_PROGRAM"] = AmqpLogin,
                ["HARBORA_PROBE_HOST"] = creds.Host,
                ["HARBORA_PROBE_PORT"] = creds.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["HARBORA_PROBE_USER"] = creds.User,
                ["HARBORA_PROBE_PASS"] = creds.Password
            }),

        // NATS speaks lines of text, so the image's own shell can hold the conversation: CONNECT with
        // the user and password, then PING. A server that accepted the login answers PONG, and one
        // that did not answers -ERR 'Authorization Violation' and hangs up. The pause keeps our end
        // open until the answer has arrived; the server's INFO line is left out of what is shown.
        ManagedServiceType.Nats => new ProbePlan(
            ["sh", "-c",
             "out=$({ printf 'CONNECT %s\\r\\nPING\\r\\n' \"$HARBORA_PROBE_CONNECT\"; sleep 2; } " +
             $"| nc -w 5 {Shell(creds.Host)} {creds.Port} 2>&1); " +
             "said=$(printf '%s\\n' \"$out\" | tr -d '\\r' | grep -v '^INFO '); " +
             "printf '%s\\n' \"$said\"; " +
             "if printf '%s\\n' \"$said\" | grep -qx 'PONG'; then exit 0; fi; " +
             "echo 'NATS did not answer PING.'; exit 1"],
            new Dictionary<string, string>
            {
                ["HARBORA_PROBE_CONNECT"] = System.Text.Json.JsonSerializer.Serialize(new
                {
                    verbose = false, pedantic = false, user = creds.User, pass = creds.Password,
                    name = "harbora-probe", lang = "sh", version = "1", protocol = 0
                })
            }),

        _ => null
    };

    /// <summary>
    /// The first half of an AMQP 0-9-1 connection, as far as the broker's verdict on the login. A frame
    /// is a type, a channel, a length, the payload and 0xCE; Start-Ok is class 10 method 11 with an
    /// empty table of client properties, the mechanism, the response and a locale. Without the
    /// <c>authentication_failure_close</c> capability, which this does not claim, a broker that refuses
    /// a login closes the socket rather than explaining, so both endings are read as a refusal.
    /// </summary>
    private const string AmqpLogin =
        """
        Out = fun(Text, Code) -> io:format("~ts~n", [Text]), halt(Code) end,
        Host = os:getenv("HARBORA_PROBE_HOST"),
        Port = list_to_integer(os:getenv("HARBORA_PROBE_PORT")),
        User = unicode:characters_to_binary(os:getenv("HARBORA_PROBE_USER")),
        Pass = unicode:characters_to_binary(os:getenv("HARBORA_PROBE_PASS")),
        case gen_tcp:connect(Host, Port, [binary, {active, false}], 5000) of
            {error, Why} -> Out(io_lib:format("AMQP connect failed: ~p", [Why]), 1);
            {ok, S} ->
                ok = gen_tcp:send(S, <<"AMQP", 0, 0, 9, 1>>),
                case gen_tcp:recv(S, 7, 10000) of
                    {ok, <<1, 0:16, Len:32>>} ->
                        {ok, _} = gen_tcp:recv(S, Len + 1, 10000),
                        Resp = <<0, User/binary, 0, Pass/binary>>,
                        Body = <<10:16, 11:16, 0:32, 5, "PLAIN", (byte_size(Resp)):32, Resp/binary, 5, "en_US">>,
                        ok = gen_tcp:send(S, <<1, 0:16, (byte_size(Body)):32, Body/binary, 206>>),
                        case gen_tcp:recv(S, 11, 10000) of
                            {ok, <<1, 0:16, _:32, 10:16, 30:16>>} -> Out("AMQP login accepted", 0);
                            {ok, <<1, 0:16, _:32, 10:16, 50:16>>} -> Out("AMQP login refused by the broker", 1);
                            {error, closed} -> Out("AMQP login refused by the broker, which closed the connection", 1);
                            Other -> Out(io_lib:format("AMQP gave an unexpected answer: ~p", [Other]), 1)
                        end;
                    Greeting -> Out(io_lib:format("Not an AMQP greeting: ~p", [Greeting]), 1)
                end
        end.
        """;

    private static readonly ServiceCreds NobodysCreds = new("", 0, "", "", "");

    /// <summary>
    /// Why an engine cannot be probed, for the screen that would otherwise show nothing. Every engine in
    /// the catalogue can be asked today: MongoDB and the two brokers were the last that could not. This
    /// stays for the next engine that arrives without a probe, which must read as "not tested" and
    /// never as a pass, so it is answered from <see cref="For"/> itself rather than from a second list.
    /// </summary>
    public static string? WhyUnsupported(ManagedServiceType type) =>
        For(type, NobodysCreds) is null ? NoProbeYet : null;

    /// <summary>
    /// What to tell someone when the probe fails. The exit code alone means nothing to them, and the
    /// client's own message is usually about a symptom rather than the cause.
    /// </summary>
    public static string Explain(ManagedServiceType type, string? output)
    {
        var text = output ?? "";

        // The last lines of each group are how mongosh (Node's resolver and driver), Erlang and
        // busybox nc say the same things.
        if (text.Contains("could not translate host name", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Unknown MySQL server host", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ENOTFOUND", StringComparison.Ordinal)
            || text.Contains("EAI_AGAIN", StringComparison.Ordinal)
            || text.Contains("nxdomain", StringComparison.Ordinal)
            || text.Contains("bad address", StringComparison.OrdinalIgnoreCase))
            return NameDoesNotResolve;

        if (text.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WRONGPASS", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("AMQP login refused", StringComparison.Ordinal)
            || text.Contains("Authorization Violation", StringComparison.OrdinalIgnoreCase))
            return PasswordRefused;

        if (text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase))
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
        NoProbeYet => "هاربورا هنوز راهی برای آزمایش اتصال این نوع سرویس ندارد.",
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
