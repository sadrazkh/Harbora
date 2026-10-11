using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The agent's three platform settings: what the settings page may save, what a blank means, and how
/// a stored value is read back. The page itself is covered over real HTTP in
/// <see cref="AgentSettingsPageHttpTests"/>; this is the rule underneath it.
/// </summary>
public class AgentModelSettingsTests
{
    private static HarboraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("agent-settings-" + Guid.NewGuid()).Options);

    // ---- validation ------------------------------------------------------------------------

    [Fact]
    public void Everything_blank_is_valid_and_stores_blanks_so_the_defaults_apply()
    {
        var result = AgentSettingsService.Validate(null, "", "   ");

        result.Ok.Should().BeTrue();
        result.Input.Should().Be(new AgentSettingsInput("", "", ""));
    }

    [Theory]
    [InlineData("300", "300")]
    [InlineData("0", "0")]
    [InlineData(" 12 ", "12")]
    [InlineData("007", "7")]
    [InlineData("۵۰۰", "500")]
    [InlineData("٣٠٠", "300")]
    [InlineData("۰", "0")]
    [InlineData("2147483647", "2147483647")]
    public void A_non_negative_whole_number_is_accepted_and_stored_in_plain_digits(string typed, string stored)
    {
        var result = AgentSettingsService.Validate(null, null, typed);

        result.Ok.Should().BeTrue();
        result.Input!.DailyRequestLimit.Should().Be(stored);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-0")]
    [InlineData("+5")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1,000")]
    [InlineData("12 34")]
    [InlineData("2147483648")]
    [InlineData("99999999999999999999")]
    [InlineData("۵۰۰x")]
    public void Anything_else_is_refused_in_both_languages_and_stores_nothing(string typed)
    {
        var result = AgentSettingsService.Validate("vendor/model:free", "a/b:free", typed);

        result.Ok.Should().BeFalse();
        result.Input.Should().BeNull();
        result.Error.Should().Contain("daily request limit").And.Contain("0 means unlimited");
        result.ErrorFa.Should().NotBeNullOrWhiteSpace().And.NotBe(result.Error);
    }

    [Fact]
    public void Fallbacks_are_trimmed_deduplicated_and_joined_without_blanks()
    {
        var result = AgentSettingsService.Validate("  main/model:free ", " a/b:free ,, C/d:free , A/B:free,", null);

        result.Ok.Should().BeTrue();
        result.Input!.Model.Should().Be("main/model:free");
        result.Input.FallbackModels.Should().Be("a/b:free,C/d:free");
    }

    [Theory]
    [InlineData("vendor/model :free", null)]
    [InlineData("two models", null)]
    [InlineData(null, "ok/one:free, not ok/two:free")]
    [InlineData("tab\tseparated", null)]
    public void A_model_id_with_whitespace_inside_is_refused_naming_the_culprit_for_fallbacks(string? model, string? fallbacks)
    {
        var result = AgentSettingsService.Validate(model, fallbacks, null);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("no spaces");
        result.ErrorFa.Should().NotBeNullOrWhiteSpace();
        if (fallbacks is not null) result.Error.Should().Contain("not ok/two:free");
    }

    [Fact]
    public void An_absurdly_long_model_id_is_refused()
    {
        AgentSettingsService.Validate(new string('a', 201), null, null).Ok.Should().BeFalse();
        AgentSettingsService.Validate(new string('a', 200), null, null).Ok.Should().BeTrue();
    }

    // ---- resolving -------------------------------------------------------------------------

    [Fact]
    public void The_agent_model_wins_and_blank_falls_back_to_the_assistants()
    {
        AgentSettingsService.Resolve("agent/x", "assistant/y", null).Primary.Should().Be("agent/x");
        AgentSettingsService.Resolve("", "assistant/y", null).Primary.Should().Be("assistant/y");
        AgentSettingsService.Resolve("  ", " assistant/y ", null).Primary.Should().Be("assistant/y");
        AgentSettingsService.Resolve(null, null, "a/b").Primary.Should().BeNull("nothing resolves, and a fallback is not a primary");
    }

    [Fact]
    public void Resolved_fallbacks_exclude_the_primary_and_repeats_and_keep_their_order()
    {
        var plan = AgentSettingsService.Resolve("main/x", null, "b/one, MAIN/x, c/two, b/one");

        plan.Fallbacks.Should().Equal("b/one", "c/two");
    }

    [Theory]
    [InlineData(null, 300)]
    [InlineData("", 300)]
    [InlineData("0", 0)]
    [InlineData("25", 25)]
    [InlineData("۲۵", 25)]
    [InlineData("-5", 300)]
    [InlineData("lots", 300)]
    [InlineData("99999999999", 300)]
    public void A_stored_limit_means_what_it_says_and_garbage_means_the_default_never_zero(string? stored, int expected)
    {
        // Zero is "no limit at all". A hand-edited row must never turn into that by accident.
        AgentSettingsService.EffectiveLimit(stored).Should().Be(expected);
    }

    [Fact]
    public void The_default_limit_is_the_documented_three_hundred()
    {
        AgentSettingKeys.DefaultDailyRequestLimit.Should().Be(300);
    }

    // ---- storage ---------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_stored_reads_as_blanks_and_the_default_limit()
    {
        var settings = await new AgentSettingsService(NewDb()).GetAsync(CancellationToken.None);

        settings.Model.Should().BeEmpty();
        settings.FallbackModels.Should().BeEmpty();
        settings.DailyRequestLimitText.Should().BeEmpty();
        settings.DailyRequestLimit.Should().Be(300);
    }

    [Fact]
    public async Task Saved_values_round_trip_under_the_contract_keys()
    {
        var db = NewDb();
        var service = new AgentSettingsService(db);

        await service.SaveAsync(
            AgentSettingsService.Validate("vendor/model:free", "a/b:free, c/d:free", "۱۲۰").Input!, CancellationToken.None);

        var rows = await db.Settings.ToDictionaryAsync(s => s.Key, s => s.Value);
        rows[AgentSettingKeys.Model].Should().Be("vendor/model:free");
        rows[AgentSettingKeys.FallbackModels].Should().Be("a/b:free,c/d:free");
        rows[AgentSettingKeys.DailyRequestLimit].Should().Be("120");

        var read = await service.GetAsync(CancellationToken.None);
        read.Should().Be(new AgentSettings("vendor/model:free", "a/b:free,c/d:free", "120", 120));
    }

    [Fact]
    public async Task Zero_is_stored_as_zero_and_read_as_unlimited_not_as_the_default()
    {
        var db = NewDb();
        var service = new AgentSettingsService(db);

        await service.SaveAsync(AgentSettingsService.Validate(null, null, "0").Input!, CancellationToken.None);

        var read = await service.GetAsync(CancellationToken.None);
        read.DailyRequestLimit.Should().Be(0);
        read.DailyRequestLimitText.Should().Be("0");
    }

    [Fact]
    public async Task Saving_again_updates_the_rows_instead_of_adding_second_ones_and_blank_clears()
    {
        var db = NewDb();
        var service = new AgentSettingsService(db);

        await service.SaveAsync(AgentSettingsService.Validate("one/x", "f/1", "5").Input!, CancellationToken.None);
        await service.SaveAsync(AgentSettingsService.Validate("", "", "").Input!, CancellationToken.None);

        (await db.Settings.CountAsync(s => s.Key.StartsWith("assistant.agent_"))).Should().Be(3);
        var read = await service.GetAsync(CancellationToken.None);
        read.Model.Should().BeEmpty();
        read.FallbackModels.Should().BeEmpty();
        read.DailyRequestLimit.Should().Be(300, "blank means the default again");
    }

    [Fact]
    public async Task Saving_never_touches_the_assistants_own_settings()
    {
        var db = NewDb();
        db.Settings.Add(new Setting { Key = "assistant.model", Value = "keep/me" });
        db.Settings.Add(new Setting { Key = "assistant.api_key", Value = "enc:secret", IsSecret = true });
        await db.SaveChangesAsync();

        await new AgentSettingsService(db).SaveAsync(
            AgentSettingsService.Validate("agent/x", null, null).Input!, CancellationToken.None);

        (await db.Settings.SingleAsync(s => s.Key == "assistant.model")).Value.Should().Be("keep/me");
        (await db.Settings.SingleAsync(s => s.Key == "assistant.api_key")).Value.Should().Be("enc:secret");
    }
}
