using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Finding out whether a service can really reach its database.
///
/// Everything else on the screen is configuration: a hostname that is written down, a password that
/// is stored. All of it can be right while the connection still fails — the wrong network, a
/// container that never came back, a password rotated in one place and not the other.
/// </summary>
public class ConnectionProbeTests
{
    private static readonly ServiceCreds Creds =
        new("harbora-svc-shop-db", 5432, "shop", "s3cretpassword", "shopdata");

    [Fact]
    public void The_probe_authenticates_rather_than_just_opening_a_socket()
    {
        // A port that accepts TCP proves nothing about credentials, and wrong credentials are the
        // common failure.
        var command = string.Join(" ", ConnectionProbe.For(ManagedServiceType.PostgreSql, Creds)!.Command);

        command.Should().Contain("SELECT 1");
        command.Should().Contain("harbora-svc-shop-db");
    }

    [Fact]
    public void The_password_never_appears_on_the_command_line()
    {
        // Every engine there is, so that the next one added is held to it without anybody remembering.
        foreach (var type in ServiceCatalog.All.Keys)
        {
            var plan = ConnectionProbe.For(type, Creds)!;

            string.Join(" ", plan.Command).Should().NotContain("s3cretpassword", $"{type} leaks the password");
            plan.Env.Values.Should().Contain(v => v.Contains("s3cretpassword"), $"{type} must still authenticate");
        }
    }

    [Fact]
    public void Every_engine_in_the_catalogue_can_be_asked()
    {
        // MongoDB, RabbitMQ and NATS used to answer "not tested", so a launch on one of them deployed
        // beside a database nobody had asked anything, and its check could never say Passed.
        foreach (var type in ServiceCatalog.All.Keys)
        {
            ConnectionProbe.For(type, Creds).Should().NotBeNull($"{type} has no probe");
            ConnectionProbe.WhyUnsupported(type).Should().BeNull($"{type} has a probe, so nothing excuses it");
        }
    }

