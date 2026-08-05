using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The message brokers, and the catalogue they joined.
///
/// An environment that holds several apps and several databases usually holds a broker too, and
/// until now the only way to get one was to deploy it as an ordinary application and wire it up by
/// hand. Adding an engine is a new entry in the catalogue and no engine change — which is exactly
/// why the entry itself has to be checked: a mistake in it produces a service that provisions,
/// starts, and hands out a connection string nothing can connect to.
/// </summary>
public class ServiceCatalogBrokerTests
{
    private static readonly ServiceCreds Creds =
        new("harbora-svc-demo", 5672, "harbora", "s3cret", "demo");

    [Theory]
    [InlineData(ManagedServiceType.RabbitMq)]
    [InlineData(ManagedServiceType.Nats)]
    public void A_broker_is_in_the_catalogue(ManagedServiceType type)
    {
        ServiceCatalog.All.Should().ContainKey(type);
    }

    [Theory]
    [InlineData(ManagedServiceType.RabbitMq)]
    [InlineData(ManagedServiceType.Nats)]
    public void Every_engine_offers_at_least_one_version(ManagedServiceType type)
    {
        // The create path takes Versions[0] when nobody chooses. An empty list is an index out of
        // range at the moment somebody presses the button.
        ServiceCatalog.All[type].Versions.Should().NotBeEmpty();
    }

    [Fact]
    public void Every_engine_in_the_catalogue_is_complete()
    {
        // Swept rather than listed, so an engine added later cannot be half-filled in and shipped:
        // each of these is used unconditionally on the provisioning path.
        foreach (var (type, definition) in ServiceCatalog.All)
        {
            definition.ImageRepo.Should().NotBeNullOrWhiteSpace(type.ToString());
            definition.DataMountPath.Should().StartWith("/", type.ToString());
            definition.Port.Should().BeGreaterThan(0, type.ToString());
            definition.Versions.Should().NotBeEmpty(type.ToString());
            definition.DisplayName.Should().NotBeNullOrWhiteSpace(type.ToString());
        }
    }

    [Fact]
    public void A_broker_has_no_database_name()
    {
        // A queue is not a schema. Asking for one would put a field on the create form that means
        // nothing and gets written into a container that ignores it.
        ServiceCatalog.All[ManagedServiceType.RabbitMq].HasDatabaseName.Should().BeFalse();
        ServiceCatalog.All[ManagedServiceType.Nats].HasDatabaseName.Should().BeFalse();
    }

    [Fact]
    public void RabbitMq_is_started_with_the_credentials_it_hands_out()
    {
        // The connection string and the container's environment have to agree. They are built by
        // two different lambdas, which is exactly where they can drift apart.
        var env = ServiceCatalog.All[ManagedServiceType.RabbitMq].Env(Creds);
        var (full, _) = ServiceCatalog.All[ManagedServiceType.RabbitMq].Conn(Creds);

        env["RABBITMQ_DEFAULT_USER"].Should().Be(Creds.User);
        env["RABBITMQ_DEFAULT_PASS"].Should().Be(Creds.Password);
        full.Should().Contain(Creds.User).And.Contain(Creds.Password);
    }

    [Fact]
    public void Nats_is_started_with_the_credentials_it_hands_out()
    {
        // NATS takes them on the command line rather than the environment, so an entry that filled
        // in Env would start a broker with no authentication at all while advertising a password.
        var command = ServiceCatalog.All[ManagedServiceType.Nats].Command(Creds);

        command.Should().NotBeNull();
        command.Should().Contain(Creds.User).And.Contain(Creds.Password);
    }

    [Fact]
    public void Nats_keeps_its_messages_across_a_restart()
    {
        // JetStream is off unless asked for, and a broker that loses every message when its
        // container restarts is not what somebody adding one to an environment expects.
        var definition = ServiceCatalog.All[ManagedServiceType.Nats];

        definition.Command(Creds).Should().Contain("--jetstream");
        definition.Command(Creds).Should().Contain(definition.DataMountPath);
    }

    [Fact]
    public void The_masked_connection_string_does_not_carry_the_password()
    {
        foreach (var (type, definition) in ServiceCatalog.All)
        {
            var (_, masked) = definition.Conn(Creds);
            masked.Should().NotContain(Creds.Password, type.ToString());
        }
    }

    [Theory]
    [InlineData(ManagedServiceType.RabbitMq)]
    [InlineData(ManagedServiceType.Nats)]
    public void Attaching_a_broker_injects_something_an_app_can_use(ManagedServiceType type)
    {
        var attach = ServiceCatalog.All[type].AttachEnv(Creds);

        attach.Should().NotBeEmpty();
        attach.Values.Should().Contain(v => v.Contains(Creds.Host));
    }

    [Theory]
    [InlineData(ManagedServiceType.RabbitMq, "rabbitmq")]
    [InlineData(ManagedServiceType.Nats, "nats")]
    [InlineData(ManagedServiceType.PostgreSql, "postgres")]
    public void Each_engine_draws_its_own_mark(ManagedServiceType type, string expected)
    {
        // The fallback is a generic database mark, which is wrong for a broker and silently so.
        ServiceTypeKey.For(type).Should().Be(expected);
    }

    [Fact]
    public void A_broker_is_known_to_be_a_broker()
    {
        ServiceTypeKey.IsBroker(ManagedServiceType.RabbitMq).Should().BeTrue();
        ServiceTypeKey.IsBroker(ManagedServiceType.Nats).Should().BeTrue();
        ServiceTypeKey.IsBroker(ManagedServiceType.PostgreSql).Should().BeFalse();
    }

    [Theory]
    [InlineData(ManagedServiceType.RabbitMq)]
    [InlineData(ManagedServiceType.Nats)]
    public void What_a_broker_cannot_do_is_explained_rather_than_left_blank(ManagedServiceType type)
    {
        // A button that appears to work and does nothing is worse than one that is not offered —
        // and a control that is simply absent with no reason reads as a bug.
        Harbora.Infrastructure.Networking.ConnectionProbe.WhyUnsupported(type)
            .Should().NotBeNullOrWhiteSpace();
        CredentialRotationPlan.WhyUnsupported(type).Should().NotBeNullOrWhiteSpace();
    }
}
