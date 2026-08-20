using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Notifications;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Every <see cref="AlertEvent"/> has a real template, in both languages this platform ships (N4,
/// 2026-08-16 notification-system spec, "in the reader's own language").
///
/// <para>
/// Reads <see cref="AlertEvent"/> itself via <c>Enum.GetValues</c> rather than a hand-kept list of
/// event names — the same reasoning <c>DetailTabCensusTests</c> and <c>AppAddressCensusTests</c> give
/// for reading source rather than a list a reviewer has to remember to update: a hand-kept list is
/// checked by a reviewer noticing an addition is missing from it, and a reviewer noticing is exactly
/// the step a real gap slips past. Append an eighth <see cref="AlertEvent"/> without a same-day case
/// in <see cref="NotificationTemplateCatalog"/> and this is what notices, the same day, rather than
/// the first time a Persian reader gets "رویداد: NewThing" in their inbox.
/// </para>
///
/// <para>
/// <see cref="AlertEvent.Test"/> is the one deliberate exemption: it never reaches
/// <c>NotificationTemplateCatalog</c> at all — <c>NotificationService.SendTestAsync</c> sends its own
/// literal, unlocalised ping so the panel's Test button shows the server's own words immediately (see
/// that class's doc comment) — so a template for it would be dead code, not coverage.
/// </para>
/// </summary>
public class NotificationTemplateCensusTests
{
    private static readonly NotificationTemplateCatalog Catalog = new();

    /// <summary>Every real event kind — everything <see cref="AlertEvent"/> declares except the one
    /// documented exemption above.</summary>
    public static IEnumerable<object[]> RealEvents() =>
        Enum.GetValues<AlertEvent>()
            .Where(e => e != AlertEvent.Test)
            .Select(e => new object[] { e });

    [Fact]
    public void The_exemption_list_is_not_quietly_hiding_the_whole_enum()
    {
        // A `RealEvents` that had somehow collapsed to nothing would make every [MemberData] test
        // below vacuously pass — this is what would notice.
        RealEvents().Should().NotBeEmpty();
        RealEvents().Should().HaveCountGreaterThanOrEqualTo(7,
            "doc 09's catalog and the six-condition M4 table between them name at least this many");
    }

    [Theory]
    [MemberData(nameof(RealEvents))]
    public void Every_real_event_renders_a_real_template_in_Persian(AlertEvent evt)
    {
        var rendered = Catalog.Render(SampleData(evt), "fa");

        rendered.Subject.Should().NotBeNullOrWhiteSpace();
        rendered.Subject.Should().NotStartWith("رویداد:",
            $"{evt} fell through to the catalog's own fallback case instead of a real template");
        rendered.TextBody.Should().NotBeNullOrWhiteSpace($"{evt} has a subject but nothing to say");
        rendered.HtmlBody.Should().Contain("dir=\"rtl\"", "Persian is right-to-left");
    }

    [Theory]
    [MemberData(nameof(RealEvents))]
    public void Every_real_event_renders_a_real_template_in_English(AlertEvent evt)
    {
        var rendered = Catalog.Render(SampleData(evt), "en");

        rendered.Subject.Should().NotBeNullOrWhiteSpace();
        rendered.Subject.Should().NotStartWith("Event:",
            $"{evt} fell through to the catalog's own fallback case instead of a real template");
        rendered.TextBody.Should().NotBeNullOrWhiteSpace($"{evt} has a subject but nothing to say");
        rendered.HtmlBody.Should().Contain("dir=\"ltr\"", "English is left-to-right");
    }

    [Theory]
    [MemberData(nameof(RealEvents))]
    public void Every_real_event_reads_differently_in_each_language(AlertEvent evt)
    {
        // The census's own point, stated as an assertion: a template that silently forgot to branch on
        // culture (both cases returning the same English sentence, say) would still pass the two tests
        // above — each only checks its own language exists, not that the other one differs from it.
        var data = SampleData(evt);
        Catalog.Render(data, "fa").Subject.Should().NotBe(Catalog.Render(data, "en").Subject,
            $"{evt} must not read identically in Persian and English");
    }

    /// <summary>
    /// Representative fields for one event — enough for every branch inside that event's own template
    /// (a metric's two shapes for <see cref="AlertEvent.ThresholdBreached"/>, expired vs. expiring for
    /// <see cref="AlertEvent.SslExpiring"/>) to produce real prose rather than empty interpolations.
    /// Not exhaustive of every branch — <see cref="NotificationTemplateCatalogTests"/> is where each
    /// branch gets its own assertion — only enough that "did this event get a template at all" is a
    /// fair question to ask.
    /// </summary>
    private static NotificationEventData SampleData(AlertEvent evt) => evt switch
    {
        AlertEvent.DeployFailed => NotificationEventData.Create(evt,
            ("AppName", "api"), ("DeploymentNumber", "4"), ("Reason", "build error")),
        AlertEvent.AppCrashed => NotificationEventData.Create(evt, ("AppName", "worker"), ("Reason", "Exited")),
        AlertEvent.SslExpiring => NotificationEventData.Create(evt,
            ("Host", "shop.example.com"), ("AppName", "shop"), ("Expired", "false"),
            ("Days", "10"), ("ExpiryDate", "2026-08-30")),
        AlertEvent.DiskWarning => NotificationEventData.Create(evt, ("ServerName", "node-1"), ("Percent", "94")),
        AlertEvent.BackupFailed => NotificationEventData.Create(evt,
            ("TargetRef", "primary-db"), ("Detail", "connection refused")),
        AlertEvent.ThresholdBreached => NotificationEventData.Create(evt,
            ("AppName", "api"), ("Metric", "CpuPercent"), ("Threshold", "90"), ("SustainedMinutes", "5")),
        AlertEvent.LowBalance => NotificationEventData.Create(evt,
            ("WorkspaceName", "tenant"), ("Hours", "22"), ("RunsOutOn", "2026-08-20")),
        AlertEvent.ServiceProvisionFailed => NotificationEventData.Create(evt,
            ("ServiceName", "orders-db"), ("Reason", "image pull timed out")),
        AlertEvent.PlatformAnnouncement => NotificationEventData.Create(evt,
            ("Title", "Scheduled maintenance"), ("Body", "The panel will be briefly unavailable tonight."),
            ("TitleFa", "تعمیرات برنامه‌ریزی‌شده"), ("BodyFa", "پنل امشب برای مدت کوتاهی در دسترس نخواهد بود.")),
        _ => throw new InvalidOperationException(
            $"{evt} has no sample data yet — add a case here alongside its NotificationTemplateCatalog template.")
    };
}
