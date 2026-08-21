using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Functions;
using Harbora.Infrastructure.Functions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What a function app refuses to store.
///
/// <para>
/// Every one of these is a refusal the server makes on its own. The editor greys out what it can,
/// but the form is a courtesy — a second function claiming one route is not a validation nicety: the
/// generated dispatcher picks the longest match, so one of the two would silently never run.
/// </para>
/// </summary>
public class FunctionValidationTests
{
    private static FunctionDefinition Existing(string slug, FunctionTrigger trigger = FunctionTrigger.Http, string? route = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = slug, Slug = slug, Trigger = trigger, Route = route, Code = "x"
        };

    private static FunctionDefinition Candidate(
        string name, FunctionTrigger trigger = FunctionTrigger.Http,
        string? route = null, string? cron = null, string? eventKey = null, string code = "x") =>
        new()
        {
            Name = name,
            Slug = FunctionSlug.Normalise(name),
            Trigger = trigger,
            Route = route,
            CronExpression = cron,
            EventKey = eventKey,
            Code = code
        };

    [Fact]
    public void A_valid_http_function_is_accepted() =>
        FunctionAppService.Validate(Candidate("hello"), [], null).Ok.Should().BeTrue();

    [Fact]
    public void A_nameless_function_is_refused() =>
        FunctionAppService.Validate(Candidate(""), [], null).Ok.Should().BeFalse();

    [Fact]
    public void A_name_with_no_usable_identifier_is_refused()
    {
        var result = FunctionAppService.Validate(Candidate("———"), [], null);

        result.Ok.Should().BeFalse();
        result.MessageFa.Should().NotBeNullOrWhiteSpace("the panel refuses in both languages it speaks");
    }

    [Fact]
    public void Two_functions_cannot_share_a_name() =>
        FunctionAppService.Validate(Candidate("hello"), [Existing("hello")], null).Ok.Should().BeFalse();

    [Fact]
    public void Two_http_functions_cannot_share_a_route()
    {
        // The dispatcher would give every request to one of them and never say which.
        var result = FunctionAppService.Validate(
            Candidate("second", route: "hooks"), [Existing("first", route: "hooks")], null);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("hooks");
    }

    [Fact]
    public void A_default_route_collides_with_a_matching_explicit_one()
    {
        // "hello" defaults to its own slug, which is exactly what the other one asked for by hand.
        var result = FunctionAppService.Validate(
            Candidate("hello"), [Existing("other", route: "hello")], null);

        result.Ok.Should().BeFalse();
    }

    [Fact]
    public void Renaming_a_function_does_not_collide_with_itself()
    {
        var existing = Existing("hello");
        var edited = Candidate("hello");
        edited.Id = existing.Id;

        FunctionAppService.Validate(edited, [existing], editingId: existing.Id).Ok.Should().BeTrue();
    }

    [Fact]
    public void An_unreadable_schedule_is_refused_with_the_parsers_own_reason()
    {
        var result = FunctionAppService.Validate(
            Candidate("nightly", FunctionTrigger.Cron, cron: "not a schedule"), [], null);

        result.Ok.Should().BeFalse();
        result.Field.Should().Be("CronExpression");
    }

    [Fact]
    public void A_readable_schedule_is_accepted() =>
        FunctionAppService.Validate(Candidate("nightly", FunctionTrigger.Cron, cron: "0 3 * * *"), [], null)
            .Ok.Should().BeTrue();

    [Fact]
    public void An_event_nothing_publishes_is_refused()
    {
        // A subscription to a key no call site raises is a function that never runs and never
        // errors — this feature's worst failure mode, refused at the moment it is typed.
        FunctionAppService.Validate(
            Candidate("on-thing", FunctionTrigger.Event, eventKey: "nothing.raises.this"), [], null)
            .Ok.Should().BeFalse();
    }

    [Fact]
    public void A_known_event_is_accepted() =>
        FunctionAppService.Validate(
            Candidate("on-deploy", FunctionTrigger.Event, eventKey: FunctionEvents.DeploymentSucceeded), [], null)
            .Ok.Should().BeTrue();

    [Fact]
    public void A_custom_event_key_is_accepted_even_though_it_is_not_in_the_fixed_catalog()
    {
        // F3, 2026-08-21 functions-and-services plan: custom.* is not fixed code like the platform's
        // own vocabulary — it is whatever a workspace's own apps choose to raise, so it cannot live
        // in FunctionEvents.All, and this refusal must not treat "not in the catalog" as "nothing
        // raises this" for a key under the customer namespace.
        FunctionAppService.Validate(
            Candidate("on-order-paid", FunctionTrigger.Event, eventKey: "custom.order.paid"), [], null)
            .Ok.Should().BeTrue();
    }

    [Fact]
    public void A_function_with_no_code_is_refused() =>
        FunctionAppService.Validate(Candidate("empty", code: ""), [], null).Ok.Should().BeFalse();

    [Fact]
    public void Two_cron_functions_may_share_a_schedule()
    {
        // Only routes are addresses. Two jobs at 03:00 is an ordinary thing to want.
        var result = FunctionAppService.Validate(
            Candidate("b", FunctionTrigger.Cron, cron: "0 3 * * *"),
            [Existing("a", FunctionTrigger.Cron)], null);

        result.Ok.Should().BeTrue();
    }
}

/// <summary>
/// Which platform happenings a function can be woken by.
/// </summary>
public class FunctionEventCatalogueTests
{
    [Fact]
    public void Event_keys_are_unique() =>
        FunctionEvents.All.Select(e => e.Key).Should().OnlyHaveUniqueItems();

    [Fact]
    public void Every_event_is_named_in_both_languages() =>
        FunctionEvents.All.Should().OnlyContain(e => e.NameEn.Length > 0 && e.NameFa.Length > 0);

    [Theory]
    [InlineData(AlertEvent.DeployFailed)]
    [InlineData(AlertEvent.AppCrashed)]
    [InlineData(AlertEvent.BackupFailed)]
    [InlineData(AlertEvent.DiskWarning)]
    [InlineData(AlertEvent.ThresholdBreached)]
    [InlineData(AlertEvent.LowBalance)]
    [InlineData(AlertEvent.SslExpiring)]
    public void Each_alert_the_platform_raises_maps_to_a_subscribable_event(AlertEvent alert)
    {
        // The bridge is what keeps the two in step: an alert with no event would mean a human is
        // emailed about something no function was ever told about.
        var key = FunctionEvents.ForAlert(alert);

        key.Should().NotBeNull();
        FunctionEvents.IsKnown(key).Should().BeTrue();
    }

    [Fact]
    public void The_notification_test_button_wakes_nobodys_code()
    {
        // It exists so an operator can prove a channel works. Firing customer code from a
        // connectivity test would make that button unsafe to press.
        FunctionEvents.ForAlert(AlertEvent.Test).Should().BeNull();
    }
}
