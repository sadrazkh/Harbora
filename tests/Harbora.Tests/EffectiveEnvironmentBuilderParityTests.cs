using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Email;
using Harbora.Domain.Services;
using Harbora.Domain.Storage;
using Harbora.Infrastructure.Apps;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 4.1 (2026-09-04 local-dev-parity plan) — the requirement its own task brief states directly: "the
/// pulled environment is byte-identical to what a deploy would inject for the same app". Both
/// <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline.BuildEnv"/> and
/// <c>ApiV1Controller.Env</c> (the endpoint behind <c>harbora env pull</c>/<c>harbora run</c>) now call
/// the same <see cref="EffectiveEnvironmentBuilder.Compute"/>; this proves that shared call really does
/// yield identical values to what a real deployment injects into a real (fake) container, across every
/// attachment kind at once — a config group, a storage bucket, an email provider, and a managed
/// database — not merely that the two call sites happen to look alike.
///
/// <para>
/// Deliberately does not hand-write an expected dictionary to compare against: the assertion is
/// entries computed independently by <see cref="EffectiveEnvironmentBuilder.Compute"/> against
/// <see cref="Fakes.FakeDockerEngine.RunRequests"/> — what <see cref="PipelineHarness.RunAsync"/>'s real
/// <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/> actually gave the container —
/// exactly the "not against a hand-written expectation" instruction in the task brief.
/// </para>
/// </summary>
public class EffectiveEnvironmentBuilderParityTests
{
    [Fact]
    public async Task The_effective_environment_a_pull_would_return_is_byte_identical_to_what_the_container_receives()
    {
        using var h = new PipelineHarness();

        // The app's own variables: one plain, one secret.
        h.Db.EnvironmentVariables.Add(new EnvironmentVariable
        {
            AppId = h.App.Id, Key = "APP_NAME", Value = "blog", IsSecret = false
        });
        h.Db.EnvironmentVariables.Add(new EnvironmentVariable
        {
            AppId = h.App.Id, Key = "APP_SECRET", Value = h.Protector.Protect("own-secret"), IsSecret = true
        });

        // A config group.
        var group = new ConfigGroup { WorkspaceId = h.Workspace.Id, Name = "shared" };
        h.Db.ConfigGroups.Add(group);
        h.Db.ConfigGroupEntries.Add(new ConfigGroupEntry
        {
            ConfigGroupId = group.Id, Key = "LOG_LEVEL", Value = "debug", IsSecret = false
        });
        h.Db.AppConfigGroups.Add(new AppConfigGroup { AppId = h.App.Id, ConfigGroupId = group.Id, AttachOrder = 1 });

        // A storage bucket.
        var bucket = new StorageBucket
        {
            WorkspaceId = h.Workspace.Id, Name = "uploads", AccessKey = "AKIATEST",
            EncryptedSecretKey = h.Protector.Protect("bucket-secret"), Status = BucketStatus.Ready
        };
        h.Db.StorageBuckets.Add(bucket);
        h.Db.AppStorageBuckets.Add(new AppStorageBucket { AppId = h.App.Id, StorageBucketId = bucket.Id, AttachOrder = 1 });

        // An email provider.
        var provider = new EmailProvider
        {
            WorkspaceId = h.Workspace.Id, Name = "SendGrid", Host = "smtp.sendgrid.net", Port = 587,
            Username = "apikey", EncryptedPassword = h.Protector.Protect("smtp-secret"),
            FromAddress = "noreply@acme.example", FromName = "Acme", UseSsl = true
        };
        h.Db.EmailProviders.Add(provider);
        h.Db.AppEmailProviders.Add(new AppEmailProvider { AppId = h.App.Id, EmailProviderId = provider.Id, AttachOrder = 1 });

        // A managed database.
        var svc = new ManagedService
        {
            WorkspaceId = h.Workspace.Id, EnvironmentId = h.Environment.Id, ServerId = h.Server.Id,
            Name = "orders", Type = ManagedServiceType.PostgreSql, Version = "16-alpine",
            ContainerName = "harbora-svc-orders", InternalPort = 5432,
            Username = "harbora", EncryptedPassword = h.Protector.Protect("db-secret"),
            DatabaseName = "orders", VolumeName = "harbora-svc-orders-data", Status = ServiceStatus.Running
        };
        h.Db.ManagedServices.Add(svc);
        h.Db.AppManagedServices.Add(new AppManagedService
        {
            AppId = h.App.Id, ManagedServiceId = svc.Id, Alias = "ORDERS", AttachOrder = 1
        });

        h.Db.SaveChanges();

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);
        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var containerEnv = h.Docker.RunRequests.Should().ContainSingle().Which.Env;

        // Independently — the exact shape ApiV1Controller.Env loads — reload the app fresh with the
        // same Includes and decrypt secrets the same way BuildEnv does.
        var app = await h.Db.Apps
            .Include(a => a.EnvironmentVariables)
            .Include(a => a.ConfigGroups).ThenInclude(cg => cg.ConfigGroup!).ThenInclude(g => g.Entries)
            .Include(a => a.StorageBuckets).ThenInclude(sb => sb.StorageBucket)
            .Include(a => a.EmailProviders).ThenInclude(ep => ep.EmailProvider)
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.ManagedService)
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.Database)
            .AsNoTracking()
            .FirstAsync(a => a.Id == h.App.Id);

        var merged = EffectiveEnvironmentBuilder.Compute(app, h.Protector, h.StorageOptions.CustomerEndpoint);
        var pulled = merged.ToDictionary(e => e.Key, e => e.IsSecret ? h.Protector.Unprotect(e.Value) : e.Value);

        pulled.Should().BeEquivalentTo(containerEnv,
            "harbora env pull must hand back exactly what this deployment actually injected — not a " +
            "second implementation of the same idea that happens to look similar");

        // Named checks so a future change that breaks one attachment kind fails on the specific key,
        // not just on "the dictionaries differ somewhere".
        pulled.Should().ContainKey("APP_NAME").WhoseValue.Should().Be("blog");
        pulled.Should().ContainKey("APP_SECRET").WhoseValue.Should().Be("own-secret");
        pulled.Should().ContainKey("LOG_LEVEL").WhoseValue.Should().Be("debug");
        pulled.Should().ContainKey("S3_ACCESS_KEY").WhoseValue.Should().Be("AKIATEST");
        pulled.Should().ContainKey("SMTP_HOST").WhoseValue.Should().Be("smtp.sendgrid.net");
        pulled.Should().ContainKey("ORDERS_DATABASE_URL");
    }
}
