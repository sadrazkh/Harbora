using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Opening a managed database to the outside world.
///
/// This is the feature with the least room for a mistake in the permissive direction: everything it
/// decides ends up as a port on the public internet with somebody's data behind it.
/// </summary>
public class TcpGatewayPlanTests
{
    // ---- ports ----

    [Fact]
    public void The_first_grant_takes_the_first_port()
    {
        TcpGatewayPlan.NextPort([]).Should().Be(TcpGatewayPlan.FirstPort);
    }

    [Fact]
    public void A_port_in_use_is_not_handed_out_twice()
    {
        // Two grants on one port is one customer's client reaching another customer's database.
        TcpGatewayPlan.NextPort([TcpGatewayPlan.FirstPort]).Should().Be(TcpGatewayPlan.FirstPort + 1);
    }

    [Fact]
    public void A_gap_left_by_a_closed_grant_is_reused()
    {
        // Lowest-free rather than next-highest: a connection string somebody saved yesterday points
        // at the same number again instead of at whichever database took the port after them.
        var taken = new[] { TcpGatewayPlan.FirstPort, TcpGatewayPlan.FirstPort + 2 };

        TcpGatewayPlan.NextPort(taken).Should().Be(TcpGatewayPlan.FirstPort + 1);
    }

    [Fact]
    public void A_full_band_hands_out_nothing_rather_than_something_outside_it()
    {
        // Returning a port past the band would publish on whatever else the host is running there.
        var all = Enumerable.Range(TcpGatewayPlan.FirstPort, TcpGatewayPlan.LastPort - TcpGatewayPlan.FirstPort + 1);

        TcpGatewayPlan.NextPort(all).Should().BeNull();
    }

    [Fact]
    public void Every_port_it_offers_is_inside_the_band()
    {
        for (var used = 0; used < 50; used++)
        {
            var port = TcpGatewayPlan.NextPort(Enumerable.Range(TcpGatewayPlan.FirstPort, used));
            port.Should().BeInRange(TcpGatewayPlan.FirstPort, TcpGatewayPlan.LastPort);
        }
    }

    // ---- where a client connects ----

    [Fact]
    public void The_host_is_the_services_own_subdomain_of_the_platform_domain()
    {
        // The wildcard that already answers for deployed apps, so a grant that lasts fifteen minutes
        // needs no DNS record of its own.
        TcpGatewayPlan.HostFor("harbora.example.com", "Shop DB", "203.0.113.7")
            .Should().Be("shop-db.harbora.example.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost")]
    public void With_no_usable_root_domain_the_address_is_used_instead(string? rootDomain)
    {
        // A connection string with a bare address works. One with a hostname that does not resolve
        // is a support ticket that looks like a broken database.
        TcpGatewayPlan.HostFor(rootDomain, "Shop DB", "203.0.113.7").Should().Be("203.0.113.7");
    }

    [Fact]
    public void With_neither_a_domain_nor_an_address_it_says_localhost_rather_than_nothing()
    {
        TcpGatewayPlan.HostFor(null, "Shop DB", null).Should().Be("localhost");
    }

    [Theory]
    [InlineData("Shop DB", "shop-db")]
    [InlineData("  Orders  ", "orders")]
    [InlineData("a//b", "a-b")]
    [InlineData("Ünïcødé", "n-c-d")]
    public void The_subdomain_is_safe_to_put_in_a_hostname(string name, string expected)
    {
        TcpGatewayPlan.HostFor("example.com", name, null).Should().Be($"{expected}.example.com");
    }

    // ---- who may connect ----

    [Fact]
    public void An_empty_allowlist_means_anywhere_and_says_so_by_writing_no_rule()
    {
        var config = TcpGatewayPlan.Config("harbora-svc-shop", 5432, null);

        config.Should().NotBeNull();
        config.Should().NotContain("reject");
    }

    [Fact]
    public void An_allowlist_rejects_everything_it_does_not_name()
    {
        var config = TcpGatewayPlan.Config("harbora-svc-shop", 5432, "203.0.113.7, 198.51.100.0/24");

        config.Should().Contain("acl allowed src 203.0.113.7 198.51.100.0/24");
        config.Should().Contain("tcp-request connection reject if !allowed");
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("203.0.113.7/64")]
    [InlineData("203.0.113.999")]
    [InlineData("203.0.113.7/abc")]
    [InlineData("203.0.113.7, oops")]
    public void An_entry_that_cannot_be_read_closes_the_door_rather_than_opening_it(string allowed)
    {
        // The direction this must fail in. A typo that locks the customer out is reported within the
        // hour; a typo that publishes their database to the internet is found in an incident review.
        TcpGatewayPlan.Config("harbora-svc-shop", 5432, allowed).Should().BeNull();
    }

