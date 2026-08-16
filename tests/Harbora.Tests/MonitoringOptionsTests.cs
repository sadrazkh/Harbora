using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Harbora.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The disk warn ratio, the disk-alert interval, the threshold repeat window, and the dashboard's own
/// backup-staleness figure — four numbers that used to be constants scattered across
/// <c>MetricsCollector</c>, <c>ThresholdRule</c> and <c>MonitoringController</c>, now one
/// <see cref="MonitoringOptions"/> section bound the way the other eleven options sections are
/// (<c>DependencyInjection.cs</c>).
///
/// <para>
/// This file only proves the wiring: that a key under <c>Monitoring:</c> actually reaches
/// <see cref="MonitoringOptions"/>, and that an installation which sets nothing gets exactly what the
/// constants used to be. Whether a configured value actually changes what fires is proved separately,
/// closer to the behaviour it changes — <c>MetricsCollectorOptionsTests</c> for the collector,
/// <c>MonitoringControllerBackupStalenessTests</c> for the dashboard banner.
/// </para>
/// </summary>
public class MonitoringOptionsTests
{
    private static ServiceCollection BuildServices(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["Harbora:MasterKey"] = Convert.ToBase64String(SHA256.HashData("tests"u8.ToArray()))
        };
        foreach (var (key, value) in settings) values[key] = value;

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        Harbora.Infrastructure.DependencyInjection.AddHarboraInfrastructure(services, config);
        return services;
    }

    [Fact]
    public void Every_monitoring_knob_is_readable_from_configuration()
    {
        var configured = BuildServices(
                ("Monitoring:DiskWarnRatio", "0.75"),
                ("Monitoring:DiskAlertIntervalHours", "2"),
                ("Monitoring:ThresholdRepeatAfterHours", "0.5"),
                ("Monitoring:BackupStalenessHours", "72"))
            .BuildServiceProvider()
            .GetRequiredService<IOptions<MonitoringOptions>>().Value;

        configured.DiskWarnRatio.Should().Be(0.75);
        configured.DiskAlertIntervalHours.Should().Be(2);
        configured.ThresholdRepeatAfterHours.Should().Be(0.5);
        configured.BackupStalenessHours.Should().Be(72);
    }

    [Fact]
    public void An_install_that_says_nothing_about_monitoring_gets_todays_constants()
    {
        // The acceptance test for "nothing changes for an installation that sets nothing": every
        // default here has to equal the constant it replaced.
        var unconfigured = BuildServices()
            .BuildServiceProvider()
            .GetRequiredService<IOptions<MonitoringOptions>>().Value;

        unconfigured.DiskWarnRatio.Should().Be(0.85, "MetricsCollector.DiskWarnRatio was 0.85");
        unconfigured.DiskAlertIntervalHours.Should().Be(1, "MetricsCollector.DiskAlertInterval was one hour");
        unconfigured.ThresholdRepeatAfterHours.Should().Be(1, "ThresholdRule.RepeatAfter was one hour");
        unconfigured.BackupStalenessHours.Should().Be(48, "MonitoringController compared against 48 hours");
    }

    [Fact]
    public void The_shipped_defaults_stay_pinned_to_ThresholdRules_own_constant()
    {
        // ThresholdRule.RepeatAfter is the pre-existing single source of truth for the repeat window;
        // MonitoringOptions derives its default from it rather than duplicating "1" as a second
        // literal that could quietly drift from the first.
        new MonitoringOptions().ThresholdRepeatAfterHours.Should().Be(ThresholdRule.RepeatAfter.TotalHours);
    }

    [Fact]
    public void The_defaults_an_operator_reads_in_appsettings_are_the_defaults_the_code_ships()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Harbora.Web", "appsettings.json")));
        var section = settings.RootElement.GetProperty(MonitoringOptions.SectionName);

        var documented = section.GetProperty("_comment_defaults").GetString()!
            .TrimEnd('.')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(pair =>
            {
                pair.Should().HaveCount(2, "every documented default is one key and one value");
                return pair;
            })
            .ToDictionary(pair => pair[0], pair => double.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture));

        var shipped = typeof(MonitoringOptions).GetProperties()
            .Where(p => p.CanWrite) // the two internal TimeSpan conveniences are read-only and derived
            .ToDictionary(
                knob => knob.Name,
                knob => Convert.ToDouble(knob.GetValue(new MonitoringOptions())));

        documented.Should().Equal(shipped,
            "every monitoring knob has to be named in appsettings.json with the value it actually ships");
    }

    [Fact]
    public void The_shipped_monitoring_section_sets_no_key_at_all()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Harbora.Web", "appsettings.json")));

        settings.RootElement.GetProperty(MonitoringOptions.SectionName).EnumerateObject()
            .Select(key => key.Name)
            .Should().OnlyContain(name => name.StartsWith("_comment"),
                "the section documents the knobs and sets none of them, so the C# defaults stay the "
                + "single source of truth for what an install actually does");
    }

    [Fact]
    public void The_options_documentation_names_all_three_backup_staleness_numbers_and_which_is_which()
    {
        // The spec's own requirement: a line in the options documentation saying which staleness
        // number is which, so the next person does not merge them.
        using var settings = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Harbora.Web", "appsettings.json")));
        var note = settings.RootElement.GetProperty(MonitoringOptions.SectionName)
            .GetProperty("_comment_backup_staleness").GetString()!;

        note.Should().Contain("VerificationSchedule.StaleAfter", "the 7-day verification staleness must be named and told apart");
        note.Should().Contain("StorageMeasurer.StaleAfter", "the 24-hour storage-measurement staleness must be named and told apart");
        note.Should().Contain("BackupStalenessHours", "the dashboard's own configurable figure must be named too");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return dir!.FullName;
    }
}
