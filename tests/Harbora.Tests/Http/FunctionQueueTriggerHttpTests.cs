using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F2's editor UI, through real requests: the Queue trigger's own fields, the broker dropdown never
/// offering another workspace's RabbitMQ service, the honest throughput copy, the broker-error banner,
/// and the dead-letter list with its discard action.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class FunctionQueueTriggerHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private sealed record World(Guid AppId, Guid FunctionId, Guid BrokerId);

    private World GivenQueueFunctionApp(string slug, string? queueLastError = null)
    {
        var appId = Guid.CreateVersion7();
        var functionId = Guid.CreateVersion7();
        var brokerId = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.FeatureGrants.Add(new FeatureGrant
            {
                Scope = FeatureScope.Workspace, TargetId = fixture.WorkspaceId,
                FeatureKey = PlatformFeatures.Functions, State = FeatureState.Enabled
            });

            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
                Name = slug, Slug = slug, SourceType = AppSourceType.InlineCode,
                FunctionRuntime = FunctionRuntime.JavaScript,
                DockerfilePath = "Dockerfile.harbora"
            });

            db.ManagedServices.Add(new ManagedService
            {
                Id = brokerId, WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
                ServerId = Guid.CreateVersion7(), Name = slug + "-broker", Type = ManagedServiceType.RabbitMq,
                Version = "4-management-alpine", ContainerName = slug + "-broker-c", VolumeName = slug + "-broker-v",
                Username = "guest", EncryptedPassword = "enc", Status = ServiceStatus.Running
            });

            db.FunctionDefinitions.Add(new FunctionDefinition
            {
                Id = functionId, AppId = appId, WorkspaceId = fixture.WorkspaceId,
                Name = "consume-orders", Slug = "consume-orders", Trigger = FunctionTrigger.Queue,
                QueueServiceId = brokerId, QueueName = "orders",
                QueueLastError = queueLastError,
                Code = "export default async () => {};"
            });
        });

        return new World(appId, functionId, brokerId);
    }

    private static async Task<IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    [Fact]
    public async Task The_editor_offers_a_queue_trigger_and_its_own_field_block()
    {
        var world = GivenQueueFunctionApp("fn-queue-render");
        Panel.GivenUser(fixture.WorkspaceId, "fn-queue-render@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.160", "fn-queue-render@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector($"option[value='{(int)FunctionTrigger.Queue}']").Should().NotBeNull(
            "the trigger dropdown must offer Queue alongside HTTP/Schedule/Event");
        document.QuerySelector($"[data-when='{(int)FunctionTrigger.Queue}']").Should().NotBeNull(
            "the Queue trigger needs its own field block, the same idiom every other trigger already gets");
        document.QuerySelector("[name='QueueName']").Should().NotBeNull();
        document.QuerySelector("[name='QueueServiceId']").Should().NotBeNull();
    }

    [Fact]
    public async Task The_broker_dropdown_never_offers_another_workspaces_rabbitmq_service()
    {
        var world = GivenQueueFunctionApp("fn-queue-tenancy");
        var otherWorkspaceId = Guid.CreateVersion7();
        var otherBrokerId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Workspaces.Add(new Workspace { Id = otherWorkspaceId, Name = "Other", Slug = "other-ws" });
            db.ManagedServices.Add(new ManagedService
            {
                Id = otherBrokerId, WorkspaceId = otherWorkspaceId, EnvironmentId = Guid.CreateVersion7(),
                ServerId = Guid.CreateVersion7(), Name = "not-mine", Type = ManagedServiceType.RabbitMq,
                Version = "4-management-alpine", ContainerName = "other-broker-c", VolumeName = "other-broker-v",
                Username = "guest", EncryptedPassword = "enc", Status = ServiceStatus.Running
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "fn-queue-tenancy@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.161", "fn-queue-tenancy@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector($"option[value='{world.BrokerId}']").Should().NotBeNull(
            "this workspace's own broker must be offered");
        document.QuerySelector($"option[value='{otherBrokerId}']").Should().BeNull(
            "a broker belonging to another workspace must never appear in this dropdown");
    }

    [Fact]
    public async Task The_editor_states_the_throughput_ceiling_plainly()
    {
        var world = GivenQueueFunctionApp("fn-queue-throughput");
        Panel.GivenUser(fixture.WorkspaceId, "fn-queue-throughput@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.162", "fn-queue-throughput@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-queue-throughput-note]").Should().NotBeNull(
            "the plan requires stating the one-panel-side-consumer ceiling plainly, not letting it be discovered under load");
    }

    [Fact]
    public async Task A_broken_broker_connection_shows_its_own_banner()
    {
        var world = GivenQueueFunctionApp("fn-queue-broken", queueLastError: "connection refused");
        Panel.GivenUser(fixture.WorkspaceId, "fn-queue-broken@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.163", "fn-queue-broken@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-queue-broker-error]").Should().NotBeNull();
    }

    [Fact]
    public async Task A_healthy_broker_connection_shows_no_error_banner()
    {
        var world = GivenQueueFunctionApp("fn-queue-healthy");
        Panel.GivenUser(fixture.WorkspaceId, "fn-queue-healthy@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.164", "fn-queue-healthy@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-queue-broker-error]").Should().BeNull();
    }

    [Fact]
    public async Task Saving_a_valid_queue_trigger_persists_its_broker_and_queue_name()
    {
        var world = GivenQueueFunctionApp("fn-queue-save");
        Panel.GivenUser(fixture.WorkspaceId, "fn-queue-save@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.165", "fn-queue-save@example.com");

        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");
        var response = await client.PostFormAsync(
            $"/functions/{world.AppId}/save", token,
            ("functionId", world.FunctionId.ToString()),
            ("Name", "consume-orders"), ("Trigger", ((int)FunctionTrigger.Queue).ToString()),
            ("QueueServiceId", world.BrokerId.ToString()), ("QueueName", "orders.v2"),
            ("Code", "export default async () => {};"), ("IsEnabled", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var stored = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        stored.Trigger.Should().Be(FunctionTrigger.Queue);
        stored.QueueServiceId.Should().Be(world.BrokerId);
        stored.QueueName.Should().Be("orders.v2");
    }

    [Fact]
    public async Task Saving_a_queue_trigger_pointed_at_another_workspaces_broker_is_refused()
    {
        var world = GivenQueueFunctionApp("fn-queue-cross-tenant");
        var otherWorkspaceId = Guid.CreateVersion7();
        var otherBrokerId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Workspaces.Add(new Workspace { Id = otherWorkspaceId, Name = "Other2", Slug = "other-ws-2" });
            db.ManagedServices.Add(new ManagedService
            {
                Id = otherBrokerId, WorkspaceId = otherWorkspaceId, EnvironmentId = Guid.CreateVersion7(),
                ServerId = Guid.CreateVersion7(), Name = "not-mine-2", Type = ManagedServiceType.RabbitMq,
                Version = "4-management-alpine", ContainerName = "other-broker-c2", VolumeName = "other-broker-v2",
                Username = "guest", EncryptedPassword = "enc", Status = ServiceStatus.Running
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "fn-queue-cross-tenant@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.166", "fn-queue-cross-tenant@example.com");

        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");
        var response = await client.PostFormAsync(
            $"/functions/{world.AppId}/save", token,
            ("functionId", world.FunctionId.ToString()),
            ("Name", "consume-orders"), ("Trigger", ((int)FunctionTrigger.Queue).ToString()),
            ("QueueServiceId", otherBrokerId.ToString()), ("QueueName", "orders"),
            ("Code", "export default async () => {};"), ("IsEnabled", "true"));

        // Refused, not silently accepted: the redisplay is a 200 (the same view, with the error), not
        // a redirect — and the stored row must still point at this workspace's own original broker.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        stored.QueueServiceId.Should().Be(world.BrokerId);
    }

    // ------------------------------------------------------------- dead letters

    [Fact]
    public async Task An_empty_dead_letter_list_says_so_rather_than_rendering_nothing()
    {
        var world = GivenQueueFunctionApp("fn-queue-dl-empty");
        Panel.GivenUser(fixture.WorkspaceId, "fn-queue-dl-empty@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.167", "fn-queue-dl-empty@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-dead-letters-empty]").Should().NotBeNull();
        document.QuerySelector("[data-dead-letters]").Should().BeNull();
    }

    [Fact]
    public async Task A_parked_dead_letter_is_listed_and_can_be_discarded()
    {
        var world = GivenQueueFunctionApp("fn-queue-dl");
        var deadLetterId = Guid.CreateVersion7();
        Panel.Seed(db => db.FunctionQueueDeadLetters.Add(new FunctionQueueDeadLetter
        {
            Id = deadLetterId, FunctionId = world.FunctionId, AppId = world.AppId, WorkspaceId = fixture.WorkspaceId,
            QueueName = "orders", Body = "{\"order\":42}", Reason = "The function answered 500."
        }));
        Panel.GivenUser(fixture.WorkspaceId, "fn-queue-dl@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.168", "fn-queue-dl@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());
        document.QuerySelector($"[data-dead-letter-id='{deadLetterId}']").Should().NotBeNull();

        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");
        var response = await client.PostFormAsync(
            $"/functions/{world.AppId}/{world.FunctionId}/deadletters/{deadLetterId}/discard", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.FunctionQueueDeadLetters.AsNoTracking().Any(d => d.Id == deadLetterId))
            .Should().BeFalse("discarding removes the row — there is no other act offered on a dead letter");
    }
}
