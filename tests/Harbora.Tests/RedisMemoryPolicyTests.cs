using FluentAssertions;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 1.6 (redis-eviction): the pure logic behind a Redis instance's <c>maxmemory</c>/
/// <c>maxmemory-policy</c> — offering five named choices with a plain-language consequence each
/// rather than Redis's own eight raw constants, and refusing a pair that cannot be honoured rather
/// than silently accepting or clamping it. The engine-level proof (does the setting actually reach a
/// running container, is a stopped one told the truth) lives in
/// <see cref="RedisMemoryPolicyEngineTests"/>; this file is about the decision alone.
/// </summary>
public class RedisMemoryPolicyTests
{
    [Fact]
    public void Only_five_choices_are_offered_and_each_carries_both_languages()
    {
        // allkeys-random and volatile-random are deliberately left out — see the class's own doc.
        RedisMemoryPolicy.Choices.Should().HaveCount(5);
        foreach (var choice in RedisMemoryPolicy.Choices)
        {
            choice.Label.Should().NotBeNullOrWhiteSpace();
            choice.LabelFa.Should().NotBeNullOrWhiteSpace();
            choice.Consequence.Should().NotBeNullOrWhiteSpace();
            choice.ConsequenceFa.Should().NotBeNullOrWhiteSpace();
            choice.SuitedTo.Should().NotBeNullOrWhiteSpace();
            choice.SuitedToFa.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Never_evict_is_the_only_choice_that_does_not_evict()
    {
        RedisMemoryPolicy.Find(RedisMemoryPolicy.NoEviction)!.Evicts.Should().BeFalse();
        RedisMemoryPolicy.Choices.Where(c => c.Key != RedisMemoryPolicy.NoEviction)
            .Should().OnlyContain(c => c.Evicts);
    }

    [Fact]
    public void An_unknown_key_is_not_found_and_not_known()
    {
        RedisMemoryPolicy.Find("allkeys-random").Should().BeNull("offering it turns a decision into a menu");
        RedisMemoryPolicy.IsKnown("allkeys-random").Should().BeFalse();
        RedisMemoryPolicy.IsKnown(null).Should().BeFalse();
        RedisMemoryPolicy.IsKnown(RedisMemoryPolicy.AllKeysLru).Should().BeTrue();
    }

    [Fact]
    public void Nothing_chosen_is_never_refused()
    {
        // The state every instance is already in — an untouched Redis must never be told its own
        // default is invalid.
        RedisMemoryPolicy.WhyRefused(null, 0, 0, isFa: false).Should().BeNull();
        RedisMemoryPolicy.WhyRefused(null, 0, 1024L * 1024 * 1024, isFa: false).Should().BeNull();
    }

    [Fact]
    public void A_cap_with_no_policy_is_refused_because_it_would_silently_mean_noeviction()
    {
        RedisMemoryPolicy.WhyRefused(null, 64L * 1024 * 1024, 0, isFa: false).Should()
            .Contain("eviction policy", "a cap with nothing to enforce it is invisible from the panel otherwise");
    }

    [Fact]
    public void An_evicting_policy_with_no_cap_is_refused_because_it_would_do_nothing()
    {
        RedisMemoryPolicy.WhyRefused(RedisMemoryPolicy.AllKeysLru, 0, 0, isFa: false).Should()
            .Contain("does nothing", "Redis evicts nothing at all until maxmemory is set");
    }

    [Fact]
    public void Never_evict_with_no_cap_is_never_refused()
    {
        // The mirror image of the previous test: noeviction does not evict, so pairing it with no cap
        // is exactly today's default and must be allowed.
        RedisMemoryPolicy.WhyRefused(RedisMemoryPolicy.NoEviction, 0, 0, isFa: false).Should().BeNull();
    }

    [Fact]
    public void A_cap_below_the_minimum_is_refused_by_name()
    {
        RedisMemoryPolicy.WhyRefused(RedisMemoryPolicy.AllKeysLru, 1024, 0, isFa: false).Should()
            .Contain("at least");
    }

    [Fact]
    public void A_cap_within_the_containers_usable_fraction_is_accepted()
    {
        // 0.8 of a 1 GB container is 800ish MB usable — anything at or under that must be allowed.
        var containerLimit = 1024L * 1024 * 1024;
        var withinCeiling = RedisMemoryPolicy.Ceiling(containerLimit)!.Value;
        RedisMemoryPolicy.WhyRefused(RedisMemoryPolicy.AllKeysLru, withinCeiling, containerLimit, isFa: false)
            .Should().BeNull();
    }

    [Fact]
    public void A_container_with_no_recorded_limit_has_no_ceiling_to_violate()
    {
        // Zero means "Harbora cannot tell you the ceiling", not "the ceiling is zero" — a huge cap
        // must not be refused against a limit that was never actually recorded.
        RedisMemoryPolicy.Ceiling(0).Should().BeNull();
        RedisMemoryPolicy.WhyRefused(RedisMemoryPolicy.AllKeysLru, 50L * 1024 * 1024 * 1024, 0, isFa: false)
            .Should().BeNull();
    }

    [Fact]
    public void A_negative_cap_is_refused_before_anything_else_is_checked()
    {
        RedisMemoryPolicy.WhyRefused(RedisMemoryPolicy.AllKeysLru, -1, 0, isFa: false).Should().Contain("negative");
    }

    [Fact]
    public void An_unknown_policy_is_refused_by_its_own_name_in_both_languages()
    {
        RedisMemoryPolicy.WhyRefused("bogus", 0, 0, isFa: false).Should().Contain("bogus");
        RedisMemoryPolicy.WhyRefused("bogus", 0, 0, isFa: true).Should().Contain("bogus");
    }

    [Fact]
    public void Command_arguments_are_empty_when_nothing_has_ever_been_chosen()
    {
        // The exact fact that keeps an untouched instance's command line byte-for-byte what it always
        // was — see RedisMemoryPolicyEngineTests for the end-to-end proof through ProvisionAsync.
        RedisMemoryPolicy.CommandArguments(null, 0).Should().BeEmpty();
    }

    [Fact]
    public void Command_arguments_carry_the_policy_before_the_cap()
    {
        var args = RedisMemoryPolicy.CommandArguments(RedisMemoryPolicy.VolatileTtl, 128L * 1024 * 1024);

        args.Should().Equal("--maxmemory-policy", "volatile-ttl", "--maxmemory", (128L * 1024 * 1024).ToString());
    }

    [Fact]
    public void Command_arguments_carry_only_the_cap_when_no_policy_was_chosen()
    {
        // Not a state the panel offers (WhyRefused above already refuses it), but the builder itself
        // must still behave rather than throw if it is ever called with one.
        var args = RedisMemoryPolicy.CommandArguments(null, 64L * 1024 * 1024);

        args.Should().Equal("--maxmemory", (64L * 1024 * 1024).ToString());
    }

    [Fact]
    public void Live_apply_is_null_when_nothing_has_been_chosen()
    {
        var creds = new ServiceCreds("harbora-svc-cache", 6379, "harbora", "pw1234567890", "");

        RedisMemoryPolicy.LiveApply(creds, null, 0).Should().BeNull("there is nothing to send to a default instance");
    }

    [Fact]
    public void Live_apply_carries_the_password_only_through_the_environment()
    {
        var creds = new ServiceCreds("harbora-svc-cache", 6379, "harbora", "super-secret-pw", "");

        var plan = RedisMemoryPolicy.LiveApply(creds, RedisMemoryPolicy.AllKeysLru, 64L * 1024 * 1024)!;

        string.Join(' ', plan.Command).Should().NotContain("super-secret-pw",
            "the password must never land on a command line another process on the host could read");
        plan.Env["REDISCLI_AUTH"].Should().Be("super-secret-pw");
    }

    [Fact]
    public void Live_apply_stops_at_the_first_failed_statement()
    {
        var creds = new ServiceCreds("harbora-svc-cache", 6379, "harbora", "pw1234567890", "");

        var plan = RedisMemoryPolicy.LiveApply(creds, RedisMemoryPolicy.AllKeysLru, 64L * 1024 * 1024)!;

        string.Join(' ', plan.Command).Should().Contain("set -e",
            "otherwise the second CONFIG SET could be reported as the pair's outcome when the first one already failed");
    }
}
