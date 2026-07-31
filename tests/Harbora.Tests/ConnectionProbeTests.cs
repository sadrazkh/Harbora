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
        foreach (var type in new[]
                 { ManagedServiceType.PostgreSql, ManagedServiceType.MySql, ManagedServiceType.MariaDb, ManagedServiceType.Redis })
        {
            var plan = ConnectionProbe.For(type, Creds)!;

            string.Join(" ", plan.Command).Should().NotContain("s3cretpassword", $"{type} leaks the password");
            plan.Env.Values.Should().Contain("s3cretpassword", $"{type} must still authenticate");
        }
    }

    [Fact]
    public void Mongodb_says_it_cannot_be_tested_rather_than_reporting_a_pass()
    {
        // A test that always succeeds is worse than no test: it is believed.
        ConnectionProbe.For(ManagedServiceType.MongoDb, Creds).Should().BeNull();
        ConnectionProbe.WhyUnsupported(ManagedServiceType.MongoDb).Should().NotBeNullOrWhiteSpace();
        ConnectionProbe.WhyUnsupported(ManagedServiceType.PostgreSql).Should().BeNull();
    }

    [Theory]
    [InlineData("could not translate host name \"harbora-svc-shop-db\"", "private network")]
    [InlineData("psql: error: password authentication failed for user \"shop\"", "rotated")]
    [InlineData("Access denied for user 'shop'@'172.18.0.5'", "rotated")]
    [InlineData("(WRONGPASS invalid username-password pair)", "rotated")]
    [InlineData("Connection refused", "stopped")]
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
