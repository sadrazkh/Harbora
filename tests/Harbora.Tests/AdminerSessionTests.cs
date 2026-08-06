using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The rules of a throwaway database-admin container.
///
/// It is a web interface with reach into a customer's private network, so every rule here is a way
/// of keeping it temporary and narrow: pinned by digest, offered only for engines it can actually
/// speak to, and expired on a clock somebody else supplies.
/// </summary>
public class AdminerSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_image_is_pinned_by_digest()
    {
        // A tag moves. This is the exact build that was looked at — and it was read from the
        // registry, never composed by hand, because a plausible-looking digest passes every test
        // here and fails on the server.
        AdminerSession.Image.Should().StartWith("adminer@sha256:");
        AdminerSession.Image.Should().NotContain(":latest");
        AdminerSession.Image.Split(':')[^1].Should().HaveLength(64);
    }

    [Theory]
    [InlineData(ManagedServiceType.PostgreSql, "pgsql")]
    [InlineData(ManagedServiceType.MySql, "server")]
    [InlineData(ManagedServiceType.MariaDb, "server")]
    public void The_engines_it_speaks_are_offered(ManagedServiceType type, string driver)
    {
        AdminerSession.DriverFor(type).Should().Be(driver);
        AdminerSession.Supports(type).Should().BeTrue();
    }

    [Theory]
    [InlineData(ManagedServiceType.Redis)]
    [InlineData(ManagedServiceType.MongoDb)]
    [InlineData(ManagedServiceType.RabbitMq)]
    [InlineData(ManagedServiceType.Nats)]
    public void The_engines_it_cannot_speak_are_refused(ManagedServiceType type)
    {
        // A button that opens a page saying "unknown driver" is worse than no button.
        AdminerSession.DriverFor(type).Should().BeNull();
        AdminerSession.Supports(type).Should().BeFalse();
    }

    [Fact]
    public void A_session_expires_after_its_hour()
    {
        var started = Now - AdminerSession.Lifetime;

        AdminerSession.Expired(started, Now).Should().BeTrue();
        AdminerSession.Expired(Now - TimeSpan.FromMinutes(59), Now).Should().BeFalse();
    }

    [Fact]
    public void An_unreadable_start_time_counts_as_expired()
    {
        // The sweeper labels a session it cannot read as DateTimeOffset.MinValue. For something
        // whose whole purpose is to be temporary, "unknown" must resolve to "remove it".
        AdminerSession.Expired(DateTimeOffset.MinValue, Now).Should().BeTrue();
    }

    [Fact]
    public void One_container_name_per_database_so_a_second_click_replaces_it()
    {
        var id = Guid.NewGuid();

        AdminerSession.ContainerName(id).Should().Be(AdminerSession.ContainerName(id));
        AdminerSession.ContainerName(id).Should().NotBe(AdminerSession.ContainerName(Guid.NewGuid()));
        AdminerSession.ContainerName(id).Should().StartWith("harbora-adminer-");
        AdminerSession.ContainerName(id).Length.Should().BeLessThanOrEqualTo(63, "Docker will not take a longer name");
    }
}
