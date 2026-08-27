using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Ai;
using Harbora.Web.Controllers;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What the administration screen does to the database.
///
/// The page-level guards live in <see cref="AiAdminPageTests"/>; these are the state changes behind
/// them, and each one is a change nobody notices when it silently does nothing: a token that was not
/// really replaced keeps failing, a model unticked from a plan keeps being served, a base URL that
/// points inside our own network is only discovered when the gateway calls it.
/// </summary>
public class AiAdminControllerTests
{
    private sealed class Clock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<(string Action, string? TargetId)> Entries { get; } = [];

        public Task LogAsync(
            string action, string? targetType = null, string? targetId = null, string? ipAddress = null,
            string? actorEmailOverride = null, Guid? userIdOverride = null, string? metadataJson = null,
            Guid? workspaceId = null, CancellationToken ct = default)
        {
            Entries.Add((action, targetId));
            return Task.CompletedTask;
        }
    }

    private sealed class PassthroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;

        public string Unprotect(string ciphertext) =>
            ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : ciphertext;

        public string? TryUnprotect(string? ciphertext) => ciphertext is null ? null : Unprotect(ciphertext);

        public byte[] DeriveKey(string purpose) =>
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("test:" + purpose));
    }

    private sealed record Fixture(HarboraDbContext Db, AiAdminController Controller, RecordingAudit Audit);

    private static Fixture Build()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("ai-admin-" + Guid.NewGuid()).Options);

        var audit = new RecordingAudit();
        var controller = new AiAdminController(db, new PassthroughProtector(), audit, new Clock())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.TempData = new TempDataDictionary(controller.HttpContext, new NullTempDataProvider());

        return new Fixture(db, controller, audit);
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private static AiProvider Provider(HarboraDbContext db)
    {
        var provider = new AiProvider
        {
            Id = Guid.CreateVersion7(), Name = "OpenRouter",
            BaseUrl = "https://openrouter.ai/api/v1", IsEnabled = true
        };
        db.Add(provider);
        db.SaveChanges();
        return provider;
    }

    [Fact]
    public async Task A_base_url_inside_our_own_network_is_refused_before_it_is_stored()
    {
        // The check has to happen here as well as at call time. Stored, it becomes a request our
        // server makes to itself on someone else's instruction — which is the whole of SSRF.
        var f = Build();

        var result = await f.Controller.SaveProvider(
            null, "Internal", AiProviderType.OpenAiCompatible, "http://169.254.169.254/latest",
            0, null, true, CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>();
        (await f.Db.AiProviders.CountAsync()).Should().Be(0);
        f.Controller.TempData["Error"].Should().NotBeNull();
    }

    [Fact]
    public async Task A_usable_provider_is_saved()
    {
        var f = Build();

        await f.Controller.SaveProvider(
            null, "OpenRouter", AiProviderType.OpenRouter, "https://openrouter.ai/api/v1",
            3, 250m, true, CancellationToken.None);

        var saved = await f.Db.AiProviders.SingleAsync();
        saved.Name.Should().Be("OpenRouter");
        saved.Priority.Should().Be(3);
        saved.MonthlyBudget.Should().Be(250m);
    }

    [Fact]
    public async Task A_token_is_encrypted_and_the_plaintext_is_kept_nowhere()
    {
        var f = Build();
        var provider = Provider(f.Db);

        await f.Controller.AddCredential(provider.Id, "primary", "sk-secret-value", 0, 2, CancellationToken.None);

        var credential = await f.Db.AiProviderCredentials.SingleAsync();
        credential.EncryptedToken.Should().Be("enc:sk-secret-value");
        credential.EncryptedToken.Should().NotBe("sk-secret-value");

        // Not in the audit trail either. An audit row holding a provider secret is a second copy of
        // the thing hardest to rotate, kept for as long as the audit retention.
        f.Audit.Entries.Should().NotContain(e => e.TargetId != null && e.TargetId.Contains("sk-secret-value"));
    }

    [Fact]
    public async Task An_empty_token_is_refused_rather_than_stored()
    {
        // A blank field means the administrator did not type one. Storing it would present as a
        // configured credential that fails every call.
        var f = Build();
        var provider = Provider(f.Db);

        await f.Controller.AddCredential(provider.Id, "primary", "   ", 0, 1, CancellationToken.None);

        (await f.Db.AiProviderCredentials.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Rotating_replaces_the_token_and_clears_the_health_of_the_old_one()
    {
        var f = Build();
        var provider = Provider(f.Db);
        var credential = new AiProviderCredential
        {
            Id = Guid.CreateVersion7(), AiProviderId = provider.Id, Label = "primary",
            EncryptedToken = "enc:old", ConsecutiveFailures = 9, LastFailureReason = "401 unauthorized",
            RateLimitedUntil = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        f.Db.Add(credential);
        await f.Db.SaveChangesAsync();

        await f.Controller.RotateCredential(credential.Id, "sk-new", CancellationToken.None);

        var updated = await f.Db.AiProviderCredentials.SingleAsync();
        updated.EncryptedToken.Should().Be("enc:sk-new");

        // Without this the new token stays out of rotation behind the old one's open circuit, and
        // the administrator's fix appears to have changed nothing.
        updated.ConsecutiveFailures.Should().Be(0);
        updated.LastFailureReason.Should().BeNull();
        updated.RateLimitedUntil.Should().BeNull();
    }

    [Fact]
    public async Task An_empty_replacement_leaves_the_existing_token_alone()
    {
        // The dangerous version of this bug wipes a working token because somebody submitted the
        // form without typing in the rotate field.
        var f = Build();
        var provider = Provider(f.Db);
        var credential = new AiProviderCredential
        {
            Id = Guid.CreateVersion7(), AiProviderId = provider.Id, Label = "primary", EncryptedToken = "enc:live"
        };
        f.Db.Add(credential);
        await f.Db.SaveChangesAsync();

        await f.Controller.RotateCredential(credential.Id, "", CancellationToken.None);

        (await f.Db.AiProviderCredentials.SingleAsync()).EncryptedToken.Should().Be("enc:live");
    }

    [Fact]
    public async Task Disabling_a_credential_does_not_delete_it()
    {
        // Turning a token off must be reversible. Deleting it means finding the original again,
        // which by design nobody can.
        var f = Build();
        var provider = Provider(f.Db);
        var credential = new AiProviderCredential
        {
            Id = Guid.CreateVersion7(), AiProviderId = provider.Id, Label = "primary",
            EncryptedToken = "enc:live", IsEnabled = true
        };
        f.Db.Add(credential);
        await f.Db.SaveChangesAsync();

        await f.Controller.ToggleCredential(credential.Id, CancellationToken.None);

        var after = await f.Db.AiProviderCredentials.SingleAsync();
        after.IsEnabled.Should().BeFalse();
        after.EncryptedToken.Should().Be("enc:live");
    }

    [Fact]
    public async Task Unticking_a_model_removes_it_from_the_plan()
    {
        // The reason the form replaces the set instead of adding to it. An add-only form cannot take
        // a model back off a plan, so a mistake there is permanent from the interface.
        var f = Build();
        var provider = Provider(f.Db);

        var kept = new AiModel { Id = Guid.CreateVersion7(), AiProviderId = provider.Id, Alias = "kept", ProviderModelId = "a" };
        var dropped = new AiModel { Id = Guid.CreateVersion7(), AiProviderId = provider.Id, Alias = "dropped", ProviderModelId = "b" };
        var plan = new AiPlan { Id = Guid.CreateVersion7(), Name = "Pro" };
        f.Db.AddRange(kept, dropped, plan);
        f.Db.AddRange(
            new AiPlanModel { Id = Guid.CreateVersion7(), AiPlanId = plan.Id, AiModelId = kept.Id },
            new AiPlanModel { Id = Guid.CreateVersion7(), AiPlanId = plan.Id, AiModelId = dropped.Id });
        await f.Db.SaveChangesAsync();

        await f.Controller.SetPlanModels(plan.Id, [kept.Id], CancellationToken.None);

        var remaining = await f.Db.AiPlanModels.Where(m => m.AiPlanId == plan.Id).ToListAsync();
        remaining.Should().ContainSingle().Which.AiModelId.Should().Be(kept.Id);
    }

    [Fact]
    public async Task Clearing_every_model_leaves_the_plan_with_none()
    {
        // Submitting the form with nothing ticked has to mean nothing, not "no change". The browser
        // sends no field at all for an empty checkbox group, so this is the case that silently
        // no-ops if the null is treated as "leave it alone".
        var f = Build();
        var provider = Provider(f.Db);
        var model = new AiModel { Id = Guid.CreateVersion7(), AiProviderId = provider.Id, Alias = "m", ProviderModelId = "a" };
        var plan = new AiPlan { Id = Guid.CreateVersion7(), Name = "Pro" };
        f.Db.AddRange(model, plan);
        f.Db.Add(new AiPlanModel { Id = Guid.CreateVersion7(), AiPlanId = plan.Id, AiModelId = model.Id });
        await f.Db.SaveChangesAsync();

        await f.Controller.SetPlanModels(plan.Id, null, CancellationToken.None);

        (await f.Db.AiPlanModels.CountAsync(m => m.AiPlanId == plan.Id)).Should().Be(0);
    }

    [Fact]
    public async Task A_saved_model_is_marked_as_manually_managed()
    {
        // So a later registry sync leaves the operator's pricing alone. A sync that reverts a
        // deliberate override is one nobody dares run again.
        var f = Build();
        var provider = Provider(f.Db);

        await f.Controller.SaveModel(
            null, provider.Id, "fast", "", "vendor/fast-1", 128_000, 8_192,
            0.5m, 1.5m, null, null, 20m,
            true, false, false, false, true, CancellationToken.None);

        var saved = await f.Db.AiModels.SingleAsync();
        saved.IsManuallyManaged.Should().BeTrue();
        saved.Alias.Should().Be("fast");

        // An empty display name falls back to the alias rather than rendering as a blank row.
        saved.DisplayName.Should().Be("fast");
    }

    [Fact]
    public async Task The_overview_counts_only_the_last_thirty_days()
    {
        // A total that quietly includes everything ever recorded grows for ever and can never be
        // reconciled against a provider invoice.
        var f = Build();
        var clock = new Clock();

        f.Db.AddRange(
            new AiUsageRecord { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow.AddDays(-5), ProviderCost = 2m, ChargedCost = 3m, StatusCode = 200, RequestedModel = "fast" },
            new AiUsageRecord { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow.AddDays(-45), ProviderCost = 100m, ChargedCost = 150m, StatusCode = 200, RequestedModel = "fast" });
        await f.Db.SaveChangesAsync();

        var view = (ViewResult)await f.Controller.Index(CancellationToken.None);
        var vm = (AiAdminViewModel)view.Model!;

        vm.SpendLast30Days.Should().Be(2m);
        vm.ChargedLast30Days.Should().Be(3m);
        vm.RequestsLast30Days.Should().Be(1);
    }

    [Fact]
    public async Task Only_failures_appear_in_the_failure_list()
    {
        var f = Build();
        var clock = new Clock();

        f.Db.AddRange(
            new AiUsageRecord { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow.AddHours(-1), StatusCode = 200, RequestedModel = "fast" },
            new AiUsageRecord { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow.AddHours(-2), StatusCode = 429, RequestedModel = "fast", FailureReason = "rate limited" },
            new AiUsageRecord { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow.AddDays(-9), StatusCode = 500, RequestedModel = "fast", FailureReason = "old" });
        await f.Db.SaveChangesAsync();

        var vm = (AiAdminViewModel)((ViewResult)await f.Controller.Index(CancellationToken.None)).Model!;

        vm.RecentFailures.Should().ContainSingle();
        vm.RecentFailures[0].StatusCode.Should().Be(429);
    }
}
