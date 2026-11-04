using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Infrastructure.Verification;
using Xunit;
using static Harbora.Infrastructure.Verification.AppLogDiagnosis;

namespace Harbora.Tests.Verification;

/// <summary>
/// The log reader, on lines the way Node, Python, .NET and Go really print them. Half of what this file
/// protects is silence: this feeds a warning on the page somebody reads to decide whether their app is
/// healthy, so a reader that fires on a healthy log teaches them to ignore it.
/// </summary>
public class AppLogDiagnosisTests
{
    private static string? CodeOf(params string[] lines) => Find(lines).SingleOrDefault()?.Code;

    // ---- it is connecting to itself ----

    [Theory]
    // Node
    [InlineData("Error: connect ECONNREFUSED 127.0.0.1:5432")]
    [InlineData("Error: connect ECONNREFUSED ::1:5432")]
    [InlineData("[ioredis] Unhandled error event: Error: connect ECONNREFUSED 127.0.0.1:6379")]
    [InlineData("MongoServerSelectionError: connect ECONNREFUSED ::1:27017")]
    [InlineData("Error: connect ECONNREFUSED 127.0.0.1:5672")]
    [InlineData("Error: connect ECONNREFUSED 127.0.0.1:3306")]
    // Python
    [InlineData("psycopg2.OperationalError: connection to server at \"localhost\" (127.0.0.1), port 5432 failed: Connection refused")]
    [InlineData("redis.exceptions.ConnectionError: Error 111 connecting to localhost:6379. Connection refused.")]
    [InlineData("pymongo.errors.ServerSelectionTimeoutError: localhost:27017: [Errno 111] Connection refused")]
    [InlineData("mysql.connector.errors.DatabaseError: 2003 (HY000): Can't connect to MySQL server on 'localhost:3306' (111)")]
    // .NET
    [InlineData("Npgsql.NpgsqlException (0x80004005): Failed to connect to 127.0.0.1:5432")]
    [InlineData("StackExchange.Redis.RedisConnectionException: It was not possible to connect to the redis server(s). UnableToConnect on localhost:6379/Interactive")]
    // Go
    [InlineData("dial tcp 127.0.0.1:5432: connect: connection refused")]
    [InlineData("dial tcp [::1]:3306: connect: connection refused")]
    public void A_refused_connection_to_loopback_on_a_database_port_is_the_app_talking_to_itself(string line)
    {
        CodeOf(line).Should().Be(ConnectingToItself,
            "it is one problem with a precise name, not that and also a vaguer \"cannot reach\"");
    }

    [Fact]
    public void Libpq_older_wording_spreads_it_over_two_lines_and_is_still_one_finding()
    {
        var findings = Find([
            "could not connect to server: Connection refused",
            "\tIs the server running on host \"localhost\" (127.0.0.1) and accepting",
            "\tTCP/IP connections on port 5432?",
            "could not connect to server: Connection refused",
            "\tIs the server running on host \"localhost\" (127.0.0.1) and accepting TCP/IP connections on port 5432?"
        ]);

        findings.Select(f => f.Code).Should().Equal(ConnectingToItself);
    }

    [Theory]
    [InlineData("ERROR 2002 (HY000): Can't connect to local MySQL server through socket '/var/run/mysqld/mysqld.sock' (2)")]
    [InlineData("OperationalError: (2003, \"Can't connect to MySQL server on 'localhost' (111)\")")]
    public void The_mysql_client_says_it_its_own_way(string line) =>
        CodeOf(line).Should().Be(ConnectingToItself);

    [Theory]
    [InlineData("Connected to redis at localhost:6379")]
    [InlineData("Postgres is listening on 127.0.0.1:5432")]
    [InlineData("connecting to localhost:5432")]
    [InlineData("Error: connect ECONNREFUSED 127.0.0.1:54321")]
    [InlineData("Error: connect ECONNREFUSED 10.127.0.0.1:5432")]
    [InlineData("Error: connect ECONNREFUSED my-localhost:5432")]
    public void Loopback_with_no_error_or_not_quite_that_port_or_host_is_not_it(string line)
    {
        Find([line]).Select(f => f.Code).Should().NotContain(ConnectingToItself);
    }

    // ---- the rest of the table ----

