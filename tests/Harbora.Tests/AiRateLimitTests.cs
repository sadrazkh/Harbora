using FluentAssertions;
using Harbora.Domain.Ai;
using Harbora.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The per-minute limits a plan advertises.
///
/// They were stored, shown on the plan page and charged for, and nothing enforced them. A limit that
/// exists everywhere except in the code path is worse than no limit: the plan page says sixty
/// requests a minute, the customer sends six thousand, and the operator finds out from the provider
/// invoice.
/// </summary>
public class AiRateLimitTests
{
    private sealed class Clock(DateTimeOffset now) : Harbora.Application.Abstractions.ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static readonly DateTimeOffset Start = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static AiPlan Plan(
        int perMinute = 60, int perDay = 10_000, int tokensPerMinute = 100_000, int concurrent = 4) => new()
    {
        Id = Guid.CreateVersion7(), Name = "Pro",
        RequestsPerMinute = perMinute,
        RequestsPerDay = perDay,
        TokensPerMinute = tokensPerMinute,
        ConcurrentRequests = concurrent
    };

    private static List<RateEvent> Events(DateTimeOffset at, int count, long tokens = 0) =>
        Enumerable.Range(0, count).Select(i => new RateEvent(at.AddMilliseconds(i), tokens)).ToList();

    // ---- the rule ----

    [Fact]
    public void An_empty_history_allows_a_request()
    {
        AiRateWindow.Refuse(Plan(), [], 0, Start).Allowed.Should().BeTrue();
    }

    [Fact]
    public void The_request_that_reaches_the_limit_is_refused_not_the_one_after_it()
    {
        // Sixty a minute means the sixty-first is refused. Off by one here gives every customer one
        // free request a minute, or takes one away, and nobody can tell which from the outside.
        var plan = Plan(perMinute: 60);

        AiRateWindow.Refuse(plan, Events(Start, 59), 0, Start.AddSeconds(1)).Allowed.Should().BeTrue();
        AiRateWindow.Refuse(plan, Events(Start, 60), 0, Start.AddSeconds(1)).Allowed.Should().BeFalse();
    }

