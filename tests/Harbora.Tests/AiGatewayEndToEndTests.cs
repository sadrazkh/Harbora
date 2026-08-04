using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Ai;
using Harbora.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The gateway's path from a bare key to a routed request, against a real database context.
///
/// Tenant isolation is the property under test. A gateway authenticates before it knows which
/// tenant a request belongs to, so the usual workspace filter cannot protect it — the tenant comes
/// from the key's own row, and if that link were ever wrong one customer would be billed for
/// another's traffic, or worse, reach their models.
/// </summary>
public class AiGatewayEndToEndTests
{
    private sealed class Clock(DateTimeOffset now) : Harbora.Application.Abstractions.ISystemClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>Encryption is not what these tests are about; this keeps the token readable.</summary>
    private sealed class PassthroughProtector : Harbora.Application.Abstractions.ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;

        public string Unprotect(string ciphertext) =>
            ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : ciphertext;

        public string? TryUnprotect(string? ciphertext) =>
            ciphertext is null ? null : Unprotect(ciphertext);

        // Deterministic, as the contract requires: a key that changed per call would produce
        // ciphertext nothing could decrypt later.
        public byte[] DeriveKey(string purpose) =>
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("test:" + purpose));
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        HarboraDbContext Db, AiGatewayService Gateway,
        Guid WorkspaceA, string KeyA, Guid WorkspaceB, string KeyB,
        AiModel SharedModel, AiModel PremiumModel);

    private static Fixture Build()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("gateway-" + Guid.NewGuid()).Options);

        var provider = new AiProvider
        {
            Id = Guid.CreateVersion7(), Name = "OpenRouter",
            BaseUrl = "https://openrouter.ai/api/v1", IsEnabled = true
        };
        db.Add(provider);
        db.Add(new AiProviderCredential
        {
            Id = Guid.CreateVersion7(), AiProviderId = provider.Id,
            Label = "primary", EncryptedToken = "enc:sk-upstream-token", IsEnabled = true, Weight = 1
        });

        var shared = new AiModel
        {
            Id = Guid.CreateVersion7(), AiProviderId = provider.Id,
            Alias = "everyday", DisplayName = "Everyday", ProviderModelId = "vendor/everyday",
            IsEnabled = true, SupportsStreaming = true, MaxOutputTokens = 4096
        };
        var premium = new AiModel
        {
            Id = Guid.CreateVersion7(), AiProviderId = provider.Id,
            Alias = "premium", DisplayName = "Premium", ProviderModelId = "vendor/premium",
            IsEnabled = true, SupportsStreaming = true, MaxOutputTokens = 8192
        };
        db.AddRange(shared, premium);

        // Two tenants: one on a basic plan, one on a plan that also includes the premium model.
        var basic = new AiPlan { Id = Guid.CreateVersion7(), Name = "Basic", MaxOutputTokens = 2048 };
        basic.Models.Add(new AiPlanModel { AiPlanId = basic.Id, AiModelId = shared.Id });

        var full = new AiPlan { Id = Guid.CreateVersion7(), Name = "Full", MaxOutputTokens = 8192 };
        full.Models.Add(new AiPlanModel { AiPlanId = full.Id, AiModelId = shared.Id });
        full.Models.Add(new AiPlanModel { AiPlanId = full.Id, AiModelId = premium.Id });

        db.AddRange(basic, full);

        var workspaceA = Guid.CreateVersion7();
        var workspaceB = Guid.CreateVersion7();

        db.Add(new AiSubscription { WorkspaceId = workspaceA, AiPlanId = basic.Id, IsActive = true });
        db.Add(new AiSubscription { WorkspaceId = workspaceB, AiPlanId = full.Id, IsActive = true });

        var keyA = AiApiKeys.Create();
        var keyB = AiApiKeys.Create();

        db.Add(new AiUserApiKey
        {
            WorkspaceId = workspaceA, UserId = Guid.CreateVersion7(),
            Label = "A", Prefix = keyA.Prefix, KeyHash = keyA.Hash
        });
        db.Add(new AiUserApiKey
        {
            WorkspaceId = workspaceB, UserId = Guid.CreateVersion7(),
            Label = "B", Prefix = keyB.Prefix, KeyHash = keyB.Hash
        });

        db.SaveChanges();

        var gateway = new AiGatewayService(
            db, new PassthroughProtector(), new Clock(Now), NullLogger<AiGatewayService>.Instance);

        return new Fixture(db, gateway, workspaceA, keyA.Secret, workspaceB, keyB.Secret, shared, premium);
    }

    [Fact]
    public async Task A_valid_key_identifies_its_own_tenant_and_plan()
    {
        var f = Build();

        var caller = await f.Gateway.AuthenticateAsync(f.KeyA, default);

        caller.Should().NotBeNull();
        caller!.Key.WorkspaceId.Should().Be(f.WorkspaceA);
        caller.Plan.Name.Should().Be("Basic");
    }

    [Fact]
    public async Task An_unknown_key_is_nobody()
    {
        var f = Build();

        (await f.Gateway.AuthenticateAsync("har_completelymadeupkeyvalue", default)).Should().BeNull();
        (await f.Gateway.AuthenticateAsync("sk-someone-elses", default)).Should().BeNull();
        (await f.Gateway.AuthenticateAsync(null, default)).Should().BeNull();
    }

    [Fact]
    public async Task A_revoked_key_stops_working_immediately()
    {
        var f = Build();

        var key = await f.Db.AiUserApiKeys.FirstAsync(k => k.WorkspaceId == f.WorkspaceA);
        key.IsRevoked = true;
        await f.Db.SaveChangesAsync();

        (await f.Gateway.AuthenticateAsync(f.KeyA, default)).Should().BeNull();
    }

    [Fact]
    public async Task A_tenant_without_an_active_subscription_cannot_authenticate()
    {
        var f = Build();

        var subscription = await f.Db.AiSubscriptions.IgnoreQueryFilters()
            .FirstAsync(s => s.WorkspaceId == f.WorkspaceA);
        subscription.IsActive = false;
        await f.Db.SaveChangesAsync();

        (await f.Gateway.AuthenticateAsync(f.KeyA, default)).Should().BeNull();
    }

    [Fact]
    public async Task One_tenants_key_cannot_reach_another_tenants_models()
    {
        // The isolation property. A gateway authenticates before it knows the tenant, so this link
        // is the only thing standing between two customers.
        var f = Build();

        var a = await f.Gateway.AuthenticateAsync(f.KeyA, default);
        var b = await f.Gateway.AuthenticateAsync(f.KeyB, default);

        var aModels = (await f.Gateway.ModelsForAsync(a!, default)).Select(m => m.Alias).ToList();
        var bModels = (await f.Gateway.ModelsForAsync(b!, default)).Select(m => m.Alias).ToList();

        aModels.Should().BeEquivalentTo(["everyday"]);
        bModels.Should().BeEquivalentTo(["everyday", "premium"]);
    }

    [Fact]
    public async Task Asking_for_a_model_outside_the_plan_is_refused_by_the_router()
    {
        var f = Build();
        var a = await f.Gateway.AuthenticateAsync(f.KeyA, default);

        var (routed, refusal) = await f.Gateway.RouteAsync(a!, "premium", null, false, null, default);

        routed.Should().BeNull();
        refusal!.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task A_permitted_request_routes_to_a_credential_and_decrypts_its_token()
    {
        var f = Build();
        var a = await f.Gateway.AuthenticateAsync(f.KeyA, default);

        var (routed, refusal) = await f.Gateway.RouteAsync(a!, "everyday", 1000, false, null, default);

        refusal.Should().BeNull();
        routed!.Model.Alias.Should().Be("everyday");
        routed.Token.Should().Be("sk-upstream-token", "the adapter needs the real token, decrypted here");
        routed.Credential.Label.Should().Be("primary");
    }

    [Fact]
    public async Task The_plans_output_ceiling_is_enforced_at_routing_time()
    {
        // Basic caps at 2048 even though the model itself allows 4096.
        var f = Build();
        var a = await f.Gateway.AuthenticateAsync(f.KeyA, default);

        var (routed, refusal) = await f.Gateway.RouteAsync(a!, "everyday", 4000, false, null, default);

        routed.Should().BeNull();
        refusal!.Code.Should().Be("max_tokens_too_high");
    }

    [Fact]
    public async Task With_every_credential_excluded_the_request_is_refused_rather_than_unrouted()
    {
        var f = Build();
        var a = await f.Gateway.AuthenticateAsync(f.KeyA, default);

        var credential = await f.Db.AiProviderCredentials.FirstAsync();
        var (routed, refusal) = await f.Gateway.RouteAsync(
            a!, "everyday", null, false, new HashSet<Guid> { credential.Id }, default);

        routed.Should().BeNull();
        refusal!.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task Metering_records_the_request_against_the_right_tenant_and_moves_its_totals()
    {
        var f = Build();
        var a = await f.Gateway.AuthenticateAsync(f.KeyA, default);
        var (routed, _) = await f.Gateway.RouteAsync(a!, "everyday", null, false, null, default);

        f.SharedModel.ProviderInputPrice = 3m;
        f.SharedModel.ProviderOutputPrice = 15m;
        await f.Db.SaveChangesAsync();

        await f.Gateway.MeterAsync(routed!, 1_000_000, 0, 0, 200, 120, false, false, "corr-1", null, default);

        var record = await f.Db.AiUsageRecords.IgnoreQueryFilters().SingleAsync();
        record.WorkspaceId.Should().Be(f.WorkspaceA);
        record.InputTokens.Should().Be(1_000_000);
        record.ProviderCost.Should().Be(3m);

        var subscription = await f.Db.AiSubscriptions.IgnoreQueryFilters()
            .FirstAsync(s => s.WorkspaceId == f.WorkspaceA);
        subscription.PeriodTokens.Should().Be(1_000_000);
    }

    [Fact]
    public async Task A_metered_request_stores_no_prompt_or_response()
    {
        // The privacy property, asserted on the shape of the row rather than trusted to review.
        var f = Build();
        var a = await f.Gateway.AuthenticateAsync(f.KeyA, default);
        var (routed, _) = await f.Gateway.RouteAsync(a!, "everyday", null, false, null, default);

        await f.Gateway.MeterAsync(routed!, 10, 20, 0, 200, 50, false, false, "corr-2", null, default);

        var columns = typeof(AiUsageRecord).GetProperties().Select(p => p.Name).ToList();
        columns.Should().NotContain(n =>
            n.Contains("Prompt", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Message", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Content", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Response", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_disconnected_stream_is_still_recorded_and_still_charged()
    {
        // The provider billed us for what was produced before the customer left.
        var f = Build();
        var a = await f.Gateway.AuthenticateAsync(f.KeyA, default);
        var (routed, _) = await f.Gateway.RouteAsync(a!, "everyday", null, true, null, default);

        f.SharedModel.ProviderOutputPrice = 15m;
        await f.Db.SaveChangesAsync();

        await f.Gateway.MeterAsync(routed!, 100, 500, 0, 499, 900, true, true, "corr-3", "client disconnected", default);

        var record = await f.Db.AiUsageRecords.IgnoreQueryFilters().SingleAsync();
        record.ClientDisconnected.Should().BeTrue();
        record.OutputTokens.Should().Be(500);
        record.ProviderCost.Should().BeGreaterThan(0m);
    }
}
