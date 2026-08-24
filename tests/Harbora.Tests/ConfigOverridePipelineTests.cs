using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Configuration;
using Harbora.Domain.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// C2 (2026-08-22 config-delivery plan): the real <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/>
/// over the fake Docker engine and a fake in-memory container filesystem
/// (<see cref="FakeContainerConfigFileWriter"/>), the same shape <see cref="ConfigGroupPipelineTests"/>
/// already proved the env-merge seam with. What is asserted here is what the container's own file
/// actually received (<see cref="FakeContainerConfigFileWriter.Writes"/>), never a helper's return
/// value — and that a failing rule fails the whole deployment with an actionable message rather than
/// shipping the placeholder.
/// </summary>
public class ConfigOverridePipelineTests
{
    private static ConfigOverrideRule GivenRule(
        PipelineHarness h, string filePath, string keyPath, ConfigOverrideValueKind kind,
        string? literalValue = null, string? secretPlaintext = null, string? serviceAlias = null,
        bool unpublished = true, int order = 0)
    {
        var rule = new ConfigOverrideRule
        {
            AppId = h.App.Id,
            FilePath = filePath,
            KeyPath = keyPath,
            ValueKind = kind,
            LiteralValue = literalValue,
            EncryptedSecretValue = secretPlaintext is null ? null : h.Protector.Protect(secretPlaintext),
            AttachedServiceAlias = serviceAlias,
            HasUnpublishedChanges = unpublished,
            Order = order
        };
        h.Db.ConfigOverrideRules.Add(rule);
        h.Db.SaveChanges();
        return rule;
    }

    private const string AppSettings = """
        {
          "ConnectionStrings": {
            "Default": "REPLACE_ME"
          }
        }
        """;

    [Fact]
    public async Task A_literal_rule_reaches_the_actual_file_the_container_receives()
    {
        using var h = new PipelineHarness();
        h.ConfigFiles.SeedFile("/app/appsettings.json", AppSettings);
        GivenRule(h, "/app/appsettings.json", "ConnectionStrings:Default", ConfigOverrideValueKind.Literal,
            literalValue: "Host=db;Database=app");

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var write = h.ConfigFiles.Writes.Should().ContainSingle().Which;
        write.Path.Should().Be("/app/appsettings.json");
        write.Content.Should().Contain("Host=db;Database=app");
    }

    [Fact]
    public async Task A_secret_rule_reaches_the_file_decrypted()
    {
        using var h = new PipelineHarness();
        h.ConfigFiles.SeedFile("/app/appsettings.json", AppSettings);
        GivenRule(h, "/app/appsettings.json", "ConnectionStrings:Default", ConfigOverrideValueKind.Secret,
            secretPlaintext: "Host=db;Password=s3cret");

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.ConfigFiles.Writes.Should().ContainSingle().Which.Content.Should().Contain("Host=db;Password=s3cret");
    }

    [Fact]
    public async Task An_attached_service_alias_resolves_through_the_seam_C1_fills_in()
    {
        using var h = new PipelineHarness();
        h.ConfigFiles.SeedFile("/app/appsettings.json", AppSettings);
        h.ServiceResolver.Seed(h.App.Id, "orders", "Host=managed-db;Password=rotated");
        GivenRule(h, "/app/appsettings.json", "ConnectionStrings:Default",
            ConfigOverrideValueKind.AttachedServiceConnectionString, serviceAlias: "orders");

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.ConfigFiles.Writes.Should().ContainSingle().Which.Content.Should().Contain("Host=managed-db;Password=rotated");
    }

    [Fact]
    public async Task An_unresolved_service_alias_fails_the_deployment_with_the_resolvers_own_reason()
    {
        using var h = new PipelineHarness();
        h.ConfigFiles.SeedFile("/app/appsettings.json", AppSettings);
        // Not seeded in ServiceResolver — simulates a database that was detached after the rule was made.
        GivenRule(h, "/app/appsettings.json", "ConnectionStrings:Default",
            ConfigOverrideValueKind.AttachedServiceConnectionString, serviceAlias: "orders");

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("no attachment named 'orders'");
        h.ConfigFiles.Writes.Should().BeEmpty("a failing rule must never write a partially-applied file");
    }

    [Fact]
    public async Task A_missing_key_path_fails_the_deployment_and_names_the_files_real_keys()
    {
        using var h = new PipelineHarness();
        h.ConfigFiles.SeedFile("/app/appsettings.json", AppSettings);
        GivenRule(h, "/app/appsettings.json", "ConnectionStrings:DoesNotExist", ConfigOverrideValueKind.Literal,
            literalValue: "x");

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("ConnectionStrings:DoesNotExist");
        result.ErrorMessage.Should().Contain("ConnectionStrings:Default", "the file's real key paths must be shown");
    }