    [Fact]
    public void The_window_slides_rather_than_resetting_on_the_minute()
    {
        // The failure a fixed window has: the whole allowance at 11:59:59 and the whole next
        // allowance at 12:00:00 is twice the limit in two seconds, and it passes every test written
        // against a fixed window.
        var plan = Plan(perMinute: 60);
        var burst = Events(Start, 60);

        AiRateWindow.Refuse(plan, burst, 0, Start.AddSeconds(30)).Allowed.Should().BeFalse();
        AiRateWindow.Refuse(plan, burst, 0, Start.AddSeconds(61)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_burst_just_before_the_hand_of_the_clock_moves_still_counts()
    {
        // The exploit itself, and the one a fixed window lets through: the whole allowance at
        // 11:59:59 and the whole next allowance at 12:00:00 — twice the limit, one second apart.
        // Any implementation that resets on the minute passes the sliding test above and fails this.
        var plan = Plan(perMinute: 60);
        var justBefore = Events(Start.AddSeconds(-1), 60);

        AiRateWindow.Refuse(plan, justBefore, 0, Start).Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_refusal_says_how_long_to_wait()
    {
        var plan = Plan(perMinute: 10);
        var decision = AiRateWindow.Refuse(plan, Events(Start, 10), 0, Start.AddSeconds(20));

        decision.Allowed.Should().BeFalse();
        decision.RetryAfterSeconds.Should().BeInRange(39, 41, "the oldest request ages out after a minute");
    }

    [Fact]
    public void The_wait_is_never_zero()
    {
        // A Retry-After of zero invites an immediate retry that is certain to fail, and a client
        // honouring it turns one refusal into a spin.
        var plan = Plan(perMinute: 1);
        var decision = AiRateWindow.Refuse(plan, Events(Start, 1), 0, Start.AddSeconds(59.9));

        decision.RetryAfterSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public void The_daily_limit_is_reported_before_the_minute_one()
    {
        // Both are exhausted here. Told to wait a minute, the caller waits a minute, fails, and
        // reads the gateway as broken rather than as limited.
        var plan = Plan(perMinute: 10, perDay: 20);
        var events = Events(Start.AddHours(-12), 10).Concat(Events(Start, 10)).ToList();

        var decision = AiRateWindow.Refuse(plan, events, 0, Start.AddSeconds(1));

        decision.Refusal!.Code.Should().Be("daily_limit");
        decision.RetryAfterSeconds.Should().BeGreaterThan(60);
    }

    [Fact]
    public void Requests_older_than_a_day_do_not_count()
    {
        var plan = Plan(perDay: 5);
        var yesterday = Events(Start.AddDays(-1).AddMinutes(-1), 5);

        AiRateWindow.Refuse(plan, yesterday, 0, Start).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Tokens_already_spent_this_minute_close_the_gate()
    {
        var plan = Plan(perMinute: 1000, tokensPerMinute: 5_000);
        var spent = Events(Start, 5, tokens: 1_000);

        var decision = AiRateWindow.Refuse(plan, spent, 0, Start.AddSeconds(10));

        decision.Refusal!.Code.Should().Be("token_rate_limit");
    }

    [Fact]
    public void Tokens_spent_more_than_a_minute_ago_do_not_count()
    {
        var plan = Plan(perMinute: 1000, tokensPerMinute: 5_000);
        var spent = Events(Start, 5, tokens: 1_000);

        AiRateWindow.Refuse(plan, spent, 0, Start.AddSeconds(61)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_token_limit_of_zero_is_not_enforced_as_a_block()
    {
        // Unlike the request limits: a plan that meant to leave the token limit off would otherwise
        // be unusable, and the request limits already bound it.
        var plan = Plan(tokensPerMinute: 0);

        AiRateWindow.Refuse(plan, Events(Start, 1, tokens: 999_999), 0, Start).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Concurrency_is_refused_last_and_with_the_shortest_wait()
    {
        // It clears when a request in progress finishes, which can be sooner than any window.
        var plan = Plan(concurrent: 2);
        var decision = AiRateWindow.Refuse(plan, [], 2, Start);

        decision.Refusal!.Code.Should().Be("too_many_concurrent");
        decision.RetryAfterSeconds.Should().Be(1);
    }

    [Fact]
    public void One_below_the_concurrency_limit_is_allowed()
    {
        AiRateWindow.Refuse(Plan(concurrent: 2), [], 1, Start).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 10, 10)]
    [InlineData(10, 0, 10)]
    [InlineData(10, 10, 0)]
    public void A_limit_of_zero_blocks_rather_than_opens(int perMinute, int perDay, int concurrent)
    {
        // The direction this must fail in. An administrator who clears a field breaks their
        // customers, who say so within the hour; one who accidentally removes every limit is told by
        // the provider invoice a month later.
        var plan = Plan(perMinute: perMinute, perDay: perDay, concurrent: concurrent);

        AiRateWindow.Refuse(plan, [], 0, Start).Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_blocking_plan_offers_no_retry_time()
    {
        // Waiting will not help; the plan has to change.
        AiRateWindow.Refuse(Plan(perMinute: 0), [], 0, Start).RetryAfterSeconds.Should().Be(0);
    }

    [Fact]
    public void Every_refusal_is_a_429()
    {
        // Not 402 or 403. A client library retries a 429 with backoff and gives up on the others,
        // and a limit that reads as "you are not entitled" gets escalated as a billing problem.
        var refusals = new[]
        {
            AiRateWindow.Refuse(Plan(perMinute: 1), Events(Start, 1), 0, Start),
            AiRateWindow.Refuse(Plan(perDay: 1), Events(Start, 1), 0, Start),
            AiRateWindow.Refuse(Plan(tokensPerMinute: 1), Events(Start, 1, tokens: 5), 0, Start),
            AiRateWindow.Refuse(Plan(concurrent: 1), [], 1, Start),
            AiRateWindow.Refuse(Plan(perMinute: 0), [], 0, Start)
        };

        refusals.Should().OnlyContain(r => r.Refusal!.StatusCode == 429);
    }

    [Fact]
    public void Pruning_keeps_a_day_and_drops_the_rest()
    {
        var events = Events(Start.AddDays(-2), 3).Concat(Events(Start.AddHours(-1), 2)).ToList();

        AiRateWindow.Prune(events, Start).Should().HaveCount(2);
    }

    // ---- the counter ----

    [Fact]
    public void The_limiter_counts_a_request_as_it_starts()
    {
        // Counted on the way in, not on the way out. Counting finished requests lets a caller open a
        // thousand at once: none has finished, so none is counted.
        var limiter = new AiRateLimiter(new Clock(Start));
        var plan = Plan(perMinute: 2);
        var workspace = Guid.CreateVersion7();

        limiter.TryEnter(workspace, plan).Slot.Should().NotBeNull();
        limiter.TryEnter(workspace, plan).Slot.Should().NotBeNull();
        limiter.TryEnter(workspace, plan).Slot.Should().BeNull();
    }

    [Fact]
    public void One_tenant_does_not_spend_another_tenants_allowance()
    {
        var limiter = new AiRateLimiter(new Clock(Start));
        var plan = Plan(perMinute: 1);
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();

        limiter.TryEnter(mine, plan).Slot.Should().NotBeNull();
        limiter.TryEnter(mine, plan).Slot.Should().BeNull();
        limiter.TryEnter(theirs, plan).Slot.Should().NotBeNull();
    }

    [Fact]
    public void Disposing_a_slot_frees_the_concurrency_it_took()
    {
        var limiter = new AiRateLimiter(new Clock(Start));
        var plan = Plan(perMinute: 100, concurrent: 1);
        var workspace = Guid.CreateVersion7();

        var first = limiter.TryEnter(workspace, plan).Slot;
        first.Should().NotBeNull();
        limiter.TryEnter(workspace, plan).Slot.Should().BeNull("the one slot is taken");

        first!.Dispose();
        limiter.TryEnter(workspace, plan).Slot.Should().NotBeNull();
    }

    [Fact]
    public void Disposing_a_slot_twice_does_not_hand_back_a_slot_that_was_never_taken()
    {
        // Otherwise a caller who abandons requests accumulates free concurrency.
        var limiter = new AiRateLimiter(new Clock(Start));
        var plan = Plan(perMinute: 100, concurrent: 2);
        var workspace = Guid.CreateVersion7();

        var a = limiter.TryEnter(workspace, plan).Slot!;
        var b = limiter.TryEnter(workspace, plan).Slot!;

        a.Dispose();
        a.Dispose();

        // Checked while b is still held. Once both are released the count is zero either way, so
        // asserting at the end proves nothing — the extra release would have been absorbed by the
        // guard against going negative, and a tenant with a limit of two would be running three.
        limiter.InFlight(workspace).Should().Be(1);

        b.Dispose();
        limiter.InFlight(workspace).Should().Be(0);
    }

    [Fact]
    public void Recorded_tokens_attach_to_the_request_that_spent_them()
    {
        var clock = new Clock(Start);
        var limiter = new AiRateLimiter(clock);
        var plan = Plan(perMinute: 100, tokensPerMinute: 1_000);
        var workspace = Guid.CreateVersion7();

        var slot = limiter.TryEnter(workspace, plan).Slot!;
        slot.Record(900);

        limiter.TokensInLastMinute(workspace).Should().Be(900);
    }

    [Fact]
    public void Reporting_tokens_twice_does_not_count_the_request_twice()
    {
        // A retry path or a finally block running once too often would otherwise halve the
        // customer's real allowance, and nothing on their side would explain it.
        var clock = new Clock(Start);
        var limiter = new AiRateLimiter(clock);
        var plan = Plan(perMinute: 100, tokensPerMinute: 10_000);
        var workspace = Guid.CreateVersion7();

        var slot = limiter.TryEnter(workspace, plan).Slot!;
        slot.Record(500);
        slot.Record(500);

        limiter.TokensInLastMinute(workspace).Should().Be(500);
    }

    [Fact]
    public void Spent_tokens_close_the_gate_for_the_next_request()
    {
        var clock = new Clock(Start);
        var limiter = new AiRateLimiter(clock);
        var plan = Plan(perMinute: 100, tokensPerMinute: 1_000);
        var workspace = Guid.CreateVersion7();

        var slot = limiter.TryEnter(workspace, plan).Slot!;
        slot.Record(1_000);
        slot.Dispose();

        clock.UtcNow = Start.AddSeconds(5);
        limiter.TryEnter(workspace, plan).Slot.Should().BeNull();

        // ...and opens again once that minute has passed.
        clock.UtcNow = Start.AddSeconds(61);
        limiter.TryEnter(workspace, plan).Slot.Should().NotBeNull();
    }

    [Fact]
    public void A_refusal_does_not_take_a_concurrency_slot()
    {
        // A refused request never ran. Counting it would mean a tenant at their limit could never
        // recover, because every rejected retry would hold another slot.
        var limiter = new AiRateLimiter(new Clock(Start));
        var plan = Plan(perMinute: 1, concurrent: 4);
        var workspace = Guid.CreateVersion7();

        var slot = limiter.TryEnter(workspace, plan).Slot!;
        limiter.TryEnter(workspace, plan).Slot.Should().BeNull();
        limiter.TryEnter(workspace, plan).Slot.Should().BeNull();

        limiter.InFlight(workspace).Should().Be(1);
        slot.Dispose();
        limiter.InFlight(workspace).Should().Be(0);
    }

    [Fact]
    public void The_limiter_is_registered_as_a_singleton()
    {
        // Everything above is about a counter that remembers. Registered scoped, every request gets
        // a fresh one, every limit is judged against a history of exactly one request, and the
        // feature is present, tested, configured and enforcing nothing — which is precisely the
        // state it was in before this phase.
        // A throwaway master key: registration refuses to run without one, which is itself a
        // property worth having and not one this test is about.
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harbora:MasterKey"] = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData("tests"u8.ToArray()))
            })
            .Build();

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Harbora.Infrastructure.DependencyInjection.AddHarboraInfrastructure(services, config);

        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(AiRateLimiter));

        descriptor.Should().NotBeNull("the gateway resolves it by type");
        descriptor!.Lifetime.Should().Be(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton);
    }

    [Fact]
    public void Concurrent_callers_never_exceed_the_limit()
    {
        // The lock earns its place here: without it two threads read the same count and both take
        // the last slot.
        var limiter = new AiRateLimiter(new Clock(Start));
        var plan = Plan(perMinute: 10_000, perDay: 100_000, concurrent: 5);
        var workspace = Guid.CreateVersion7();

        var granted = 0;
        Parallel.For(0, 200, _ =>
        {
            var slot = limiter.TryEnter(workspace, plan).Slot;
            if (slot is not null) Interlocked.Increment(ref granted);
        });

        granted.Should().Be(5, "the slots were never released");
        limiter.InFlight(workspace).Should().Be(5);
    }
}
