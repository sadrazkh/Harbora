using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Email;
using Harbora.Domain.ErrorTracking;
using Harbora.Domain.Services;
using Harbora.Domain.Storage;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A scheduled run is handed what a deployed container of the same app is handed.
///
/// <para>
/// It used to be handed the variables typed on the environment page and nothing else, so a nightly
/// job attached to a database never saw <c>DATABASE_URL</c> while the job's own page listed it. A
/// backup script that cannot find its database fails in the one way that looks like a bug in the
/// script. Every assertion here is against what <see cref="FakeDockerEngine"/> received, from the
/// real pipeline on one side and the real runner on the other, never against a list written out by
/// hand: the thing being pinned is that the two agree, whatever either of them gives.
/// </para>
/// </summary>
public class CronJobEnvironmentTests
{
    /// <summary>
    /// One of every attachment kind the merge knows, each carrying a secret, on the harness's app.
    /// </summary>
    private static void AttachEverything(PipelineHarness h)
    {
        h.Db.EnvironmentVariables.AddRange(
            new EnvironmentVariable { AppId = h.App.Id, Key = "APP_NAME", Value = "blog" },
            new EnvironmentVariable
            {
                AppId = h.App.Id, Key = "APP_SECRET", Value = h.Protector.Protect("own-secret"), IsSecret = true
            });

        var group = new ConfigGroup { WorkspaceId = h.Workspace.Id, Name = "shared" };
        h.Db.ConfigGroups.Add(group);
        h.Db.ConfigGroupEntries.Add(new ConfigGroupEntry { ConfigGroupId = group.Id, Key = "LOG_LEVEL", Value = "debug" });
        h.Db.AppConfigGroups.Add(new AppConfigGroup { AppId = h.App.Id, ConfigGroupId = group.Id, AttachOrder = 1 });

        var bucket = new StorageBucket
        {
            WorkspaceId = h.Workspace.Id, Name = "uploads", AccessKey = "AKIATEST",
            EncryptedSecretKey = h.Protector.Protect("bucket-secret"), Status = BucketStatus.Ready
        };
        h.Db.StorageBuckets.Add(bucket);
        h.Db.AppStorageBuckets.Add(new AppStorageBucket { AppId = h.App.Id, StorageBucketId = bucket.Id, AttachOrder = 1 });

        var mail = new EmailProvider
        {
            WorkspaceId = h.Workspace.Id, Name = "SendGrid", Host = "smtp.sendgrid.net", Port = 587,
            Username = "apikey", EncryptedPassword = h.Protector.Protect("smtp-secret"),
            FromAddress = "noreply@acme.example", FromName = "Acme", UseSsl = true
        };
        h.Db.EmailProviders.Add(mail);
        h.Db.AppEmailProviders.Add(new AppEmailProvider { AppId = h.App.Id, EmailProviderId = mail.Id, AttachOrder = 1 });

        var database = Postgres(h, "orders");
        var replica = Postgres(h, "orders-replica");
        replica.PrimaryManagedServiceId = database.Id;
        h.Db.ManagedServices.AddRange(database, replica);
        h.Db.AppManagedServices.Add(new AppManagedService
        {
            AppId = h.App.Id, ManagedServiceId = database.Id, Alias = "ORDERS", AttachOrder = 1
        });

        var tracker = new ErrorTrackingProvider
        {
            WorkspaceId = h.Workspace.Id, Name = "sentry", EncryptedDsn = h.Protector.Protect("https://key@sentry.example/1")
        };
        h.Db.ErrorTrackingProviders.Add(tracker);
        h.Db.AppErrorTrackingProviders.Add(new AppErrorTrackingProvider
        {
            AppId = h.App.Id, ErrorTrackingProviderId = tracker.Id, AttachOrder = 1
        });

        h.Db.SaveChanges();
    }

    private static ManagedService Postgres(PipelineHarness h, string name) => new()
    {
        WorkspaceId = h.Workspace.Id, EnvironmentId = h.Environment.Id, ServerId = h.Server.Id,
        Name = name, Type = ManagedServiceType.PostgreSql, Version = "16-alpine",
        ContainerName = $"harbora-svc-{name}", InternalPort = 5432,
        Username = "harbora", EncryptedPassword = h.Protector.Protect($"{name}-password"),
        DatabaseName = "orders", VolumeName = $"harbora-svc-{name}-data", Status = ServiceStatus.Running
    };

    /// <summary>Deploys the harness's app as <paramref name="kind"/> and returns what its container was given.</summary>
    private static async Task<Dictionary<string, string>> DeployedEnvironmentAsync(PipelineHarness h, ServiceKind kind)
    {
        h.App.Kind = kind;
        h.Db.SaveChanges();

        var result = await h.RunAsync(h.QueueDeployment(number: 1));
        result.Status.Should().Be(DeploymentStatus.Succeeded);

        return new Dictionary<string, string>(h.Docker.RunRequests.Should().ContainSingle().Which.Env);
    }