    [Fact]
    public void An_ipv6_range_is_understood()
    {
        TcpGatewayPlan.Config("harbora-svc-shop", 5432, "2001:db8::/32").Should().NotBeNull();
    }

    [Fact]
    public void A_plaintext_client_is_dropped_before_the_database_sees_it()
    {
        // PostgreSQL with ssl=on still accepts plaintext: a client passing sslmode=disable connects
        // in the clear over a port on the internet and nothing tells anyone. Forcing it on the
        // server means owning its pg_hba.conf, including local access. The gateway is ours.
        var config = TcpGatewayPlan.Config("harbora-svc-shop", 5432, null, requireTls: true);

        // Length 8, request code 80877103 — the fixed opening of a libpq TLS handshake.
        config.Should().Contain("req.payload(0,8) -m bin 0000000804d2162f");
        config.Should().Contain("tcp-request content reject");
    }

    [Fact]
    public void An_engine_that_does_not_open_with_that_handshake_is_left_alone()
    {
        // MySQL speaks first on its own connections, so the same check would reject every client.
        var config = TcpGatewayPlan.Config("harbora-svc-shop", 3306, null, requireTls: false);

        config.Should().NotContain("req.payload");
        config.Should().NotContain("tcp-request content reject");
    }

    [Fact]
    public void Requiring_encryption_does_not_replace_the_allowlist()
    {
        // Both, or a grant that names its addresses would silently stop enforcing them the moment
        // encryption was turned on.
        var config = TcpGatewayPlan.Config("harbora-svc-shop", 5432, "203.0.113.7", requireTls: true);

        config.Should().Contain("tcp-request connection reject if !allowed");
        config.Should().Contain("req.payload");
    }

    [Fact]
    public void The_proxy_points_at_the_service_on_the_private_network()
    {
        // The database container is never published itself. What is exposed is this proxy, and
        // closing a grant removes it rather than reconfiguring the thing holding the data.
        TcpGatewayPlan.Config("harbora-svc-shop", 5432, null)
            .Should().Contain("server db harbora-svc-shop:5432");
    }

    [Fact]
    public void Each_grant_gets_its_own_container_name()
    {
        // Two in the same millisecond, which is the case that matters: grant ids are version 7 and
        // begin with a timestamp, so a truncated name collides and revoking one grant would remove
        // the other one's gateway. The first version of this used the first thirteen digits.
        var names = Enumerable.Range(0, 200)
            .Select(_ => TcpGatewayPlan.ContainerName(Guid.CreateVersion7()))
            .ToList();

        names.Should().OnlyHaveUniqueItems();
        names.Should().OnlyContain(n => n.StartsWith("harbora-gw-"));
    }

    [Fact]
    public void The_config_never_reaches_the_command_line()
    {
        // It is written from an environment variable inside the container. A config in argv is
        // visible in `docker inspect` and in the host's process list for the life of the container.
        var entrypoint = string.Join(' ', TcpGatewayPlan.Entrypoint());

        entrypoint.Should().Contain(TcpGatewayPlan.ConfigVariable);
        entrypoint.Should().NotContain("frontend");
    }

    // ---- the login on the database ----

    [Theory]
    [InlineData(ManagedServiceType.PostgreSql)]
    [InlineData(ManagedServiceType.MySql)]
    [InlineData(ManagedServiceType.MariaDb)]
    public void A_supported_engine_can_be_opened(ManagedServiceType type)
    {
        DatabaseGrantSql.Supports(type).Should().BeTrue();
        DatabaseGrantSql.Create(type, "host", 5432, "harbora", "shop", "hb_reader", "secret123")
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData(ManagedServiceType.Redis)]
    [InlineData(ManagedServiceType.MongoDb)]
    public void An_engine_it_cannot_do_is_refused_rather_than_faked(ManagedServiceType type)
    {
        DatabaseGrantSql.Supports(type).Should().BeFalse();
        DatabaseGrantSql.Create(type, "host", 6379, "harbora", "shop", "hb_reader", "secret123")
            .Should().BeNull();
        DatabaseGrantSql.UnsupportedReason(type).Should().Contain(type.ToString());
    }

    [Theory]
    [InlineData("hb_reader")]
    [InlineData("harbora_a1b2c3d4")]
    public void A_generated_name_is_accepted(string value)
    {
        DatabaseGrantSql.IsSafe(value).Should().BeTrue();
    }

