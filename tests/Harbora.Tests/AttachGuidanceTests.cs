using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What the application page is allowed to say about an attach.
///
/// The screen has to name variables, and the one thing it must never do is name them from a list
/// somebody kept by hand — a screen that confidently points at a variable nothing sets is worse
/// than a screen that says nothing. So the names come back out of the catalog, and these tests pin
/// both halves: that the list is real, and that no value ever rides along with it.
/// </summary>
public class AttachGuidanceTests
{
    [Fact]
    public void A_relational_database_reports_the_connection_string_it_writes()
    {
        AttachGuidance.KeysFor(ManagedServiceType.PostgreSql)
            .Should().Contain([AttachGuidance.DsnKey, AttachGuidance.DotNetKey, "DATABASE_URL", "PGHOST"]);

        AttachGuidance.WritesConnectionString(ManagedServiceType.PostgreSql).Should().BeTrue();
    }

    [Theory]
    [InlineData(ManagedServiceType.Redis)]
    [InlineData(ManagedServiceType.RabbitMq)]
    [InlineData(ManagedServiceType.Nats)]
    public void A_service_reached_by_URL_does_not_claim_to_write_one(ManagedServiceType type)
    {
        AttachGuidance.WritesConnectionString(type).Should().BeFalse();
        AttachGuidance.KeysFor(type).Should().NotContain(AttachGuidance.DotNetKey);
    }

    [Fact]
    public void Redis_still_reports_the_names_it_does_write()
    {
        // "Nothing to say" is not the same as "no variables". The screen should still be able to
        // tell somebody that REDIS_URL is what appeared.
        AttachGuidance.KeysFor(ManagedServiceType.Redis).Should().Contain("REDIS_URL");
    }

    [Fact]
    public void No_credential_ever_travels_with_the_names()
    {
        // The guidance is rendered on a page that anybody with read access to the app can open.
        // Returning keys only is the whole reason this class exists rather than the raw dictionary.
        foreach (var type in Enum.GetValues<ManagedServiceType>())
        foreach (var key in AttachGuidance.KeysFor(type))
        {
            key.Should().NotContain("password", "a key is a name, and a name is not a secret");
            key.Should().NotContain("://", "that would be a value, not a name");
        }
    }

    [Fact]
    public void Every_service_type_in_the_catalog_can_be_asked()
    {
        // A new service added to the catalog must not make the page throw.
        foreach (var type in Enum.GetValues<ManagedServiceType>())
            AttachGuidance.KeysFor(type).Should().NotBeEmpty($"{type} writes something on attach");
    }
}
