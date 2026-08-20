using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F9 (2026-08-21 functions-and-services plan, decision 5): a workspace's own bring-your-own
/// Cloudflare token, proven the same way the platform's own token is in
/// <see cref="CloudflarePlatformServiceTests"/> — a fake handler at the HTTP seam, since no real
/// Cloudflare credential exists on this machine. A live round-trip against the real API remains
/// unproven locally.
///
/// <para>
/// The tenancy tests mirror <c>LogsControllerTenancyTests</c>'s own strongest shape: one proof with
/// the ambient <c>CustomerDnsCredential</c> query filter doing the work, and one with it deliberately
/// disabled (<see cref="SystemWorkspaceScope"/>) so the service's own explicit
/// <c>WorkspaceId ==</c> check is shown to isolate tenants on its own, not merely by accident of a
/// filter one layer down.
/// </para>
/// </summary>
public class CustomerCloudflareServiceTests
{
    private const string Key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
    private static readonly Guid WorkspaceA = Guid.NewGuid();
    private static readonly Guid WorkspaceB = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbName = "customer-dns-" + Guid.NewGuid();

    private HarboraDbContext AsTenant(Guid workspaceId) => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_dbName).Options,
        new FixedWorkspaceScope(workspaceId));

    private HarboraDbContext AsSystem() => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_dbName).Options,
        SystemWorkspaceScope.Instance);

    private static CustomerCloudflareService Build(HarboraDbContext db, RecordingCloudflareHandler handler) =>
        new(db, new CloudflareApiClient(new Factory(handler)), new AesGcmSecretProtector(Key), new Clock(),
            NullLogger<CustomerCloudflareService>.Instance);

    // ---- saving a token ----

    [Fact]
    public async Task Saving_a_token_verifies_it_live_and_stores_it_encrypted()
    {
        using var db = AsSystem();
        var handler = new RecordingCloudflareHandler();

        var result = await Build(db, handler).SaveTokenAsync(WorkspaceA, "cf-token-a", default);

        result.Success.Should().BeTrue();
        var row = await db.CustomerDnsCredentials.IgnoreQueryFilters().SingleAsync(c => c.WorkspaceId == WorkspaceA);
        row.EncryptedToken.Should().NotBe("cf-token-a");
        new AesGcmSecretProtector(Key).Unprotect(row.EncryptedToken).Should().Be("cf-token-a");
        row.LastVerifiedAt.Should().NotBeNull();
        row.LastVerificationError.Should().BeNull();
    }

    [Fact]
    public async Task A_token_Cloudflare_itself_rejects_is_refused_and_nothing_is_stored()
    {
        using var db = AsSystem();
        var handler = new RecordingCloudflareHandler();
        handler.InvalidTokens.Add("bad-token");

        var result = await Build(db, handler).SaveTokenAsync(WorkspaceA, "bad-token", default);

        result.Success.Should().BeFalse();
        (await db.CustomerDnsCredentials.IgnoreQueryFilters().AnyAsync(c => c.WorkspaceId == WorkspaceA))
            .Should().BeFalse("a token Cloudflare itself would not accept must leave nothing behind");
    }

    // ---- honest states ----

    [Fact]
    public async Task With_no_token_the_state_says_so_and_zones_are_refused_not_faked_empty()
    {
        using var db = AsSystem();
        var service = Build(db, new RecordingCloudflareHandler());

        (await service.GetStateAsync(WorkspaceA, default)).HasToken.Should().BeFalse();

        var zones = await service.ListZonesAsync(WorkspaceA, default);
        zones.Success.Should().BeFalse();
        zones.Zones.Should().BeEmpty();
        zones.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_token_that_cannot_list_zones_says_exactly_that_and_the_credential_records_it()
    {
        using var db = AsSystem();
        var handler = new RecordingCloudflareHandler();
        handler.TokensThatCannotListZones.Add("narrow-token");
        var service = Build(db, handler);
        (await service.SaveTokenAsync(WorkspaceA, "narrow-token", default)).Success.Should().BeTrue();

        var zones = await service.ListZonesAsync(WorkspaceA, default);

        zones.Success.Should().BeFalse();
        zones.Zones.Should().BeEmpty("an empty table here would read as \"you have no records\", which is not this fact");
        zones.Error.Should().NotBeNullOrEmpty();

        (await service.GetStateAsync(WorkspaceA, default)).LastVerificationError.Should().Be(zones.Error,
            "the page reads this back later instead of re-asking Cloudflare on every load");
    }

    // ---- round trip, with a faked client ----

    [Fact]
    public async Task Listing_zones_and_records_round_trips_and_filters_to_v1s_supported_types()
    {
        using var db = AsSystem();
        var handler = new RecordingCloudflareHandler();
        handler.ZonesByToken["good-token"] = ("zone-1", "example.com");
        handler.RecordsByZone["zone-1"] =
        [
            new RecordingCloudflareHandler.RecordRow("rec-1", "A", "example.com", "203.0.113.5", 1, null, false),
            new RecordingCloudflareHandler.RecordRow("rec-2", "NS", "example.com", "ns1.example.com", 3600, null, false)
        ];
        var service = Build(db, handler);
        await service.SaveTokenAsync(WorkspaceA, "good-token", default);

        var zones = await service.ListZonesAsync(WorkspaceA, default);
        zones.Success.Should().BeTrue();
        zones.Zones.Should().ContainSingle(z => z.Name == "example.com");

        var records = await service.ListRecordsAsync(WorkspaceA, "zone-1", default);
        records.Success.Should().BeTrue();
        records.Records.Should().ContainSingle(r => r.Type == "A");
        records.Records.Should().NotContain(r => r.Type == "NS",
            "NS is a real record but out of F9's v1 scope (A/AAAA/CNAME/TXT/MX only)");
    }

    [Fact]
    public async Task Creating_and_deleting_a_record_round_trips_through_the_fake_client()
    {
        using var db = AsSystem();
        var handler = new RecordingCloudflareHandler();
        handler.ZonesByToken["good-token"] = ("zone-1", "example.com");
        var service = Build(db, handler);
        await service.SaveTokenAsync(WorkspaceA, "good-token", default);

        var created = await service.CreateRecordAsync(
            WorkspaceA, "zone-1", "TXT", "_verify.example.com", "hello-world", 300, null, default);
        created.Success.Should().BeTrue();

        var afterCreate = await service.ListRecordsAsync(WorkspaceA, "zone-1", default);
        var record = afterCreate.Records.Should().ContainSingle(r => r.Type == "TXT").Subject;
        record.Content.Should().Be("hello-world");

        var deleted = await service.DeleteRecordAsync(WorkspaceA, "zone-1", record.Id, default);
        deleted.Success.Should().BeTrue();

        (await service.ListRecordsAsync(WorkspaceA, "zone-1", default)).Records.Should().BeEmpty();
    }

    [Fact]
    public async Task An_out_of_scope_record_type_is_refused_before_any_call_reaches_Cloudflare()
    {
        using var db = AsSystem();
        var handler = new RecordingCloudflareHandler();
        handler.ZonesByToken["good-token"] = ("zone-1", "example.com");
        var service = Build(db, handler);
        await service.SaveTokenAsync(WorkspaceA, "good-token", default);
        var callsBefore = handler.Requests.Count;

        var result = await service.CreateRecordAsync(
            WorkspaceA, "zone-1", "NS", "example.com", "ns1.example.com", 3600, null, default);

        result.Success.Should().BeFalse();
        handler.Requests.Count.Should().Be(callsBefore,
            "zone creation, DNSSEC and anything outside A/AAAA/CNAME/TXT/MX must be refused locally, not sent on");
    }

    // ---- tenancy, both directions ----

    [Fact]
    public async Task A_workspace_finds_its_own_token_and_its_own_zone()
    {
        var handler = new RecordingCloudflareHandler();
        handler.ZonesByToken["token-a"] = ("zone-a", "a.example.com");
        handler.ZonesByToken["token-b"] = ("zone-b", "b.example.com");
        using (var seed = AsSystem())
        {
            await Build(seed, handler).SaveTokenAsync(WorkspaceA, "token-a", default);
            await Build(seed, handler).SaveTokenAsync(WorkspaceB, "token-b", default);
        }

        using var scopedToA = AsTenant(WorkspaceA);
        var service = Build(scopedToA, handler);

        (await service.GetStateAsync(WorkspaceA, default)).HasToken.Should().BeTrue();
        var zones = await service.ListZonesAsync(WorkspaceA, default);
        zones.Zones.Should().ContainSingle(z => z.Name == "a.example.com");
    }

    [Fact]
    public async Task A_workspace_cannot_resolve_another_workspaces_token_even_when_asked_for_its_id()
    {
        var handler = new RecordingCloudflareHandler();
        handler.ZonesByToken["token-a"] = ("zone-a", "a.example.com");
        handler.ZonesByToken["token-b"] = ("zone-b", "b.example.com");
        using (var seed = AsSystem())
        {
            await Build(seed, handler).SaveTokenAsync(WorkspaceA, "token-a", default);
            await Build(seed, handler).SaveTokenAsync(WorkspaceB, "token-b", default);
        }

        // A session scoped to A, asked — as a guessed parameter or a caller bug would — for B's id.
        using var scopedToA = AsTenant(WorkspaceA);
        var service = Build(scopedToA, handler);

        (await service.GetStateAsync(WorkspaceB, default)).HasToken.Should().BeFalse(
            "the ambient filter for this session sees only A, whichever id is asked for");

        var callsBeforeAttempt = handler.Requests.Count;
        var zones = await service.ListZonesAsync(WorkspaceB, default);
        zones.Success.Should().BeFalse();
        zones.Zones.Should().BeEmpty();
        handler.Requests.Count.Should().Be(callsBeforeAttempt,
            "no token could be resolved for B from a session scoped to A, so Cloudflare was never even asked");
    }

    [Fact]
    public async Task Even_with_the_ambient_filter_disabled_the_services_own_explicit_id_check_still_isolates()
    {
        // Mirrors LogsControllerTenancyTests' strongest proof: turn off the model-level guard
        // (SystemWorkspaceScope makes CustomerDnsCredential's own query filter inert) and show the
        // service's explicit `WorkspaceId ==` predicate is what is actually doing the isolating.
        var handler = new RecordingCloudflareHandler();
        handler.ZonesByToken["token-a"] = ("zone-a", "a.example.com");
        handler.ZonesByToken["token-b"] = ("zone-b", "b.example.com");
        using var db = AsSystem();
        var service = Build(db, handler);
        await service.SaveTokenAsync(WorkspaceA, "token-a", default);
        await service.SaveTokenAsync(WorkspaceB, "token-b", default);

        var zonesForA = await service.ListZonesAsync(WorkspaceA, default);
        zonesForA.Zones.Should().ContainSingle(z => z.Name == "a.example.com");

        var zonesForB = await service.ListZonesAsync(WorkspaceB, default);
        zonesForB.Zones.Should().ContainSingle(z => z.Name == "b.example.com");

        // Each call reached Cloudflare with exactly its own workspace's token — proof the isolation
        // above is not just a filtered-database illusion, but that A's request never carried B's
        // token (or vice versa) onto the wire.
        var zoneListCalls = handler.Requests.Where(r => r.Path.Contains("zones?status=active")).ToList();
        zoneListCalls.Should().Contain(r => r.Token == "token-a");
        zoneListCalls.Should().Contain(r => r.Token == "token-b");
    }

    // ---- fakes ----

    private sealed class Clock : ISystemClock { public DateTimeOffset UtcNow => Now; }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// A minimal in-memory Cloudflare v4, keyed by bearer token — every request is recorded so a
    /// test can prove which token (and therefore which workspace) actually went on the wire.
    /// </summary>
    private sealed class RecordingCloudflareHandler : HttpMessageHandler
    {
        public List<(string? Token, string Path)> Requests { get; } = [];
        public Dictionary<string, (string ZoneId, string ZoneName)> ZonesByToken { get; } = [];
        public Dictionary<string, List<RecordRow>> RecordsByZone { get; } = [];
        public HashSet<string> InvalidTokens { get; } = [];
        public HashSet<string> TokensThatCannotListZones { get; } = [];

        public sealed record RecordRow(string Id, string Type, string Name, string Content, int Ttl, int? Priority, bool Proxied);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = request.Headers.Authorization?.Parameter;
            var path = request.RequestUri!.AbsolutePath + request.RequestUri.Query;
            Requests.Add((token, path));

            static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
                new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            static string Ok(string result) => $"{{\"success\":true,\"result\":{result}}}";
            const string Refused = "{\"success\":false,\"errors\":[{\"message\":\"Cloudflare refused this request\"}]}";

            if (token is null || InvalidTokens.Contains(token))
                return Json(Refused, HttpStatusCode.Forbidden);

            if (path.Contains("/user/tokens/verify"))
                return Json(Ok("{\"status\":\"active\"}"));

            if (path.Contains("/zones?status=active"))
            {
                if (TokensThatCannotListZones.Contains(token))
                    return Json(Refused, HttpStatusCode.Forbidden);
                if (!ZonesByToken.TryGetValue(token, out var zone))
                    return Json(Ok("[]"));
                return Json(Ok($"[{{\"id\":\"{zone.ZoneId}\",\"name\":\"{zone.ZoneName}\"}}]"));
            }

            var match = Regex.Match(path, @"^/client/v4/zones/([^/]+)/dns_records(/([^/?]+))?");
            if (match.Success)
            {
                var zoneId = match.Groups[1].Value;

                if (request.Method == HttpMethod.Get)
                {
                    var records = RecordsByZone.GetValueOrDefault(zoneId, []);
                    var items = string.Join(",", records.Select(r =>
                        $"{{\"id\":\"{r.Id}\",\"type\":\"{r.Type}\",\"name\":\"{r.Name}\",\"content\":\"{r.Content}\"," +
                        $"\"ttl\":{r.Ttl},\"proxied\":{(r.Proxied ? "true" : "false")}" +
                        (r.Priority is { } p ? $",\"priority\":{p}" : "") + "}"));
                    return Json(Ok($"[{items}]"));
                }

                if (request.Method == HttpMethod.Post)
                {
                    var body = await request.Content!.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    var newId = "rec-" + Guid.NewGuid().ToString("N")[..8];
                    var row = new RecordRow(
                        newId,
                        root.GetProperty("type").GetString()!,
                        root.GetProperty("name").GetString()!,
                        root.GetProperty("content").GetString()!,
                        root.GetProperty("ttl").GetInt32(),
                        root.TryGetProperty("priority", out var pr) ? pr.GetInt32() : null,
                        false);
                    if (!RecordsByZone.TryGetValue(zoneId, out var list))
                        RecordsByZone[zoneId] = list = [];
                    list.Add(row);
                    return Json(Ok($"{{\"id\":\"{newId}\"}}"));
                }

                if (request.Method == HttpMethod.Delete)
                {
                    var recordId = match.Groups[3].Value;
                    RecordsByZone.GetValueOrDefault(zoneId)?.RemoveAll(r => r.Id == recordId);
                    return Json(Ok("{}"));
                }
            }

            return Json("{\"success\":false,\"errors\":[{\"message\":\"unexpected test request: " + path + "\"}]}");
        }
    }
}