    [Theory]
    [InlineData("bob\"; DROP TABLE users; --")]
    [InlineData("bob'; DROP ROLE admin; --")]
    [InlineData("bob`")]
    [InlineData("bob bob")]
    [InlineData("bob-bob")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_generated_name_is_refused(string? value)
    {
        // Narrower than escaping on purpose. Everything reaching here is generated by Harbora, so a
        // value outside the alphabet is a bug or an attack — and both should stop rather than be
        // quoted around.
        DatabaseGrantSql.IsSafe(value).Should().BeFalse();
    }

    [Fact]
    public void A_name_outside_the_alphabet_produces_no_statement_at_all()
    {
        DatabaseGrantSql.Create(ManagedServiceType.PostgreSql, "host", 5432, "harbora", "shop",
            "bob\"; DROP TABLE users; --", "secret123").Should().BeNull();

        DatabaseGrantSql.Drop(ManagedServiceType.PostgreSql, "host", 5432, "harbora", "shop",
            "bob'; DROP ROLE admin; --").Should().BeNull();
    }

    [Fact]
    public void A_grant_reaches_only_the_database_it_was_issued_for()
    {
        // A role that can read every other database on the instance is a tenancy hole with a
        // friendly name on it.
        var command = string.Join(" ", DatabaseGrantSql
            .Create(ManagedServiceType.PostgreSql, "host", 5432, "harbora", "shop", "hb_reader", "secret123")!
            .Command);

        command.Should().Contain("GRANT CONNECT ON DATABASE \"shop\"");
        command.Should().NotContain("SUPERUSER");
        command.Should().NotContain("CREATEDB");
        command.Should().NotContain("CREATEROLE");
    }

    [Fact]
    public void Dropping_a_login_that_is_already_gone_is_not_an_error()
    {
        // The sweeper and somebody pressing revoke can race, and both must end with it gone.
        var command = string.Join(" ", DatabaseGrantSql
            .Drop(ManagedServiceType.PostgreSql, "host", 5432, "harbora", "shop", "hb_reader")!
            .Command);

        command.Should().Contain("DROP ROLE IF EXISTS");
        command.Should().NotContain("ON_ERROR_STOP");
    }

    [Theory]
    [InlineData(ManagedServiceType.PostgreSql)]
    [InlineData(ManagedServiceType.MySql)]
    [InlineData(ManagedServiceType.MariaDb)]
    public void A_supported_engine_can_have_its_login_rotated(ManagedServiceType type)
    {
        DatabaseGrantSql.Rotate(type, "host", 5432, "harbora", "shop", "hb_reader", "secret123")
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData(ManagedServiceType.Redis)]
    [InlineData(ManagedServiceType.MongoDb)]
    public void An_engine_with_no_login_to_rotate_produces_no_statement(ManagedServiceType type)
    {
        DatabaseGrantSql.Rotate(type, "host", 6379, "harbora", "shop", "hb_reader", "secret123")
            .Should().BeNull();
    }

    [Fact]
    public void A_rotation_outside_the_alphabet_produces_no_statement_at_all()
    {
        // The new password goes into the statement as a literal, exactly like the first one did.
        DatabaseGrantSql.Rotate(ManagedServiceType.PostgreSql, "host", 5432, "harbora", "shop",
            "hb_reader", "secret'; DROP ROLE admin; --").Should().BeNull();

        DatabaseGrantSql.Rotate(ManagedServiceType.PostgreSql, "host", 5432, "harbora", "shop",
            "bob\"; DROP TABLE users; --", "secret123").Should().BeNull();
    }

    /// <summary>
    /// The opposite of <c>Drop</c>. A rotation that silently did nothing would hand somebody a
    /// password no database will accept, while the one they wanted retired kept working.
    ///
    /// <para>
    /// <b>What this test cannot prove, and what proves it.</b> "Fails loudly" is the one property of
    /// these statements that is not the same on both engines, and only half of it is pinned here.
    /// For PostgreSQL it is written into the argv — <c>ON_ERROR_STOP=1</c> and no <c>IF EXISTS</c> —
    /// so it is asserted directly. For MySQL/MariaDB there is no such flag: <c>mariadb -e</c> exits
    /// non-zero when its statement errors, which is the client's documented behaviour and not
    /// something a statement string can carry. What can be asserted is the absence of an
    /// <c>IF EXISTS</c> that would remove the error there was to fail on. The other half needs a
    /// live engine: issue a grant, drop the login behind Harbora's back, rotate, and require
    /// <c>DatabaseGrantExecutor.RotateAsync</c> to come back not-Ok. Nothing on this machine can run
    /// that (no Docker daemon), so it is named rather than faked.
    /// </para>
    /// </summary>
    [Fact]
    public void Rotating_a_login_that_is_not_there_has_to_fail_rather_than_pass_quietly()
    {
        var command = string.Join(" ", DatabaseGrantSql
            .Rotate(ManagedServiceType.PostgreSql, "host", 5432, "harbora", "shop", "hb_reader", "secret123")!
            .Command);

        command.Should().Contain("ON_ERROR_STOP");
        command.Should().NotContain("IF EXISTS");

        foreach (var type in new[] { ManagedServiceType.MySql, ManagedServiceType.MariaDb })
        {
            string.Join(" ", DatabaseGrantSql
                    .Rotate(type, "host", 3306, "harbora", "shop", "hb_reader", "secret123")!.Command)
                .Should().NotContain("IF EXISTS",
                    "the client aborts on error by itself, but an IF EXISTS would remove the error "
                    + "there was to abort on");
        }
    }

    [Fact]
    public void A_rotation_changes_the_password_and_nothing_else()
    {
        // Privileges are not re-granted and nothing is dropped: this login is live, and a rotation
        // that recreated it would cut every open session and lose whatever it owned.
        foreach (var type in new[] { ManagedServiceType.PostgreSql, ManagedServiceType.MariaDb })
        {
            var command = string.Join(" ", DatabaseGrantSql
                .Rotate(type, "host", 5432, "harbora", "shop", "hb_reader", "secret123")!.Command);

            command.Should().Contain("hb_reader");
            command.Should().Contain("secret123");
            command.Should().NotContain("CREATE USER");
            command.Should().NotContain("DROP");
        }
    }

    /// <summary>
    /// A rotation is one statement on every engine, and the caller's correctness rests on it.
    ///
    /// <para>
    /// <c>DatabaseAccessService.RotateAsync</c> reads a non-zero exit as "the ALTER did not take"
    /// and discards the new password on the strength of it. That is only sound while nothing can
    /// fail *after* the ALTER has succeeded. Create and Drop end with <c>FLUSH PRIVILEGES</c> and
    /// the rotation used to copy them for symmetry — a statement that changed nothing (an account
    /// statement updates the grant tables itself) but needed the RELOAD privilege to succeed, on a
    /// connection made as <c>harbora</c> rather than root. Wherever the ALTER was permitted and the
    /// reload was not, the batch exited non-zero with the password already changed, and an operator
    /// was told the database had refused. Symmetry is not worth that, so this asserts the shape
    /// rather than trusting the next person to remember why.
    /// </para>
    /// </summary>
    [Fact]
    public void A_rotation_is_a_single_statement_so_nothing_can_fail_after_it_has_worked()
    {
        foreach (var type in new[]
                 { ManagedServiceType.PostgreSql, ManagedServiceType.MySql, ManagedServiceType.MariaDb })
        {
            var rotate = DatabaseGrantSql.Rotate(type, "host", 5432, "harbora", "shop", "hb_reader", "secret123")!;
            var command = string.Join(" ", rotate.Command);

            command.Should().NotContain("FLUSH PRIVILEGES",
                "it cannot help, and it can fail after the password has already changed");

            // The claim is "one statement", so count statements, not mentions of ALTER. Counting
            // only the latter would pass an added -c "GRANT …;" pair, which re-breaks the caller's
            // argument in exactly the way FLUSH PRIVILEGES did. psql takes each statement as a -c
            // and the mariadb client takes its one as -e.
            rotate.Command.Count(a => a is "-c" or "-e").Should().Be(1,
                "a second statement could fail after the ALTER has already succeeded, and the "
                + "caller reads a non-zero exit as 'the password did not change'");

            rotate.Command[^1].TrimEnd().Should().EndWith("';",
                "the statement the client is given must end at the ALTER");
            rotate.Command.Count(a => a.Contains("ALTER USER", StringComparison.Ordinal))
                .Should().Be(1);
        }
    }

    [Fact]
    public void No_admin_password_is_ever_put_on_a_command_line()
    {
        // Visible in `docker inspect` and in the host's process list for as long as the container
        // runs. `mariadb -pSECRET` is the classic way that happens.
        foreach (var type in new[] { ManagedServiceType.PostgreSql, ManagedServiceType.MySql })
        {
            var create = DatabaseGrantSql.Create(type, "host", 5432, "harbora", "shop", "hb_reader", "secret123")!;
            string.Join(" ", create.Command).Should().NotContain("adminsecret");

            var rotate = DatabaseGrantSql.Rotate(type, "host", 5432, "harbora", "shop", "hb_reader", "secret123")!;
            string.Join(" ", rotate.Command).Should().NotContain("adminsecret");

            DatabaseGrantSql.Environment(type, "adminsecret").Values.Should().Contain("adminsecret");
        }
    }
}
