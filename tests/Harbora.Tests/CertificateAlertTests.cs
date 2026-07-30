using System.Globalization;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The SSL-expiry alert.
///
/// The rule existed in the UI (a checkbox), and in the notification router (a branch) — and nowhere
/// else. Nothing in the codebase ever raised <c>AlertEvent.SslExpiring</c>, so ticking the box
/// promised something that could not happen.
/// </summary>
public class CertificateAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    private static CertificateAlertMessage? Evaluate(int? expiresInDays) =>
        CertificateAlert.Evaluate("shop.example.com", "Shop",
            expiresInDays is null ? null : Now.AddDays(expiresInDays.Value), Now);

    [Fact]
    public void A_healthy_certificate_says_nothing()
    {
        // Renewal happens at 30 days remaining, so anything above the window is working as intended.
        Evaluate(60).Should().BeNull();
        Evaluate(15).Should().BeNull();
    }

    [Fact]
    public void A_certificate_inside_the_renewal_window_warns_and_names_the_likely_cause()
    {
        var alert = Evaluate(10);

        alert.Should().NotBeNull();
        alert!.Severity.Should().Be(AlertSeverity.Warning);
        alert.Headline.Should().Contain("shop.example.com");
        alert.Detail.Should().Contain("10 days").And.Contain("2026-08-09");
        alert.Detail.Should().Contain("port 80",
            "renewal this late means the HTTP-01 challenge is failing, which is actionable");
    }

    [Fact]
    public void An_expired_certificate_is_critical_and_says_users_are_affected_now()
    {
        var alert = Evaluate(-2);

        alert.Should().NotBeNull();
        alert!.Severity.Should().Be(AlertSeverity.Critical);
        alert.Detail.Should().Contain("2026-07-28").And.Contain("security warning");
    }

    [Fact]
    public void A_domain_with_no_certificate_is_not_an_expiry_alert()
    {
        // A brand-new domain has no certificate yet. Alerting here would page someone every time an
        // app was created — and the domain checker already explains that case properly.
        Evaluate(null).Should().BeNull();
    }

    [Fact]
    public void The_date_is_gregorian_whatever_the_reader_uses()
    {
        // Alerts go to webhooks, Telegram and email. Under a fa-IR culture the same instant formats
        // as 1405-05-18, which is unreadable to everything downstream.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");
            Evaluate(-2)!.Detail.Should().Contain("2026-07-28");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
