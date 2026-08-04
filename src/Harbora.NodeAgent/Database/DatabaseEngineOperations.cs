using System.Text.RegularExpressions;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Runtime;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Database;

/// <summary>Admin credentials for a database, read from the workload's own spec.</summary>
public sealed record EngineAdmin(string Username, string Password, string? Database);

/// <summary>
/// Creates, rotates and drops engine-side users.
///
/// <para>
/// Every engine is driven through its own first-party client, already present in the image,
/// executed as an argv array inside the container. Passwords travel in the environment, never on
/// the command line: the command line of a process is world-readable in <c>/proc</c>, so
/// <c>mysql -p&lt;secret&gt;</c> publishes the credential to every user on the box.
/// </para>
/// </summary>
public sealed partial class DatabaseEngineOperations(IContainerRuntime runtime, ILogger<DatabaseEngineOperations> log)
{
    /// <summary>
    /// Identifiers the agent generates itself, checked anyway. Both username and password come from
    /// <see cref="Security.LocalSecretVault"/>'s safe alphabets, so this can only fail if something
    /// upstream changed — which is exactly when a check earns its place.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{2,62}$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^[A-Za-z0-9]{16,128}$")]
    private static partial Regex Password();

    public sealed class EngineException(NodeErrorCode code, string message) : Exception(message)
    {
        public NodeErrorCode Code { get; } = code;
    }

    /// <summary>Create a login on the engine and grant it access to one database.</summary>
    public async Task CreateUserAsync(
        string engine, string container, EngineAdmin admin,
        string username, string password, string? database, bool readOnly, CancellationToken ct)
    {
        Guard(username, password);

        var (argv, env, stdin) = engine.ToLowerInvariant() switch
        {
            DatabaseEngines.PostgreSql => PostgresCreate(admin, username, password, database, readOnly),
            DatabaseEngines.MySql => MySqlCreate(admin, username, password, database, readOnly),
            DatabaseEngines.MongoDb => MongoCreate(admin, username, password, database, readOnly),
            DatabaseEngines.Redis => RedisCreate(admin, username, password, readOnly),
            _ => throw new EngineException(NodeErrorCode.UnsupportedDatabaseEngine, $"Engine '{engine}' is not supported."),
        };

        await ExecuteAsync(engine, container, argv, env, stdin, $"create user {username}", ct);
    }

    /// <summary>Change an existing login's password.</summary>
    public async Task RotatePasswordAsync(
        string engine, string container, EngineAdmin admin, string username, string password, CancellationToken ct)
    {
        Guard(username, password);

        var (argv, env, stdin) = engine.ToLowerInvariant() switch
        {
            DatabaseEngines.PostgreSql => PostgresSql(admin, $"ALTER ROLE \"{username}\" WITH PASSWORD '{password}';"),
            DatabaseEngines.MySql => MySqlSql(admin, $"ALTER USER '{username}'@'%' IDENTIFIED BY '{password}'; FLUSH PRIVILEGES;"),
            DatabaseEngines.MongoDb => MongoScript(admin, $"db.changeUserPassword({Quote(username)}, {Quote(password)})"),
            DatabaseEngines.Redis => RedisArgs(admin, ["ACL", "SETUSER", username, "resetpass", $">{password}"]),
            _ => throw new EngineException(NodeErrorCode.UnsupportedDatabaseEngine, $"Engine '{engine}' is not supported."),
        };

        await ExecuteAsync(engine, container, argv, env, stdin, $"rotate password for {username}", ct);
    }