    [Theory]
    [InlineData("Error: connect ECONNREFUSED 10.0.0.7:5432")]
    [InlineData("Error: getaddrinfo ENOTFOUND harbora-svc-orders")]
    [InlineData("could not translate host name \"db\" to address: Name or service not known")]
    [InlineData("dial tcp: lookup db on 127.0.0.11:53: no such host")]
    [InlineData("psycopg2.OperationalError: could not connect to server: Connection refused")]
    public void It_cannot_reach_something(string line) => CodeOf(line).Should().Be(CannotReach);

    [Theory]
    [InlineData("error: password authentication failed for user \"app\"")]
    [InlineData("Npgsql.PostgresException (0x80004005): 28P01: password authentication failed for user \"app\"")]
    [InlineData("Error 1045 (28000): Access denied for user 'app'@'172.18.0.3' (using password: YES)")]
    [InlineData("MongoServerError: Authentication failed.")]
    [InlineData("ReplyError: WRONGPASS invalid username-password pair or user is disabled.")]
    [InlineData("redis.exceptions.AuthenticationError: NOAUTH Authentication required.")]
    public void It_reached_the_database_with_the_wrong_credentials(string line) =>
        CodeOf(line).Should().Be(WrongCredentials);

    [Theory]
    [InlineData("error: relation \"users\" does not exist")]
    [InlineData("Npgsql.PostgresException (0x80004005): 42P01: relation \"Users\" does not exist")]
    [InlineData("Error 1146 (42S02): Table 'app.users' doesn't exist")]
    [InlineData("sqlite3.OperationalError: no such table: users")]
    public void Its_migrations_have_not_run(string line) => CodeOf(line).Should().Be(MigrationsNotRun);

    [Theory]
    [InlineData("Error: Cannot find module 'express'")]
    [InlineData("ModuleNotFoundError: No module named 'flask'")]
    [InlineData("ImportError: No module named requests")]
    [InlineData("System.IO.FileNotFoundException: Could not load file or assembly 'Foo.Bar, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'.")]
    public void A_dependency_is_missing_from_the_build(string line) => CodeOf(line).Should().Be(MissingDependency);

    [Theory]
    [InlineData("Error: listen EADDRINUSE: address already in use :::3000")]
    [InlineData("OSError: [Errno 98] Address already in use")]
    [InlineData("listen tcp :8080: bind: address already in use")]
    public void Two_things_want_one_port(string line) => CodeOf(line).Should().Be(PortInUse);

    [Theory]
    [InlineData(" * Running on http://127.0.0.1:5000")]
    [InlineData("INFO:     Uvicorn running on http://127.0.0.1:8000 (Press CTRL+C to quit)")]
    [InlineData("2026/11/04 10:00:00 listening on 127.0.0.1:8080")]
    [InlineData("      Now listening on: http://localhost:5000")]
    [InlineData("[2026-11-04 10:00:00 +0000] [1] [INFO] Listening at: http://127.0.0.1:8000 (1)")]
    [InlineData("bind 127.0.0.1")]
    public void It_listens_only_inside_its_own_container(string line) => CodeOf(line).Should().Be(ListeningOnLoopback);

    [Fact]
    public void Flask_bound_to_everything_prints_its_loopback_address_too_and_that_is_healthy()
    {
        Find([
            " * Running on all addresses (0.0.0.0)",
            " * Running on http://127.0.0.1:5000",
            " * Running on http://172.18.0.3:5000"
        ]).Should().BeEmpty();
    }

    // ---- silence ----

