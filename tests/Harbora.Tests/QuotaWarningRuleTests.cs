using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// C1 (2026-08-27 "warn before the refusal"): pure logic over a <see cref="WorkspaceUsage"/> snapshot
/// — no database, because <see cref="QuotaWarningRule.Breaches"/> is deliberately just a function of
/// the exact record <see cref="IQuotaService.GetUsageAsync"/> already returns.
/// </summary>
public class QuotaWarningRuleTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static WorkspaceUsage Usage(
        int apps = 0, int maxApps = 0,
        int services = 0, int maxServices = 0,
        long memoryUsed = 0, long maxMemory = 0,
        double cpuUsed = 0, double maxCpu = 0,
        long diskUsed = 0, long maxDisk = 0, int diskUnmeasured = 0,
        int members = 0, int maxMembers = 0) =>
        new("Test",
            apps, maxApps, services, maxServices, memoryUsed, maxMemory, cpuUsed, maxCpu,
            Suspended: false,
            DiskUsedBytes: diskUsed, MaxDiskBytes: maxDisk, DiskUnmeasured: diskUnmeasured,
            Members: members, MaxMembers: maxMembers);

    [Fact]
    public void A_resource_at_exactly_the_warn_ratio_is_reported()
    {
        var usage = Usage(apps: 8, maxApps: 10);

        var breaches = QuotaWarningRule.Breaches(usage, warnRatio: 0.8);

        breaches.Should().ContainSingle(b => b.ResourceEn == "Apps" && b.Percent == 80 && b.Detail == "8/10");
    }

    [Fact]
    public void A_resource_just_under_the_warn_ratio_is_not_reported()
    {
        var usage = Usage(apps: 7, maxApps: 10);

        var breaches = QuotaWarningRule.Breaches(usage, warnRatio: 0.8);

        breaches.Should().BeEmpty();
    }

    [Fact]
    public void Every_watched_resource_close_to_its_cap_is_reported_at_once()
    {
        var usage = Usage(
            apps: 9, maxApps: 10,
            memoryUsed: 9 * Gb, maxMemory: 10 * Gb,
            cpuUsed: 1, maxCpu: 4);

        var breaches = QuotaWarningRule.Breaches(usage, warnRatio: 0.8);

        breaches.Select(b => b.ResourceEn).Should().Contain(["Apps", "Memory"])
            .And.NotContain("CPU", "CPU sits at 25%, well under the 80% line");
    }

    [Fact]
    public void An_unlimited_cap_is_never_reported_however_high_usage_looks()
    {
        // MaxApps = 0 means unlimited, the same convention every plan cap in this codebase uses —
        // there is no ceiling for 500 apps to be "close" to.
        var usage = Usage(apps: 500, maxApps: 0);

        QuotaWarningRule.Breaches(usage, warnRatio: 0.8).Should().BeEmpty();
    }

    [Fact]
    public void A_workspace_with_no_plan_at_all_reports_nothing()
    {
        // GetUsageAsync answers every Max* as 0 when there is neither an assigned plan nor a default
        // one — indistinguishable, on purpose, from an explicitly unlimited plan.
        var usage = Usage();

        QuotaWarningRule.Breaches(usage, warnRatio: 0.8).Should().BeEmpty();
    }

    [Fact]
    public void Disk_with_any_unmeasured_volume_is_skipped_even_when_the_measured_part_alone_would_breach()
    {
        // 9/10 GB measured would be 90% on its own, but DiskUnmeasured > 0 means the true figure could
        // be higher still — reporting the partial sum as fact is exactly the "not measured" lie this
        // guards against (DiskQuota.Caveat draws the same line for the same reason).
        var usage = Usage(diskUsed: 9 * Gb, maxDisk: 10 * Gb, diskUnmeasured: 1);

        QuotaWarningRule.Breaches(usage, warnRatio: 0.8).Should().BeEmpty();
    }

    [Fact]
    public void Disk_with_nothing_unmeasured_is_reported_like_every_other_resource()
    {
        var usage = Usage(diskUsed: 9 * Gb, maxDisk: 10 * Gb, diskUnmeasured: 0);

        var breaches = QuotaWarningRule.Breaches(usage, warnRatio: 0.8);

        breaches.Should().ContainSingle(b => b.ResourceEn == "Disk" && b.Percent == 90);
    }

    [Fact]
    public void A_governance_ceiling_like_members_is_never_reported_even_when_maxed_out()
    {
        // Members/Projects/Environments/Domains/Volumes/BackupSchedules/CronJobs are deliberately not
        // wired into Breaches at all — QuotaService's own comment calls them "governance ceilings, not
        // metered capacity". A workspace sitting at its member cap with an otherwise empty plan must
        // report nothing.
        var usage = Usage(members: 5, maxMembers: 5);

        QuotaWarningRule.Breaches(usage, warnRatio: 0.8).Should().BeEmpty();
    }

    [Fact]
    public void A_lower_configured_ratio_catches_a_resource_the_default_would_miss()
    {
        var usage = Usage(apps: 6, maxApps: 10); // 60%

        QuotaWarningRule.Breaches(usage, warnRatio: 0.8).Should().BeEmpty();
        QuotaWarningRule.Breaches(usage, warnRatio: 0.5).Should().ContainSingle(b => b.ResourceEn == "Apps");
    }
}
