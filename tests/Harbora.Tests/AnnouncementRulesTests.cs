using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Platform;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The rules an announcement's fields and active window are measured against, read without a
/// request or a database — the same shape <c>SupportAccessTests</c> already established for its own
/// pure rules class.
/// </summary>
public class AnnouncementRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private const string Title = "Scheduled maintenance";
    private const string Body = "The panel will be briefly unavailable tonight.";
    private const string TitleFa = "تعمیرات برنامه‌ریزی‌شده";
    private const string BodyFa = "پنل امشب برای مدت کوتاهی در دسترس نخواهد بود.";

    // --- both languages are required, not optional -------------------------------------------------

    [Fact]
    public void A_missing_english_title_is_refused() =>
        AnnouncementRules.RefuseSave("", Body, TitleFa, BodyFa, AlertSeverity.Info, null, null)
            .Should().NotBeNull();

    [Fact]
    public void A_missing_english_body_is_refused() =>
        AnnouncementRules.RefuseSave(Title, "  ", TitleFa, BodyFa, AlertSeverity.Info, null, null)
            .Should().NotBeNull();

    [Fact]
    public void A_missing_persian_title_is_refused() =>
        AnnouncementRules.RefuseSave(Title, Body, "", BodyFa, AlertSeverity.Info, null, null)
            .Should().NotBeNull();

    [Fact]
    public void A_missing_persian_body_is_refused() =>
        AnnouncementRules.RefuseSave(Title, Body, TitleFa, "", AlertSeverity.Info, null, null)
            .Should().NotBeNull();

    [Fact]
    public void Both_languages_present_is_allowed() =>
        AnnouncementRules.RefuseSave(Title, Body, TitleFa, BodyFa, AlertSeverity.Info, null, null)
            .Should().BeNull();

    // --- severity is exactly Info or Warning, never Critical ----------------------------------------

    [Fact]
    public void Info_severity_is_allowed() =>
        AnnouncementRules.RefuseSave(Title, Body, TitleFa, BodyFa, AlertSeverity.Info, null, null)
            .Should().BeNull();

    [Fact]
    public void Warning_severity_is_allowed() =>
        AnnouncementRules.RefuseSave(Title, Body, TitleFa, BodyFa, AlertSeverity.Warning, null, null)
            .Should().BeNull();

    [Fact]
    public void Critical_severity_is_refused_because_nobody_designed_a_meaning_for_it_here() =>
        AnnouncementRules.RefuseSave(Title, Body, TitleFa, BodyFa, AlertSeverity.Critical, null, null)
            .Should().NotBeNull();

    // --- the window, when both bounds are given, must make sense ------------------------------------

    [Fact]
    public void An_end_before_its_own_start_is_refused() =>
        AnnouncementRules.RefuseSave(Title, Body, TitleFa, BodyFa, AlertSeverity.Info,
            Now, Now.AddHours(-1)).Should().NotBeNull();

    [Fact]
    public void An_end_equal_to_its_own_start_is_refused() =>
        AnnouncementRules.RefuseSave(Title, Body, TitleFa, BodyFa, AlertSeverity.Info,
            Now, Now).Should().NotBeNull();

    [Fact]
    public void An_end_after_its_own_start_is_allowed() =>
        AnnouncementRules.RefuseSave(Title, Body, TitleFa, BodyFa, AlertSeverity.Info,
            Now, Now.AddHours(1)).Should().BeNull();

    [Fact]
    public void No_window_at_all_is_allowed() =>
        AnnouncementRules.RefuseSave(Title, Body, TitleFa, BodyFa, AlertSeverity.Info, null, null)
            .Should().BeNull();

    // --- IsActiveAt: a null bound is open on that side, not "never" or "always" ---------------------

    private static Announcement GivenAnnouncement(DateTimeOffset? startsAt, DateTimeOffset? endsAt) => new()
    {
        Title = Title, Body = Body, TitleFa = TitleFa, BodyFa = BodyFa,
        StartsAt = startsAt, EndsAt = endsAt
    };

    [Fact]
    public void No_bounds_at_all_is_active_right_now() =>
        GivenAnnouncement(null, null).IsActiveAt(Now).Should().BeTrue();

    [Fact]
    public void A_start_in_the_future_is_not_active_yet() =>
        GivenAnnouncement(Now.AddHours(1), null).IsActiveAt(Now).Should().BeFalse();

    [Fact]
    public void A_start_exactly_now_is_already_active() =>
        GivenAnnouncement(Now, null).IsActiveAt(Now).Should().BeTrue();

    [Fact]
    public void An_end_in_the_past_is_no_longer_active() =>
        GivenAnnouncement(null, Now.AddHours(-1)).IsActiveAt(Now).Should().BeFalse();

    [Fact]
    public void An_end_exactly_now_is_still_active() =>
        GivenAnnouncement(null, Now).IsActiveAt(Now).Should().BeTrue();

    [Fact]
    public void Between_its_own_start_and_end_it_is_active() =>
        GivenAnnouncement(Now.AddHours(-1), Now.AddHours(1)).IsActiveAt(Now).Should().BeTrue();
}