    [Theory]
    [InlineData("Connected to database at harbora-svc-orders:5432")]
    [InlineData("connection established to harbora-svc-orders:5432")]
    [InlineData("Database connection pool size: 10")]
    [InlineData("GET /connection 200 3ms")]
    [InlineData("GET /auth/failed 200 3ms")]
    [InlineData("route /noauth registered")]
    [InlineData("loading relation between users and posts")]
    [InlineData("Listening on 0.0.0.0:3000")]
    [InlineData("Server running on http://localhost:3000")]
    [InlineData("      Now listening on: http://[::]:8080")]
    [InlineData("Application started. Press Ctrl+C to shut down.")]
    [InlineData("    at TCPConnectWrap.afterConnect [as oncomplete] (node:net:1595:16)")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_healthy_line_says_nothing(string line) => Find([line]).Should().BeEmpty();

    [Fact]
    public void A_whole_healthy_log_says_nothing()
    {
        Find([
            "info: Microsoft.Hosting.Lifetime[14]",
            "      Now listening on: http://[::]:8080",
            "info: Microsoft.Hosting.Lifetime[0]",
            "      Application started. Press Ctrl+C to shut down.",
            "info: Shop.Data[0]",
            "      Opened connection to database 'shop' on server 'harbora-svc-orders'.",
            "GET /health 200 2ms"
        ]).Should().BeEmpty();
    }

    // ---- the shape of the answer ----

    [Fact]
    public void One_finding_per_kind_however_often_the_log_repeats_it()
    {
        var crashLoop = Enumerable.Repeat("Error: connect ECONNREFUSED 127.0.0.1:5432", 50);

        Find(crashLoop).Select(f => f.Code).Should().Equal(ConnectingToItself);
    }

    [Fact]
    public void At_most_three_and_the_ones_about_its_wiring_come_first()
    {
        var findings = Find([
            "bind 127.0.0.1",
            "Error: listen EADDRINUSE: address already in use :::3000",
            "Error: Cannot find module 'express'",
            "error: relation \"users\" does not exist",
            "error: password authentication failed for user \"app\"",
            "Error: connect ECONNREFUSED 127.0.0.1:5432"
        ]);

        findings.Should().HaveCount(MaxFindings);
        findings.Select(f => f.Code).Should().Equal(ConnectingToItself, WrongCredentials, MigrationsNotRun);
    }

    [Fact]
    public void Every_finding_has_a_sentence_in_both_languages()
    {
        var everyKind = new[]
        {
            "Error: connect ECONNREFUSED 127.0.0.1:5432", "Error: getaddrinfo ENOTFOUND db",
            "password authentication failed", "no such table: users", "Cannot find module 'x'",
            "EADDRINUSE", "bind 127.0.0.1"
        };

        var persian = new Regex(@"[؀-ۿ]");
        foreach (var line in everyKind)
        {
            var finding = Find([line]).Single();
            finding.Message.Should().NotBeNullOrWhiteSpace();
            persian.IsMatch(finding.MessageFa).Should().BeTrue($"{finding.Code} is read by a Persian speaker first");
        }
    }

    [Fact]
    public void The_fragment_is_the_match_and_not_the_line_it_was_found_in()
    {
        var finding = Find(["Error: connect ECONNREFUSED postgres://admin:S3cretPass@localhost:5432/shop?sslmode=off"]).Single();

        finding.Fragment.Should().Be("localhost:5432");
    }

    [Fact]
    public void A_long_name_is_cut_rather_than_quoted_whole()
    {
        var name = new string('x', 80);

        Find([$"Error: Cannot find module '{name}'"]).Single().Fragment.Length.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void Colour_codes_do_not_hide_a_phrase()
    {
        CodeOf("\u001b[31mError: connect \u001b[1mECONNREFUSED\u001b[22m 127.0.0.1:5432\u001b[39m")
            .Should().Be(ConnectingToItself);
    }

    [Fact]
    public void A_line_the_size_of_a_payload_does_not_hold_the_request()
    {
        var payload = new string('a', 2_000_000) + " Connection refused";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Find([payload]).Should().BeEmpty("only the start of a line that long is read");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    // ---- masking, for anything longer than a fragment ----

    [Theory]
    [InlineData("postgres://admin:hunter2@db:5432/shop", "postgres://admin:***@db:5432/shop")]
    [InlineData("redis://:hunter2@cache:6379", "redis://:***@cache:6379")]
    [InlineData("DB_PASSWORD=hunter2 is wrong", "DB_PASSWORD=*** is wrong")]
    [InlineData("{\"password\":\"hunter2\",\"user\":\"a\"}", "{\"password\":***,\"user\":\"a\"}")]
    [InlineData("api_key: hunter2", "api_key: ***")]
    [InlineData("Authorization: Bearer hunter2", "Authorization: Bearer ***")]
    public void A_password_the_platform_never_heard_of_is_masked_by_where_it_sits(string line, string expected) =>
        MaskCredentials(line).Should().Be(expected);

    [Theory]
    [InlineData("password authentication failed for user \"app\"")]
    [InlineData("https://shop.example.com/health answered 200.")]
    [InlineData("the token expired")]
    public void Ordinary_words_are_left_alone(string line) => MaskCredentials(line).Should().Be(line);
}