    [Fact]
    public async Task A_missing_file_fails_the_deployment_and_shows_the_directory_listing()
    {
        using var h = new PipelineHarness();
        h.ConfigFiles.SeedDirectory("/app", "Program.cs", "appsettings.Development.json");
        GivenRule(h, "/app/appsettings.json", "ConnectionStrings:Default", ConfigOverrideValueKind.Literal,
            literalValue: "x");

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("/app/appsettings.json");
        result.ErrorMessage.Should().Contain("appsettings.Development.json", "what is actually in the directory must be shown");
    }

    [Fact]
    public async Task A_secret_value_never_appears_in_the_stored_failure_message()
    {
        using var h = new PipelineHarness();
        h.ConfigFiles.SeedFile("/app/appsettings.json", AppSettings);
        GivenRule(h, "/app/appsettings.json", "ConnectionStrings:Missing", ConfigOverrideValueKind.Secret,
            secretPlaintext: "super-secret-password");

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().NotContain("super-secret-password");
    }

    [Fact]
    public async Task A_successful_deploy_clears_the_unpublished_flag_on_the_rule()
    {
        using var h = new PipelineHarness();
        h.ConfigFiles.SeedFile("/app/appsettings.json", AppSettings);
        var rule = GivenRule(h, "/app/appsettings.json", "ConnectionStrings:Default", ConfigOverrideValueKind.Literal,
            literalValue: "x", unpublished: true);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var stored = await h.Db.ConfigOverrideRules.AsNoTracking().FirstAsync(x => x.Id == rule.Id);
        stored.HasUnpublishedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_deployment_leaves_the_unpublished_flag_set()
    {
        using var h = new PipelineHarness();
        h.ConfigFiles.SeedFile("/app/appsettings.json", AppSettings);
        var rule = GivenRule(h, "/app/appsettings.json", "ConnectionStrings:Missing", ConfigOverrideValueKind.Literal,
            literalValue: "x", unpublished: true);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        var stored = await h.Db.ConfigOverrideRules.AsNoTracking().FirstAsync(x => x.Id == rule.Id);
        stored.HasUnpublishedChanges.Should().BeTrue("nothing actually shipped with this rule applied");
    }

    [Fact]
    public async Task Rolling_back_still_applies_the_rules_current_value_because_it_is_never_baked_into_the_image()
    {
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        h.ConfigFiles.SeedFile("/app/appsettings.json", AppSettings);
        var rule = GivenRule(h, "/app/appsettings.json", "ConnectionStrings:Default", ConfigOverrideValueKind.Literal,
            literalValue: "v1", unpublished: false);

        // The rule changes after v1 shipped — same as editing it for real in the panel.
        rule.LiteralValue = "v2";
        rule.HasUnpublishedChanges = true;
        h.Db.SaveChanges();

        var rollback = h.QueueDeployment(number: 2, rollbackTo: h.App.ActiveDeploymentId);
        var result = await h.RunAsync(rollback);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        h.ConfigFiles.Writes.Should().ContainSingle().Which.Content.Should().Contain("v2",
            "unlike the image, a rule is resolved fresh at run time regardless of which image is running");

        var stored = await h.Db.ConfigOverrideRules.AsNoTracking().FirstAsync(x => x.Id == rule.Id);
        stored.HasUnpublishedChanges.Should().BeFalse("the rollback's container really was built with v2's value");
    }

    [Fact]
    public async Task A_rule_targeting_an_app_on_a_remote_node_fails_with_an_actionable_reason_instead_of_silently_skipping()
    {
        using var h = new PipelineHarness(localServer: false);
        h.ConfigFiles.SeedFile("/app/appsettings.json", AppSettings);
        GivenRule(h, "/app/appsettings.json", "ConnectionStrings:Default", ConfigOverrideValueKind.Literal,
            literalValue: "x");

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("remote node");
        h.ConfigFiles.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rule_on_a_compose_app_fails_the_deployment_instead_of_being_silently_never_applied()
    {
        using var h = new PipelineHarness();
        h.WithComposeFile("""
            services:
              web:
                image: nginx:alpine
                ports:
                  - "8080:80"
            """);
        GivenRule(h, "/app/appsettings.json", "ConnectionStrings:Default", ConfigOverrideValueKind.Literal,
            literalValue: "x");

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        result.ErrorMessage.Should().Contain("Compose",
            "a rule that cannot actually be applied to a compose stack must fail loudly, not be quietly ignored");
        h.ConfigFiles.Writes.Should().BeEmpty();
    }
}