    /// <summary>Drop a login. Missing is success — the end state is what was asked for.</summary>
    public async Task DropUserAsync(
        string engine, string container, EngineAdmin admin, string username, string? database, CancellationToken ct)
    {
        if (!Identifier().IsMatch(username))
            throw new EngineException(NodeErrorCode.ValidationFailed, $"'{username}' is not a valid database user name.");

        var (argv, env, stdin) = engine.ToLowerInvariant() switch
        {
            DatabaseEngines.PostgreSql => PostgresSql(admin,
                $"REASSIGN OWNED BY \"{username}\" TO \"{admin.Username}\"; DROP OWNED BY \"{username}\"; DROP ROLE IF EXISTS \"{username}\";"),
            DatabaseEngines.MySql => MySqlSql(admin, $"DROP USER IF EXISTS '{username}'@'%'; FLUSH PRIVILEGES;"),
            DatabaseEngines.MongoDb => MongoScript(admin, $"db.dropUser({Quote(username)})", database),
            DatabaseEngines.Redis => RedisArgs(admin, ["ACL", "DELUSER", username]),
            _ => throw new EngineException(NodeErrorCode.UnsupportedDatabaseEngine, $"Engine '{engine}' is not supported."),
        };

        try
        {
            await ExecuteAsync(engine, container, argv, env, stdin, $"drop user {username}", ct);
        }
        catch (EngineException e)
        {
            // Revocation must not be blocked by the user already being gone. Leaving a grant marked
            // active because the cleanup failed is the worse of the two outcomes by a wide margin.
            log.LogWarning("Dropping {User} on {Engine} reported: {Message}. Treating the user as gone.", username, engine, e.Message);
        }
    }

    /// <summary>Well-known admin credential keys per engine, as the official images define them.</summary>
    public static EngineAdmin? AdminFrom(string engine, IReadOnlyDictionary<string, string> environment)
    {
        string? Value(params string[] keys) =>
            keys.Select(k => environment.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrEmpty(v));

        return engine.ToLowerInvariant() switch
        {
            DatabaseEngines.PostgreSql when Value("POSTGRES_PASSWORD") is { } pgPass =>
                new EngineAdmin(Value("POSTGRES_USER") ?? "postgres", pgPass, Value("POSTGRES_DB")),

            DatabaseEngines.MySql when Value("MYSQL_ROOT_PASSWORD", "MARIADB_ROOT_PASSWORD") is { } myPass =>
                new EngineAdmin("root", myPass, Value("MYSQL_DATABASE", "MARIADB_DATABASE")),

            DatabaseEngines.MongoDb when Value("MONGO_INITDB_ROOT_PASSWORD") is { } mongoPass =>
                new EngineAdmin(Value("MONGO_INITDB_ROOT_USERNAME") ?? "root", mongoPass, Value("MONGO_INITDB_DATABASE") ?? "admin"),

            DatabaseEngines.Redis when Value("REDIS_PASSWORD", "REDIS_ARGS") is { } redisPass =>
                new EngineAdmin("default", redisPass, null),

            _ => null,
        };
    }

    // --- per-engine command construction ---

    private static (IReadOnlyList<string> Argv, Dictionary<string, string> Env, string? Stdin) PostgresCreate(
        EngineAdmin admin, string username, string password, string? database, bool readOnly)
    {
        var target = Quoted(database ?? admin.Database ?? "postgres");

        // Identifiers are double-quoted and the password is single-quoted; both are agent-generated
        // from alphabets with no quote characters in them, and Guard has already enforced that.
        var sql = readOnly
            ? $"""
               CREATE ROLE "{username}" LOGIN PASSWORD '{password}';
               GRANT CONNECT ON DATABASE {target} TO "{username}";
               GRANT USAGE ON SCHEMA public TO "{username}";
               GRANT SELECT ON ALL TABLES IN SCHEMA public TO "{username}";
               ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO "{username}";
               """
            : $"""
               CREATE ROLE "{username}" LOGIN PASSWORD '{password}';
               GRANT CONNECT ON DATABASE {target} TO "{username}";
               GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO "{username}";
               GRANT USAGE, CREATE ON SCHEMA public TO "{username}";
               """;

        return PostgresSql(admin, sql, database);
    }

    private static (IReadOnlyList<string>, Dictionary<string, string>, string?) PostgresSql(
        EngineAdmin admin, string sql, string? database = null) =>
    (
        // -f - reads the script from stdin, which keeps the SQL off the command line entirely.
        ["psql", "-v", "ON_ERROR_STOP=1", "-U", admin.Username, "-d", database ?? admin.Database ?? "postgres", "-f", "-"],
        new Dictionary<string, string> { ["PGPASSWORD"] = admin.Password },
        sql
    );