    [Fact]
    public void An_engine_with_no_probe_says_it_cannot_be_tested_rather_than_reporting_a_pass()
    {
        // A test that always succeeds is worse than no test: it is believed. No engine is in this
        // position today, so this stands in for the next one that is added without a probe.
        const ManagedServiceType notYetInvented = (ManagedServiceType)999;

        ConnectionProbe.For(notYetInvented, Creds).Should().BeNull();
        ConnectionProbe.WhyUnsupported(notYetInvented).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Mongodb_is_asked_something_only_a_login_may_ask()
    {
        // ping answers anybody, logged in or not, so a probe built on it would pass with a wrong password.
        var plan = ConnectionProbe.For(ManagedServiceType.MongoDb, Creds with { Port = 27017 })!;
        var command = string.Join(" ", plan.Command);

        command.Should().Contain("mongosh").And.Contain("listDatabases").And.NotContain("ping");
        plan.Env["HARBORA_PROBE_URI"].Should()
            .StartWith("mongodb://shop:s3cretpassword@harbora-svc-shop-db:27017/admin?")
            .And.Contain("authSource=admin");
    }

    [Fact]
    public void A_mongodb_password_with_a_reserved_character_still_makes_an_address()
    {
        // Generated passwords have none, and one typed by a person on the database's page can.
        var plan = ConnectionProbe.For(ManagedServiceType.MongoDb, Creds with { Password = "p@ss/w:rd" })!;

        plan.Env["HARBORA_PROBE_URI"].Should().Contain(":p%40ss%2Fw%3Ard@harbora-svc-shop-db:");
    }

    [Fact]
    public void Rabbitmq_is_asked_over_amqp_with_its_own_login()
    {
        // rabbitmqctl would talk to the node over Erlang distribution, which says nothing about the
        // port an app connects to or the password it was handed.
        var plan = ConnectionProbe.For(ManagedServiceType.RabbitMq, Creds with { Port = 5672 })!;

        string.Join(" ", plan.Command).Should().Contain("erl").And.Contain("$HARBORA_PROBE_PROGRAM");
        plan.Env["HARBORA_PROBE_PROGRAM"].Should()
            .Contain("<<\"AMQP\", 0, 0, 9, 1>>").And.Contain("\"PLAIN\"").And.Contain("AMQP login accepted");
        plan.Env["HARBORA_PROBE_HOST"].Should().Be("harbora-svc-shop-db");
        plan.Env["HARBORA_PROBE_PORT"].Should().Be("5672");
        plan.Env["HARBORA_PROBE_USER"].Should().Be("shop");
        plan.Env["HARBORA_PROBE_PASS"].Should().Be("s3cretpassword");
    }

    [Fact]
    public void Nats_is_asked_to_answer_a_ping_after_logging_in()
    {
        var plan = ConnectionProbe.For(ManagedServiceType.Nats, Creds with { Port = 4222 })!;
        var command = string.Join(" ", plan.Command);

        command.Should().Contain("nc -w 5 'harbora-svc-shop-db' 4222");
        command.Should().Contain("CONNECT %s\\r\\nPING\\r\\n").And.Contain("grep -qx 'PONG'");

        using var connect = System.Text.Json.JsonDocument.Parse(plan.Env["HARBORA_PROBE_CONNECT"]);
        connect.RootElement.GetProperty("user").GetString().Should().Be("shop");
        connect.RootElement.GetProperty("pass").GetString().Should().Be("s3cretpassword");
        connect.RootElement.GetProperty("verbose").GetBoolean().Should().BeFalse("+OK lines would sit between the login and the PONG");
    }

    [Fact]
    public void A_nats_password_with_a_quote_in_it_is_still_one_json_value()
    {
        var plan = ConnectionProbe.For(ManagedServiceType.Nats, Creds with { Password = "a\"b\\c" })!;

        using var connect = System.Text.Json.JsonDocument.Parse(plan.Env["HARBORA_PROBE_CONNECT"]);
        connect.RootElement.GetProperty("pass").GetString().Should().Be("a\"b\\c");
        plan.Env["HARBORA_PROBE_CONNECT"].Should().NotContain("\n").And.NotContain("\r", "the protocol is one line per command");
    }

    [Theory]
    [InlineData("could not translate host name \"harbora-svc-shop-db\"", "private network")]
    [InlineData("psql: error: password authentication failed for user \"shop\"", "rotated")]
    [InlineData("Access denied for user 'shop'@'172.18.0.5'", "rotated")]
    [InlineData("(WRONGPASS invalid username-password pair)", "rotated")]
    [InlineData("Connection refused", "stopped")]
    // mongosh, the Erlang program and busybox nc, each in its own words.
    [InlineData("MongoNetworkError: getaddrinfo ENOTFOUND harbora-svc-shop-db", "private network")]
    [InlineData("MongoServerSelectionError: getaddrinfo EAI_AGAIN harbora-svc-shop-db", "private network")]
    [InlineData("AMQP connect failed: nxdomain", "private network")]
    [InlineData("nc: bad address 'harbora-svc-shop-db'", "private network")]
    [InlineData("MongoServerError: Authentication failed.", "rotated")]
    [InlineData("AMQP login refused by the broker, which closed the connection", "rotated")]
    [InlineData("-ERR 'Authorization Violation'", "rotated")]
    [InlineData("MongoNetworkError: connect ECONNREFUSED 172.18.0.5:27017", "stopped")]
    [InlineData("AMQP connect failed: econnrefused", "stopped")]
    [InlineData("nc: can't connect to remote host (172.18.0.5): Connection refused", "stopped")]
    public void A_failure_is_explained_as_the_thing_to_go_and_fix(string output, string expected)
    {
        // The exit code means nothing to the person reading it, and the client's own message
        // describes a symptom rather than the cause.
        ConnectionProbe.Explain(ManagedServiceType.PostgreSql, output).Should().Contain(expected);
    }

    [Fact]
    public void An_unrecognised_failure_still_shows_what_the_client_said()
    {
        // Guessing wrong is worse than passing it along: the raw text is at least true.
        var explained = ConnectionProbe.Explain(ManagedServiceType.PostgreSql, "server closed the connection unexpectedly");

        explained.Should().Contain("server closed the connection unexpectedly");
    }

    [Fact]
    public void A_failure_with_nothing_to_say_does_not_pretend_to_know_why()
    {
        ConnectionProbe.Explain(ManagedServiceType.PostgreSql, null).Should().Contain("connection failed");
    }
}
