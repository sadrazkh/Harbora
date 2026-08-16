using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The variable a framework can actually open a connection with.
///
/// Attaching a database wrote `DATABASE_URL`, `PGHOST`, `PGUSER` and friends — the names the
/// Postgres tooling and most scripting runtimes read. A .NET application reads none of them: it
/// asks configuration for `ConnectionStrings:Something`, falls back to whatever appsettings.json
/// holds, and in a container that is `Host=localhost`. The app then starts, fails every query, and
/// the deployment dies at the health check with a stack trace about Npgsql — which names neither
/// the database nor the attach that was supposed to supply it. That happened to a real app on
/// 2026-08-16.
///
/// `DATABASE_URL` cannot be reused for this: it is a URI, and ADO.NET providers only parse
/// keyword=value. So an attach writes one more variable in the shape .NET expects, and the
/// application needs no code change at all.
/// </summary>
public class AttachConnectionStringTests
{
    private static readonly ServiceCreds Creds = new("orders-db", 5432, "appuser", "s3cret", "orders");

    private static Dictionary<string, string> AttachEnvFor(ManagedServiceType type, int port) =>
        ServiceCatalog.All[type].AttachEnv(Creds with { Port = port });

    [Fact]
    public void Postgres_hands_over_a_string_Npgsql_can_parse()
    {
        var env = AttachEnvFor(ManagedServiceType.PostgreSql, 5432);

        env["DATABASE_DSN"].Should().Be(
            "Host=orders-db;Port=5432;Database=orders;Username=appuser;Password=s3cret");
    }

    [Fact]
    public void Dot_net_configuration_finds_it_without_the_app_being_changed()
    {
        // .NET maps `__` to `:`, so this variable overrides ConnectionStrings:DefaultConnection in
        // appsettings.json. That is the whole point: no code change, no rebuild, no rewritten file.
        var env = AttachEnvFor(ManagedServiceType.PostgreSql, 5432);

        env["ConnectionStrings__DefaultConnection"].Should().Be(env["DATABASE_DSN"]);
    }

    [Theory]
    [InlineData(ManagedServiceType.MySql)]
    [InlineData(ManagedServiceType.MariaDb)]
    public void MySql_and_MariaDb_use_the_keywords_their_own_provider_reads(ManagedServiceType type)
    {
        // MySqlConnector wants Server/User ID, not Host/Username. Writing the Postgres spelling here
        // would produce a variable that looks right on the screen and throws at the first open.
        var env = AttachEnvFor(type, 3306);

        env["DATABASE_DSN"].Should().Be(
            "Server=orders-db;Port=3306;Database=orders;User ID=appuser;Password=s3cret");
        env["ConnectionStrings__DefaultConnection"].Should().Be(env["DATABASE_DSN"]);
    }

    [Theory]
    [InlineData(ManagedServiceType.Redis)]
    [InlineData(ManagedServiceType.MongoDb)]
    [InlineData(ManagedServiceType.RabbitMq)]
    [InlineData(ManagedServiceType.Nats)]
    public void Services_with_no_ADO_provider_are_left_alone(ManagedServiceType type)
    {
        // Redis and a broker are reached by URL, and there is no ConnectionStrings key anybody would
        // bind them to. Inventing one would put a password under a name nothing reads.
        var env = ServiceCatalog.All[type].AttachEnv(Creds);

        env.Should().NotContainKey("DATABASE_DSN");
        env.Should().NotContainKey("ConnectionStrings__DefaultConnection");
    }

    [Fact]
    public void The_names_everything_already_reads_are_still_written()
    {
        // Adding a variable must not cost an application that already reads the old ones.
        var env = AttachEnvFor(ManagedServiceType.PostgreSql, 5432);

        env.Should().ContainKeys("DATABASE_URL", "PGHOST", "PGPORT", "PGUSER", "PGPASSWORD", "PGDATABASE");
    }

    [Fact]
    public void A_second_database_gets_its_own_prefixed_copy_of_the_new_name_too()
    {
        // The collision rule is the reason AttachKeys exists; a new name has to obey it or the
        // second Postgres silently takes the first one's connection string.
        var wanted = AttachEnvFor(ManagedServiceType.PostgreSql, 5432);
        var taken = wanted.ToDictionary(w => w.Key, w => (string?)"somebody-elses-value");

        var final = AttachKeys.For(wanted, taken, "reporting");

        final.Should().ContainKey("REPORTING_ConnectionStrings__DefaultConnection");
        final.Should().NotContainKey("ConnectionStrings__DefaultConnection",
            "the plain name belongs to the database that claimed it first");
    }
}
