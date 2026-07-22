using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Template manifest → deploy-spec resolution (C3 / ADR-014). Pins which of the seeded templates
/// are one-click deployable and which return an honest "not yet" instead of throwing NotSupported.
/// </summary>
public class TemplateResolverTests
{
    [Fact]
    public void Image_template_is_deployable_as_image()
    {
        var spec = TemplateResolver.Resolve("""{"image":"nginx:alpine","port":80}""");
        spec.Kind.Should().Be(TemplateResolver.TemplateKind.Image);
        spec.Image.Should().Be("nginx:alpine");
        spec.Port.Should().Be(80);
    }

    [Fact]
    public void Git_template_is_deployable_from_git()
    {
        var spec = TemplateResolver.Resolve("""{"source":"git","port":3000}""");
        spec.Kind.Should().Be(TemplateResolver.TemplateKind.Git);
        spec.Port.Should().Be(3000);
    }

    [Fact]
    public void Service_template_is_a_managed_service()
    {
        var spec = TemplateResolver.Resolve("""{"service":"postgres","image":"postgres:16-alpine","port":5432}""");
        spec.Kind.Should().Be(TemplateResolver.TemplateKind.ManagedService);
        spec.Reason.Should().Contain("Databases");
    }

    [Fact]
    public void Multi_service_template_is_unsupported_with_reason()
    {
        // WordPress requires MariaDB — not one-click yet, but must not throw NotSupported.
        var spec = TemplateResolver.Resolve("""{"image":"wordpress:php8.3-apache","port":80,"requires":["mariadb"]}""");
        spec.Kind.Should().Be(TemplateResolver.TemplateKind.Unsupported);
        spec.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Invalid_or_empty_manifest_is_unsupported_not_thrown()
    {
        TemplateResolver.Resolve("not json").Kind.Should().Be(TemplateResolver.TemplateKind.Unsupported);
        TemplateResolver.Resolve("").Kind.Should().Be(TemplateResolver.TemplateKind.Unsupported);
        TemplateResolver.Resolve("{}").Kind.Should().Be(TemplateResolver.TemplateKind.Unsupported);
    }
}