    /// <summary>
    /// Turns the same app into a scheduled job, as nothing in the product does, so that the only
    /// difference between the two sides is which code built the environment.
    /// </summary>
    private static void BecomeScheduled(PipelineHarness h)
    {
        using var db = h.OpenAnotherContext();
        var app = db.Apps.Single(a => a.Id == h.App.Id);
        app.Kind = ServiceKind.Cron;
        app.CronExpression = "0 3 * * *";
        app.Command = "backup.sh";
        db.SaveChanges();
    }

    private static CronJobRunner Runner(PipelineHarness h, HarboraDbContext db) => new(
        db, new OneEngine(h.Docker), h.Protector,
        new Harbora.Infrastructure.Billing.BillingGate(
            db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions())),
        Options.Create(h.Options), Options.Create(h.StorageOptions), h.Clock,
        NullLogger<CronJobRunner>.Instance);

    private sealed class OneEngine(FakeDockerEngine engine) : IServerEngineFactory
    {
        public IDockerEngine Local => engine;
        public Task<IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct) => Task.FromResult<IDockerEngine>(engine);
    }

    /// <summary>"Run now", as the job queue reaches it: by id, on a context of its own.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> RunNowAsync(PipelineHarness h)
    {
        await using var db = h.OpenAnotherContext();
        await Runner(h, db).RunAsync(h.App.Id, default);
        return h.Docker.OneOffRequests.Should().ContainSingle().Which.Env!;
    }

    [Fact]
    public async Task Run_now_is_handed_exactly_what_a_deployed_worker_of_the_same_app_is()
    {
        // A worker, because it is the long-running kind that takes no traffic: like a scheduled job it
        // is told no PORT, so the two environments must be identical, key for key.
        using var h = new PipelineHarness();
        AttachEverything(h);
        var container = await DeployedEnvironmentAsync(h, ServiceKind.Worker);
        BecomeScheduled(h);

        var run = await RunNowAsync(h);

        run.Should().BeEquivalentTo(container,
            "a scheduled run and a deployed container of the same app are handed one environment");

        // By name, so a regression points at the attachment kind it lost rather than at "the
        // dictionaries differ somewhere".
        run["APP_SECRET"].Should().Be("own-secret", "a secret reaches the job as its real value");
        run.Should().ContainKey("LOG_LEVEL", "an attached config group reaches the job");
        run.Should().ContainKey("S3_ACCESS_KEY", "an attached bucket reaches the job");
        run.Should().ContainKey("SMTP_HOST", "an attached email provider reaches the job");
        run.Should().ContainKey("DATABASE_URL", "an attached database reaches the job");
        run.Should().ContainKey("ORDERS_DATABASE_URL", "under its alias too");
        run.Should().ContainKey("REPLICA_URL", "its running read replica rides along with it");
        run["SENTRY_DSN"].Should().Be("https://key@sentry.example/1", "an attached error tracker reaches the job");
    }

    [Fact]
    public async Task The_one_thing_a_web_container_has_that_a_scheduled_run_does_not_is_its_port()
    {
        using var h = new PipelineHarness().WithContainerPort(4000);
        AttachEverything(h);
        var container = await DeployedEnvironmentAsync(h, ServiceKind.Web);
        BecomeScheduled(h);

        var run = await RunNowAsync(h);

        container["PORT"].Should().Be("4000");
        run.Should().NotContainKey("PORT", "only something that takes traffic is told where traffic arrives");
        run.Should().BeEquivalentTo(container.Where(e => e.Key != "PORT").ToDictionary(e => e.Key, e => e.Value));
    }

    [Fact]
    public async Task The_schedule_and_run_now_hand_a_job_the_same_environment()
    {
        // The two ways in load the app themselves: the scheduler reads every job each minute and hands
        // over the one that is due, the queue loads one by id. Either loading less than the other is
        // the defect this file exists for, so both are driven here through their real entry points.
        using var h = new PipelineHarness();
        AttachEverything(h);
        BecomeScheduled(h);
        using (var db = h.OpenAnotherContext())
        {
            db.Apps.Single(a => a.Id == h.App.Id).NextRunAt = h.Clock.UtcNow.AddMinutes(-1);
            db.SaveChanges();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => h.OpenAnotherContext());
        services.AddSingleton<ISystemClock>(h.Clock);
        services.AddScoped(sp => Runner(h, sp.GetRequiredService<HarboraDbContext>()));
        await using var provider = services.BuildServiceProvider();

        await new CronRunner(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CronRunner>.Instance)
            .TickAsync(default);
        var scheduled = h.Docker.OneOffRequests.Should().ContainSingle().Which.Env!;

        h.Docker.OneOffRequests.Clear();
        var manual = await RunNowAsync(h);

        scheduled.Should().ContainKey("DATABASE_URL");
        manual.Should().BeEquivalentTo(scheduled);
    }
}
