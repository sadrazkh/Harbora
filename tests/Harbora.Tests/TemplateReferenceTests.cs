using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;
using Harbora.Infrastructure.Templates;
using Xunit;

namespace Harbora.Tests;

public class TemplateReferenceTests
{
    [Fact]
    public void A_connection_value_can_be_embedded_in_a_larger_variable()
    {
        var values = TemplateReferences.For("mariadb",
            new ServiceCreds("harbora-svc-blog-db", 3306, "harbora", "secret", "blog"));

        var result = TemplateReferences.Resolve(
            "mysql://${{mariadb.user}}:${{mariadb.password}}@${{mariadb.host}}:${{mariadb.port}}/${{mariadb.database}}",
            values,
            out var missing);

        result.Should().Be("mysql://harbora:secret@harbora-svc-blog-db:3306/blog");
        missing.Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_reference_is_not_silently_erased()
    {
        var result = TemplateReferences.Resolve("${{postgres.host}}:${{redis.port}}",
            new Dictionary<string, string> { ["postgres.host"] = "db" }, out var missing);

        result.Should().Be("db:${{redis.port}}");
        missing.Should().ContainSingle().Which.Should().Be("redis.port");
    }

    [Theory]
    [InlineData("postgres", ManagedServiceType.PostgreSql)]
    [InlineData("mariadb", ManagedServiceType.MariaDb)]
    [InlineData("mongodb", ManagedServiceType.MongoDb)]
    public void Catalog_service_names_map_to_real_engines(string value, ManagedServiceType expected)
    {
        TemplateDeploymentService.ParseServiceType(value).Should().Be(expected);
    }
}