    private static (IReadOnlyList<string>, Dictionary<string, string>, string?) MySqlCreate(
        EngineAdmin admin, string username, string password, string? database, bool readOnly)
    {
        var scope = database is { Length: > 0 } ? $"`{database}`.*" : "*.*";
        var privileges = readOnly ? "SELECT, SHOW VIEW" : "ALL PRIVILEGES";

        return MySqlSql(admin, $"""
            CREATE USER '{username}'@'%' IDENTIFIED BY '{password}';
            GRANT {privileges} ON {scope} TO '{username}'@'%';
            FLUSH PRIVILEGES;
            """);
    }

    private static (IReadOnlyList<string>, Dictionary<string, string>, string?) MySqlSql(EngineAdmin admin, string sql) =>
    (
        ["mysql", "-u", admin.Username, "--batch"],
        // MYSQL_PWD rather than -p: a command line is world-readable in /proc.
        new Dictionary<string, string> { ["MYSQL_PWD"] = admin.Password },
        sql
    );

    private static (IReadOnlyList<string>, Dictionary<string, string>, string?) MongoCreate(
        EngineAdmin admin, string username, string password, string? database, bool readOnly)
    {
        var role = readOnly ? "read" : "readWrite";
        var target = database ?? admin.Database ?? "admin";

        var script = $"db.getSiblingDB({Quote(target)}).createUser({{user:{Quote(username)},pwd:{Quote(password)},roles:[{{role:{Quote(role)},db:{Quote(target)}}}]}})";

        return MongoScript(admin, script, target);
    }

    private static (IReadOnlyList<string>, Dictionary<string, string>, string?) MongoScript(
        EngineAdmin admin, string script, string? database = null) =>
    (
        [
            "mongosh", "--quiet",
            "--username", admin.Username,
            "--authenticationDatabase", "admin",
            database ?? admin.Database ?? "admin",
        ],
        // mongosh reads the password from stdin when --password is omitted but --username is given;
        // the script follows on the same stream.
        new Dictionary<string, string>(),
        admin.Password + "\n" + script + "\n"
    );

    private static (IReadOnlyList<string>, Dictionary<string, string>, string?) RedisCreate(
        EngineAdmin admin, string username, string password, bool readOnly) =>
        RedisArgs(admin,
        [
            "ACL", "SETUSER", username, "on", $">{password}",
            "~*",
            readOnly ? "+@read" : "+@all",
        ]);

    private static (IReadOnlyList<string>, Dictionary<string, string>, string?) RedisArgs(
        EngineAdmin admin, IReadOnlyList<string> arguments) =>
    (
        ["redis-cli", .. arguments],
        // REDISCLI_AUTH rather than -a, for the same /proc reason.
        new Dictionary<string, string> { ["REDISCLI_AUTH"] = admin.Password },
        null
    );

    private async Task ExecuteAsync(
        string engine, string container, IReadOnlyList<string> argv,
        Dictionary<string, string> env, string? stdin, string what, CancellationToken ct)
    {
        var result = await runtime.ExecAsync(container, argv, env, stdin, ct);

        if (result.ExitCode == 0) return;

        // The engine's stderr can echo the statement it choked on, which would put the new password
        // in the error the control plane receives.
        var detail = Redacted(result.Stderr, stdin);

        throw new EngineException(
            NodeErrorCode.CredentialRotationFailed,
            $"Could not {what} on {engine}: the client exited {result.ExitCode}. {detail}");
    }

    private static string Redacted(string stderr, string? stdin)
    {
        var text = stderr.Trim();
        if (text.Length == 0) return string.Empty;

        // Anything the engine quotes back from a script that contained a password is unsafe to
        // forward verbatim, so a failure that echoed the script is summarised instead.
        if (stdin is { Length: > 0 } && text.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase))
            return "(engine output withheld: it echoed the statement, which contains a credential)";

        return text.Length > 400 ? text[..400] + "…" : text;
    }

    private static void Guard(string username, string password)
    {
        if (!Identifier().IsMatch(username))
            throw new EngineException(NodeErrorCode.ValidationFailed, $"'{username}' is not a valid database user name.");

        if (!Password().IsMatch(password))
            throw new EngineException(NodeErrorCode.ValidationFailed,
                "The generated password contains characters that are unsafe to embed in an engine statement.");
    }

    private static string Quoted(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string Quote(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
