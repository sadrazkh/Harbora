using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Templates;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What a template's "kind" and a required secret actually do to the app <see cref="TemplateDeploymentService"/>
/// creates — the two additions the Telegram-bot and Kavenegar templates needed and nothing built
/// before them exercised.
///
/// Before "kind" existed, every template-created app was silently <see cref="ServiceKind.Web"/>: fine
/// for a WordPress site, wrong for a long-polling bot, which would have been given a public domain
/// nothing ever routes real traffic to and a health check waiting forever for an HTTP response the
/// bot never sends. Before "required" existed, a secret with no default was always auto-generated —
/// fine for an application key, silently wrong for a third-party credential like a Kavenegar API key,
/// which would have deployed an app authenticating with a random string the provider never issued.
/// </summary>
public class TemplateKindAndRequiredSecretDeploymentTests
{
    private sealed class AllowAll : IQuotaService
    {
        public Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<QuotaCheck> CanAddAppAsync(Guid w, string? size, Guid? exclude, CancellationToken ct) =>
            Task.FromResult(QuotaCheck.Ok);

        public Task<QuotaCheck> CanAddServiceAsync(Guid w, string? size, CancellationToken ct) =>
            Task.FromResult(QuotaCheck.Ok);
    }

    private sealed class RecordingDeployments : IDeploymentEngine
    {
        public Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct) =>
            Task.FromResult(Guid.CreateVersion7());

        public Task CancelAsync(Guid deploymentId, CancellationToken ct) => Task.CompletedTask;
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(HarboraDbContext Db, TemplateDeploymentService Service, Guid WorkspaceId, AppTemplate Template);

    private static Fixture Build(string manifestJson)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("template-kind-" + Guid.NewGuid()).Options);

        db.Add(new Server { Id = Guid.CreateVersion7(), Name = "local", IsLocal = true, Architecture = "amd64" });

        db.Settings.Add(new Harbora.Domain.Settings.Setting
        {
            Key = Harbora.Domain.Settings.SettingKeys.PlatformRootDomain, Value = "apps.example.test"
        });

        var template = new AppTemplate
        {
            Id = Guid.CreateVersion7(),
            Key = "demo", Name = "Demo", Category = "automation",
            IsBuiltIn = true, IsEnabled = true, Status = TemplateStatus.Approved,
            ManifestJson = manifestJson
        };
        db.Add(template);
        db.SaveChanges();

        var service = new TemplateDeploymentService(
            db,
            new ProjectService(db, new FixedClock(Now)),
            new AllowAll(),
            new PassthroughProtector(),
            new FakeManagedServiceEngine(),
            new RecordingDeployments(),
            new Harbora.Infrastructure.Billing.ResourceCreationBilling(
                db, new FixedClock(Now), Microsoft.Extensions.Options.Options.Create(
                    new Harbora.Infrastructure.Billing.BillingOptions { Enabled = false })),
            new AppAddressAssigner(db, new ConfigurationBuilder().Build()));

        return new Fixture(db, service, Guid.CreateVersion7(), template);
    }

    private static TemplateDeployRequest Request(Fixture f, IReadOnlyDictionary<string, string>? variables = null) =>
        new(f.WorkspaceId, Guid.CreateVersion7(), f.Template.Id, "Demo", "Demo",
            RepositoryUrl: "https://github.com/example/demo.git", GitRef: null,
            Variables: variables ?? new Dictionary<string, string>(), DeployNow: false);

    [Fact]
    public async Task A_worker_template_deploys_an_app_of_kind_worker()
    {
        var f = Build("""{"source":"git","kind":"worker","env":[{"key":"TELEGRAM_BOT_TOKEN","secret":true,"required":true}]}""");

        var result = await f.Service.DeployAsync(
            Request(f, new Dictionary<string, string> { ["TELEGRAM_BOT_TOKEN"] = "123:abc" }), CancellationToken.None);

        var app = await f.Db.Apps.Include(a => a.Domains).SingleAsync(a => a.Id == result.AppId);
        app.Kind.Should().Be(ServiceKind.Worker);
    }

    [Fact]
    public async Task A_worker_template_gets_no_public_domain()
    {
        // The point of "no public exposure at all": a long-polling bot answers no HTTP, so a domain
        // pointed at it would be a certificate for a service that never responds.
        var f = Build("""{"source":"git","kind":"worker","env":[{"key":"TELEGRAM_BOT_TOKEN","secret":true,"required":true}]}""");

        var result = await f.Service.DeployAsync(
            Request(f, new Dictionary<string, string> { ["TELEGRAM_BOT_TOKEN"] = "123:abc" }), CancellationToken.None);

        var app = await f.Db.Apps.Include(a => a.Domains).SingleAsync(a => a.Id == result.AppId);
        app.Domains.Should().BeEmpty();
    }

    [Fact]
    public async Task A_template_with_no_kind_still_deploys_web_with_a_domain()
    {
        // The regression guard: every template that shipped before "kind" existed must keep getting
        // exactly what it always got.
        var f = Build("""{"image":"nginx:alpine","port":80}""");

        var result = await f.Service.DeployAsync(Request(f), CancellationToken.None);

        var app = await f.Db.Apps.Include(a => a.Domains).SingleAsync(a => a.Id == result.AppId);
        app.Kind.Should().Be(ServiceKind.Web);
        app.Domains.Should().ContainSingle();
    }

    [Fact]
    public async Task A_required_secret_left_blank_refuses_the_deploy_by_name()
    {
        var f = Build("""{"source":"git","kind":"worker","env":[{"key":"TELEGRAM_BOT_TOKEN","secret":true,"required":true}]}""");

        var act = () => f.Service.DeployAsync(Request(f), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*TELEGRAM_BOT_TOKEN*");
    }

    [Fact]
    public async Task A_supplied_required_secret_is_stored_encrypted_and_marked_secret()
    {
        var f = Build("""{"source":"git","kind":"worker","env":[{"key":"TELEGRAM_BOT_TOKEN","secret":true,"required":true}]}""");

        var result = await f.Service.DeployAsync(
            Request(f, new Dictionary<string, string> { ["TELEGRAM_BOT_TOKEN"] = "123:abc-real-token" }), CancellationToken.None);

        var app = await f.Db.Apps.Include(a => a.EnvironmentVariables).SingleAsync(a => a.Id == result.AppId);
        var variable = app.EnvironmentVariables.Should().ContainSingle(v => v.Key == "TELEGRAM_BOT_TOKEN").Subject;
        variable.IsSecret.Should().BeTrue();

        // PassthroughProtector marks what it "encrypted" rather than truly encrypting it, but a
        // required secret must still go through Protect — the same seam a real ISecretProtector
        // uses — never stored as the raw value the person typed.
        variable.Value.Should().NotBe("123:abc-real-token");
    }
}
