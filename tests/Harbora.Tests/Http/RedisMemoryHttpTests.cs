using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 1.6 (redis-eviction) end to end — the real pipeline routes, a real cookie, real Razor, against the
/// panel's shared <c>FakeDockerEngine</c>. Proves the same facts <c>RedisMemoryPolicyEngineTests</c>
/// proves at the engine layer, but through the actual controller a person's browser reaches: the
/// route exists, is gated the way the rest of the database's page is, and the page tells the truth
/// about what happened rather than showing one uniform "saved" banner.
///
/// <para>
/// <b>The panel renders Persian by default in tests</b> — assertions below read <c>data-</c>
/// attributes (<c>data-redis-current-policy</c>, <c>data-redis-policy-unpublished</c>,
/// <c>data-spec-error</c>) and stored rows, never English sentences.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class RedisMemoryHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private ManagedService SeedRedis(
        string name, ServiceStatus status = ServiceStatus.Running, long memoryLimitBytes = 0)
    {
        var protector = Panel.Resolve<ISecretProtector>();
        var service = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(), Name = name, Type = ManagedServiceType.Redis,
            Version = "7-alpine", Status = status, ContainerName = "harbora-svc-" + name,
            InternalPort = 6379, Username = "harbora", DatabaseName = "",
            VolumeName = "harbora-svc-" + name + "-data",
            EncryptedPassword = protector.Protect("redis-http-password-01"),
            MemoryLimitBytes = memoryLimitBytes
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        return service;
    }

    [Fact]
    public async Task Choosing_a_policy_on_a_running_instance_applies_it_live_and_marks_it_unpublished()
    {
        var svc = SeedRedis("http-cache-live");
        Panel.GivenUser(fixture.WorkspaceId, "redis-live@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.220", "redis-live@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/redis-memory", token,
            ("evictionPolicy", "allkeys-lru"), ("maxMemoryMb", "64"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        // Panel.Docker is shared across the whole HTTP collection, so its request log is cumulative
        // — matched by this container's own auth, not just by ContainSingle over everything anyone
        // else in the collection has ever asked it to do.
        var configSets = Panel.Docker.OneOffRequests.Where(r =>
            r.Env is not null && r.Env.GetValueOrDefault("REDISCLI_AUTH") == "redis-http-password-01"
            && string.Join(' ', r.Command).Contains("CONFIG SET maxmemory-policy")).ToList();
        configSets.Should().ContainSingle("the change must actually reach the running instance, not only the row");

        var stored = Panel.Read(db => db.ManagedServices.Single(s => s.Id == svc.Id));
        stored.RedisEvictionPolicy.Should().Be("allkeys-lru");
        stored.RedisMaxMemoryBytes.Should().Be(64L * 1024 * 1024);
        stored.HasUnpublishedChanges.Should().BeTrue(
            "a live CONFIG SET does not survive a plain restart until the container is rebuilt");

        var html = await (await client.GetAsync($"/databases/{svc.Id}")).Content.ReadAsStringAsync();
        html.Should().Contain("data-redis-current-policy=\"allkeys-lru\"");
        html.Should().Contain("data-redis-policy-unpublished",
            "the page must say the live change has not yet reached the container's own launch command");
    }

    [Fact]
    public async Task Choosing_a_policy_on_a_stopped_instance_never_calls_the_engine_live()
    {
        var svc = SeedRedis("http-cache-stopped", ServiceStatus.Stopped);
        Panel.GivenUser(fixture.WorkspaceId, "redis-stopped@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.221", "redis-stopped@example.com");

        // Panel.Docker is shared across the whole HTTP collection, so its call log is cumulative —
        // a delta, not an absolute "empty" check, is what proves THIS request reached nothing live.
        var oneOffsBefore = Panel.Docker.Calls.Count(c => c.Operation == "RunOneOffAsync");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/redis-memory", token,
            ("evictionPolicy", "noeviction"), ("maxMemoryMb", "0"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Docker.Calls.Count(c => c.Operation == "RunOneOffAsync").Should().Be(oneOffsBefore,
            "a stopped container has nothing to reach live");

        var stored = Panel.Read(db => db.ManagedServices.Single(s => s.Id == svc.Id));
        stored.RedisEvictionPolicy.Should().Be("noeviction");
        stored.HasUnpublishedChanges.Should().BeTrue("saved, but only a rebuild makes it real");
    }

    [Fact]
    public async Task An_out_of_range_cap_is_refused_and_the_row_is_left_untouched()
    {
        // 100 MB container ceiling — RedisMemoryPolicy.Ceiling leaves 80 MB usable, so 200 must refuse.
        var svc = SeedRedis("http-cache-refused", memoryLimitBytes: 100L * 1024 * 1024);
        Panel.GivenUser(fixture.WorkspaceId, "redis-refused@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.222", "redis-refused@example.com");
        var oneOffsBefore = Panel.Docker.Calls.Count(c => c.Operation == "RunOneOffAsync");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/redis-memory", token,
            ("evictionPolicy", "allkeys-lru"), ("maxMemoryMb", "200"));

        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        html.Should().Contain("data-spec-error=", "a refusal must render the page's own error banner");

        Panel.Docker.Calls.Count(c => c.Operation == "RunOneOffAsync").Should().Be(oneOffsBefore,
            "a refused request must not reach the engine at all");
        var stored = Panel.Read(db => db.ManagedServices.Single(s => s.Id == svc.Id));
        stored.RedisEvictionPolicy.Should().BeNull("a refused request must change nothing on the row");
        stored.HasUnpublishedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task An_untouched_redis_instance_shows_no_policy_chosen()
    {
        var svc = SeedRedis("http-cache-untouched");
        Panel.GivenUser(fixture.WorkspaceId, "redis-untouched@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.223", "redis-untouched@example.com");

        var html = await (await client.GetAsync($"/databases/{svc.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-redis-no-policy");
        html.Should().NotContain("data-redis-current-policy=");
        html.Should().NotContain("data-redis-policy-unpublished");
    }

    [Fact]
    public async Task A_non_redis_database_never_shows_the_eviction_policy_panel()
    {
        var protector = Panel.Resolve<ISecretProtector>();
        var svc = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(), Name = "http-postgres-no-panel", Type = ManagedServiceType.PostgreSql,
            Version = "16-alpine", Status = ServiceStatus.Running, ContainerName = "harbora-svc-http-postgres-no-panel",
            InternalPort = 5432, Username = "harbora", DatabaseName = "http_postgres_no_panel",
            VolumeName = "harbora-svc-http-postgres-no-panel-data",
            EncryptedPassword = protector.Protect("redis-http-password-02")
        };
        Panel.Seed(db => db.ManagedServices.Add(svc));
        Panel.GivenUser(fixture.WorkspaceId, "redis-non-redis@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.224", "redis-non-redis@example.com");

        var html = await (await client.GetAsync($"/databases/{svc.Id}")).Content.ReadAsStringAsync();

        html.Should().NotContain("data-redis-memory-form", "eviction policy means nothing for an engine that is not Redis");
    }
}
