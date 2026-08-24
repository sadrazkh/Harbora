using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Configuration;
using Harbora.Infrastructure.Configuration;
using Harbora.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// C2 (2026-08-22 config-delivery plan): <see cref="ConfigOverrideResolver.PreviewAsync"/> is the
/// "validate a rule against the deployed app before deploying" half — read the file, resolve the key
/// path, show the current value and what it would become, without writing anything. Exercised
/// directly against the resolver (rather than through the panel's HTTP action) because the container
/// it reads is already a plain id string by the time it gets here — resolving which container that
/// is for a live app is <c>AppsController.ConfigOverrides.cs</c>'s own concern, unit-testable on its
/// own terms, and unrelated to what this class does with the id once it has one.
/// </summary>
public class ConfigOverrideResolverPreviewTests
{
    private static ConfigOverrideResolver NewResolver(
        FakeContainerConfigFileWriter files, PassthroughProtector protector, FakeAttachedServiceConnectionStringResolver serviceResolver) =>
        new(new ConfigFileEditorFactory(), files, protector, serviceResolver, NullLogger<ConfigOverrideResolver>.Instance);

    private static App NewApp() => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), ServerId = Guid.NewGuid(),
        EnvironmentId = Guid.NewGuid(), Name = "api", Slug = "api"
    };

    private const string AppSettings = """
        {
          "ConnectionStrings": {
            "Default": "REPLACE_ME"
          }
        }
        """;

    [Fact]
    public async Task A_literal_rule_previews_its_current_and_would_become_values_in_plaintext()
    {
        var files = new FakeContainerConfigFileWriter().SeedFile("/app/appsettings.json", AppSettings);
        var resolver = NewResolver(files, new PassthroughProtector(), new FakeAttachedServiceConnectionStringResolver());
        var app = NewApp();
        var rule = new ConfigOverrideRule
        {
            AppId = app.Id, FilePath = "/app/appsettings.json", KeyPath = "ConnectionStrings:Default",
            ValueKind = ConfigOverrideValueKind.Literal, LiteralValue = "Host=db;Database=app"
        };

        var preview = await resolver.PreviewAsync(app, rule, "container-1", default);

        preview.Ok.Should().BeTrue();
        preview.CurrentValue.Should().Be("REPLACE_ME");
        preview.WouldBecomeValue.Should().Be("Host=db;Database=app");
        preview.WouldBecomeIsSecret.Should().BeFalse();
        files.Writes.Should().BeEmpty("previewing must never write anything");
    }

    [Fact]
    public async Task A_secret_rules_would_become_value_is_masked_never_shown_in_plaintext()
    {
        var files = new FakeContainerConfigFileWriter().SeedFile("/app/appsettings.json", AppSettings);
        var protector = new PassthroughProtector();
        var resolver = NewResolver(files, protector, new FakeAttachedServiceConnectionStringResolver());
        var app = NewApp();
        var rule = new ConfigOverrideRule
        {
            AppId = app.Id, FilePath = "/app/appsettings.json", KeyPath = "ConnectionStrings:Default",
            ValueKind = ConfigOverrideValueKind.Secret, EncryptedSecretValue = protector.Protect("super-secret")
        };

        var preview = await resolver.PreviewAsync(app, rule, "container-1", default);

        preview.Ok.Should().BeTrue();
        preview.CurrentValue.Should().Be("REPLACE_ME");
        preview.WouldBecomeIsSecret.Should().BeTrue();
        preview.WouldBecomeValue.Should().BeNull("a secret's real value must never reach the preview payload");
    }

    [Fact]
    public async Task An_attached_service_alias_previews_its_resolved_connection_string_masked()
    {
        var files = new FakeContainerConfigFileWriter().SeedFile("/app/appsettings.json", AppSettings);
        var app = NewApp();
        var serviceResolver = new FakeAttachedServiceConnectionStringResolver()
            .Seed(app.Id, "orders", "Host=managed-db;Password=rotated");
        var resolver = NewResolver(files, new PassthroughProtector(), serviceResolver);
        var rule = new ConfigOverrideRule
        {
            AppId = app.Id, FilePath = "/app/appsettings.json", KeyPath = "ConnectionStrings:Default",
            ValueKind = ConfigOverrideValueKind.AttachedServiceConnectionString, AttachedServiceAlias = "orders"
        };

        var preview = await resolver.PreviewAsync(app, rule, "container-1", default);

        preview.Ok.Should().BeTrue();
        preview.WouldBecomeIsSecret.Should().BeTrue();
        preview.WouldBecomeValue.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_key_path_previews_as_a_named_failure_with_the_current_value_still_shown()
    {
        var files = new FakeContainerConfigFileWriter().SeedFile("/app/appsettings.json", AppSettings);
        var resolver = NewResolver(files, new PassthroughProtector(), new FakeAttachedServiceConnectionStringResolver());
        var app = NewApp();
        var rule = new ConfigOverrideRule
        {
            AppId = app.Id, FilePath = "/app/appsettings.json", KeyPath = "ConnectionStrings:Missing",
            ValueKind = ConfigOverrideValueKind.Literal, LiteralValue = "x"
        };

        var preview = await resolver.PreviewAsync(app, rule, "container-1", default);

        preview.Ok.Should().BeFalse();
        preview.Failure.Should().NotBeNull();
        preview.Failure!.Reason.Should().Be(ConfigOverrideFailureReason.KeyPathNotFound);
        preview.Failure.Detail.Should().Contain("ConnectionStrings:Default");
    }

    [Fact]
    public async Task A_missing_file_previews_as_a_named_failure_showing_the_directory_listing()
    {
        var files = new FakeContainerConfigFileWriter().SeedDirectory("/app", "Program.cs");
        var resolver = NewResolver(files, new PassthroughProtector(), new FakeAttachedServiceConnectionStringResolver());
        var app = NewApp();
        var rule = new ConfigOverrideRule
        {
            AppId = app.Id, FilePath = "/app/appsettings.json", KeyPath = "ConnectionStrings:Default",
            ValueKind = ConfigOverrideValueKind.Literal, LiteralValue = "x"
        };

        var preview = await resolver.PreviewAsync(app, rule, "container-1", default);

        preview.Ok.Should().BeFalse();
        preview.Failure!.Reason.Should().Be(ConfigOverrideFailureReason.FileNotFound);
        preview.Failure.Detail.Should().Contain("Program.cs");
    }
}
