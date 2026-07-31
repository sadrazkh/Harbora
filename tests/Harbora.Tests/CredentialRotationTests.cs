using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Changing a database password without locking anyone out.
///
/// The engines genuinely differ here, and the honest shape of this feature is to say so: SQL engines
/// change it live, Redis reads it from its own command line and can only be given a new one by
/// starting again, and MongoDB's shell changed name between the two versions the catalog offers.
/// </summary>
public class CredentialRotationTests
{
    private static readonly ServiceCreds Current =
        new("harbora-svc-shop-db", 5432, "shop", "oldpassword12", "shopdata");

    [Fact]
    public void Postgres_changes_the_password_of_the_user_that_is_connecting()
    {
        var plan = CredentialRotationPlan.For(ManagedServiceType.PostgreSql, Current, "newpassword34")!;
        var command = string.Join(" ", plan.Command);

        command.Should().Contain("ALTER USER");
        command.Should().Contain("newpassword34");
    }

    [Fact]
    public void The_current_password_is_never_written_on_the_command_line()
    {
        // A command line is readable by every process on the host. The new one has to appear in the
        // statement itself; the old one has no reason to.
        foreach (var type in new[] { ManagedServiceType.PostgreSql, ManagedServiceType.MySql, ManagedServiceType.MariaDb })
        {
            var plan = CredentialRotationPlan.For(type, Current, "newpassword34")!;

            string.Join(" ", plan.Command).Should().NotContain("oldpassword12", $"{type} leaks the old password");
            plan.Env.Values.Should().Contain("oldpassword12", $"{type} must still be able to authenticate");
        }
    }

    [Fact]
    public void A_postgres_rotation_stops_at_the_first_error()
    {
        // Otherwise psql reports success after the statement failed, the stored password is updated
        // to one the database never accepted, and every attached app loses its connection at once.
        string.Join(" ", CredentialRotationPlan.For(ManagedServiceType.PostgreSql, Current, "newpassword34")!.Command)
            .Should().Contain("ON_ERROR_STOP=1");
    }

    [Fact]
    public void Mysql_reloads_its_privileges_so_the_change_takes_effect()
    {
        string.Join(" ", CredentialRotationPlan.For(ManagedServiceType.MySql, Current, "newpassword34")!.Command)
            .Should().Contain("FLUSH PRIVILEGES");
    }

    [Fact]
    public void Redis_is_recognised_as_needing_a_restart_rather_than_a_statement()
    {
        // It reads the password from its own command line, so there is nothing to alter while it runs.
        CredentialRotationPlan.For(ManagedServiceType.Redis, Current, "newpassword34").Should().BeNull();
        CredentialRotationPlan.RequiresRecreate(ManagedServiceType.Redis).Should().BeTrue();
        CredentialRotationPlan.RequiresRecreate(ManagedServiceType.PostgreSql).Should().BeFalse();
    }

    [Fact]
    public void Mongodb_says_it_is_not_supported_instead_of_pretending()
    {
        // A button that appears to work and does nothing is worse than one that is not offered.
        CredentialRotationPlan.For(ManagedServiceType.MongoDb, Current, "newpassword34").Should().BeNull();
        CredentialRotationPlan.RequiresRecreate(ManagedServiceType.MongoDb).Should().BeFalse();
        CredentialRotationPlan.WhyUnsupported(ManagedServiceType.MongoDb).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_engine_that_can_be_rotated_offers_no_excuse_for_not_being()
    {
        // The guard on the message above: an explanation shown next to a working button is noise.
        CredentialRotationPlan.WhyUnsupported(ManagedServiceType.PostgreSql).Should().BeNull();
        CredentialRotationPlan.WhyUnsupported(ManagedServiceType.Redis).Should().BeNull();
    }

    [Theory]
    [InlineData("Abcdef123456", true)]
    [InlineData("short1", false)]
    [InlineData("has'quote1234", false)]
    [InlineData("has space1234", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_password_that_survives_a_shell_and_a_statement_is_applied(string? password, bool expected)
    {
        // Generated passwords are alphanumeric. This is the check that keeps that true, rather than a
        // comment claiming it — a quote in the wrong place would end the statement early.
        CredentialRotationPlan.IsSafeToApply(password).Should().Be(expected);
    }
}

/// <summary>
/// The passwords Harbora generates for databases.
/// </summary>
public class ServiceCredentialTests
{
    [Fact]
    public void A_generated_password_is_always_safe_to_put_in_a_statement()
    {
        // Not asserted once: the rule is a property of every password this will ever produce, and a
        // single quote landing in ALTER USER … PASSWORD '…' ends the statement early.
        for (var i = 0; i < 200; i++)
            CredentialRotationPlan.IsSafeToApply(ServiceCredentials.Generate())
                .Should().BeTrue("every generated password must survive a shell and a statement");
    }

    [Fact]
    public void Two_passwords_are_not_the_same()
    {
        ServiceCredentials.Generate().Should().NotBe(ServiceCredentials.Generate());
    }

    [Fact]
    public void Characters_that_are_read_wrong_out_loud_are_left_out()
    {
        // These get dictated over the phone and typed by hand.
        var sample = string.Concat(Enumerable.Range(0, 50).Select(_ => ServiceCredentials.Generate()));

        sample.Should().NotContain("l").And.NotContain("I").And.NotContain("0").And.NotContain("O");
    }
}
