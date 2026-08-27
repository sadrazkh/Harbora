using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Notifications;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="NotificationTemplateCatalog"/> itself — culture selection, the per-event branches
/// <see cref="NotificationTemplateCensusTests"/> only samples once each, and the HTML alternative
/// (N4, 2026-08-16 notification-system spec, "in the reader's own language").
/// </summary>
public class NotificationTemplateCatalogTests
{
    private static readonly NotificationTemplateCatalog Catalog = new();

    // ---- culture selection: default fa, never a thrown exception ----------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xx")] // unrecognised — this platform ships fa/en only (Program.cs's supportedCultures)
    [InlineData("fa")]
    [InlineData("FA")]
    public void An_unset_missing_or_unrecognised_culture_renders_the_platforms_own_default(string? culture)
    {
        // The default the field itself documents (User.cs:25) — not a guess this test makes up.
        var expected = Catalog.Render(NotificationEventData.Create(AlertEvent.DiskWarning,
            ("ServerName", "node-1"), ("Percent", "80")), "fa");

        var rendered = Catalog.Render(NotificationEventData.Create(AlertEvent.DiskWarning,
            ("ServerName", "node-1"), ("Percent", "80")), culture);

        rendered.Should().Be(expected);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    public void Only_en_case_insensitively_selects_English(string culture)
    {
        var data = NotificationEventData.Create(AlertEvent.DiskWarning, ("ServerName", "node-1"), ("Percent", "80"));

        Catalog.Render(data, culture).Subject.Should().Be("Low disk space");
    }

    // ---- a raise site's own fields actually reach the reader ---------------------------------

    [Fact]
    public void Deploy_failed_names_the_app_the_number_and_passes_the_reason_through_untranslated()
    {
        var rendered = Catalog.Render(NotificationEventData.Create(AlertEvent.DeployFailed,
            ("AppName", "checkout"), ("DeploymentNumber", "12"), ("Reason", "docker build exited with code 1")), "fa");

        rendered.Subject.Should().Contain("checkout").And.Contain("12");
        rendered.TextBody.Should().Contain("docker build exited with code 1",
            "a build's own log line is not prose this catalog can translate — it passes through as given");
    }

    [Fact]
    public void App_crashed_reads_a_machine_key_not_english_prose()
    {
        var looping = Catalog.Render(NotificationEventData.Create(AlertEvent.AppCrashed,
            ("AppName", "worker"), ("Reason", "CrashLooping")), "fa");
        var exited = Catalog.Render(NotificationEventData.Create(AlertEvent.AppCrashed,
            ("AppName", "worker"), ("Reason", "Exited")), "fa");

        looping.TextBody.Should().NotBe(exited.TextBody,
            "the two machine keys MetricsCollector actually sends must read as two different facts");
    }

    [Fact]
    public void Ssl_expiring_and_ssl_expired_are_two_different_messages()
    {
        var expiring = Catalog.Render(NotificationEventData.Create(AlertEvent.SslExpiring,
            ("Host", "shop.example.com"), ("AppName", "shop"), ("Expired", "false"),
            ("Days", "10"), ("ExpiryDate", "2026-08-30")), "en");
        var expired = Catalog.Render(NotificationEventData.Create(AlertEvent.SslExpiring,
            ("Host", "shop.example.com"), ("AppName", "shop"), ("Expired", "true"),
            ("Days", ""), ("ExpiryDate", "2026-07-28")), "en");

        expiring.Subject.Should().Be("Certificate expiring: shop.example.com");
        expiring.TextBody.Should().Contain("10 days");
        expired.Subject.Should().Be("Certificate expired: shop.example.com");
        expired.TextBody.Should().Contain("2026-07-28").And.Contain("security warning");
    }

    [Fact]
    public void Threshold_breached_reads_differently_for_a_restart_count_than_for_cpu_or_memory()
    {
        var restarts = Catalog.Render(NotificationEventData.Create(AlertEvent.ThresholdBreached,
            ("AppName", "api"), ("Metric", "RestartRate"), ("Observed", "6"),
            ("Threshold", "3"), ("SustainedMinutes", "10")), "en");
        var cpu = Catalog.Render(NotificationEventData.Create(AlertEvent.ThresholdBreached,
            ("AppName", "api"), ("Metric", "CpuPercent"), ("Threshold", "90"), ("SustainedMinutes", "5")), "en");
        var memory = Catalog.Render(NotificationEventData.Create(AlertEvent.ThresholdBreached,
            ("AppName", "api"), ("Metric", "MemoryPercent"), ("Threshold", "90"), ("SustainedMinutes", "5")), "en");

        restarts.Subject.Should().Contain("restart");
        cpu.Subject.Should().Contain("CPU");
        memory.Subject.Should().Contain("memory");
        cpu.Subject.Should().NotBe(memory.Subject, "the same threshold on two different metrics is two different facts");
    }

    [Fact]
    public void Threshold_breached_for_disk_names_the_volumes_own_limit_not_a_sustained_window()
    {
        var disk = Catalog.Render(NotificationEventData.Create(AlertEvent.ThresholdBreached,
            ("AppName", "api"), ("Metric", "DiskPercent"), ("Threshold", "90"), ("SustainedMinutes", "5")), "en");

        disk.Subject.Should().Contain("disk").And.Contain("90%");
        // Unlike CPU/memory's "held above X% for N minute(s)", disk has no sample window to be held
        // across — EvaluateDiskThresholdsAsync's own doc says why — so the sentence must not claim one.
        disk.TextBody.Should().NotContain("minute",
            "Volume.StorageBytes is a periodic measurement, not a live sample — there is no window it was held across");
    }

    [Fact]
    public void Quota_warning_carries_the_percent_in_the_subject_and_the_resource_list_in_the_body()
    {
        var rendered = Catalog.Render(NotificationEventData.Create(AlertEvent.QuotaWarning,
            ("Summary", "Apps at 92% (46/50), Memory at 81% (6.5 GB/8 GB)"),
            ("SummaryFa", "اپلیکیشن در 92٪ (46/50)، حافظه در 81٪ (6.5 GB/8 GB)"),
            ("Percent", "92")), "en");

        rendered.Subject.Should().Contain("92%");
        rendered.TextBody.Should().Contain("Apps at 92%").And.Contain("Memory at 81%");
    }

    [Fact]
    public void Backup_failed_degrades_gracefully_when_the_module_bridge_has_no_target_ref()
    {
        // BackupNotificationService (the backup module's bridge) leaves TargetRef blank and puts
        // everything into Detail — see NotificationTemplateCatalog.BackupFailed's own doc comment.
        var withTarget = Catalog.Render(NotificationEventData.Create(AlertEvent.BackupFailed,
            ("TargetRef", "primary-db"), ("Detail", "connection refused")), "en");
        var withoutTarget = Catalog.Render(NotificationEventData.Create(AlertEvent.BackupFailed,
            ("Detail", "Backup of primary-db failed verification: connection refused")), "en");

        withTarget.Subject.Should().Be("Backup failed: primary-db");
        withoutTarget.Subject.Should().Be("Backup failed");
        withoutTarget.TextBody.Should().Contain("connection refused");
    }

    [Fact]
    public void Low_balance_names_the_workspace_and_the_hours_left()
    {
        var rendered = Catalog.Render(NotificationEventData.Create(AlertEvent.LowBalance,
            ("WorkspaceName", "acme"), ("Hours", "22")), "fa");

        rendered.Subject.Should().Contain("acme");
        rendered.TextBody.Should().Contain("acme").And.Contain("22");
    }

    [Fact]
    public void Low_balance_also_says_when_that_runway_runs_out_in_both_languages()
    {
        // The hours figure and the date are the same runway, said two ways — BillingTick hands both
        // over already computed, and this template only has to place the date it was given.
        var data = NotificationEventData.Create(AlertEvent.LowBalance,
            ("WorkspaceName", "acme"), ("Hours", "22"), ("RunsOutOn", "2026-08-20"));

        Catalog.Render(data, "en").TextBody.Should().Contain("2026-08-20");
        Catalog.Render(data, "fa").TextBody.Should().Contain("2026-08-20");
    }

    [Fact]
    public void Low_balance_omits_the_runway_date_sentence_rather_than_printing_a_blank_one()
    {
        // BurnRate.RunwayDate has none to give when nothing is currently costing money, and
        // ReviewLowBalanceAsync hands over "" for it — RunningLow already guarantees the hour that
        // triggers a warning cost something, so this is a defensive case rather than one the product
        // reaches today, and it must degrade to silence rather than "runs out around ."
        var data = NotificationEventData.Create(AlertEvent.LowBalance,
            ("WorkspaceName", "acme"), ("Hours", "22"), ("RunsOutOn", ""));

        Catalog.Render(data, "en").TextBody.Should().NotContain("around .").And.NotContain("runs out around");
        Catalog.Render(data, "fa").TextBody.Should().NotContain("به پایان می‌رسد");
    }

    // ---- the HTML alternative -----------------------------------------------------------------

    [Fact]
    public void The_html_alternative_escapes_the_text_it_wraps()
    {
        // A deploy's own failure reason is untranslated free text (see the test above) and can
        // contain anything a build tool printed — including something that looks like markup. The
        // HTML alternative must not let that become live markup in a reader's mail client.
        var rendered = Catalog.Render(NotificationEventData.Create(AlertEvent.DeployFailed,
            ("AppName", "api"), ("DeploymentNumber", "1"), ("Reason", "<script>alert(1)</script>")), "en");

        rendered.HtmlBody.Should().NotContain("<script>");
        rendered.HtmlBody.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void The_html_alternative_wraps_each_paragraph_of_the_text_body()
    {
        var rendered = Catalog.Render(NotificationEventData.Create(AlertEvent.LowBalance,
            ("WorkspaceName", "acme"), ("Hours", "22")), "en");

        // LowBalance's own English text is one paragraph — a single <p> proves the wrapping actually
        // ran rather than the HtmlBody being some unrelated constant.
        rendered.HtmlBody.Should().StartWith("<div lang=\"en\" dir=\"ltr\"><p>").And.EndWith("</p></div>");
    }
}
