using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Jobs;
using Harbora.Infrastructure.Maintenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The whole guarantee behind Task 4's startup gate is one line in
/// <c>DependencyInjection.AddHarboraInfrastructure</c>: <see cref="JobStartupGateOpener"/> must be
/// registered as a hosted service after every startup reconciler, because hosted services start in
/// registration order and nothing in the type system enforces that ordering. Every other gate test
/// constructs <see cref="JobStartupGate"/> and <see cref="JobStartupGateOpener"/> by hand, so none of
/// them would notice that line moving — this is the one that actually reads the registration list.
/// </summary>
public class DependencyInjectionTests
{
    private static ServiceCollection BuildServices(params (string Key, string Value)[] settings)
    {
        // Registration refuses to run without a master key; the value itself is not what this test
        // is about, so any well-formed throwaway will do.
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
    public void The_gate_opens_after_every_startup_reconciler()
    {
        var services = BuildServices();

        // Registration order among descriptors of the same service type is preserved by
        // IServiceCollection, and .NET starts IHostedServices in that order — this list IS the
        // startup sequence the platform will run.
        var hostedServices = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        var openerIndex = hostedServices.IndexOf(typeof(JobStartupGateOpener));
        var jobReconcilerIndex = hostedServices.IndexOf(typeof(JobReconciler));
        var deploymentReconcilerIndex = hostedServices.IndexOf(typeof(DeploymentReconciler));

        openerIndex.Should().BeGreaterThan(-1, "the opener must be registered as a hosted service at all");
        jobReconcilerIndex.Should().BeGreaterThan(-1, "JobReconciler must be registered as a hosted service at all");
        deploymentReconcilerIndex.Should().BeGreaterThan(-1,
            "DeploymentReconciler must be registered as a hosted service at all");

        openerIndex.Should().BeGreaterThan(jobReconcilerIndex,
            "JobReconciler must finish settling orphaned Running jobs before the worker may claim anything");
        openerIndex.Should().BeGreaterThan(deploymentReconcilerIndex,
            "DeploymentReconciler must finish failing stranded deployments — and settling their jobs — " +
            "before the worker may claim work again");
    }

    [Fact]
    public void How_many_jobs_run_at_once_is_read_from_configuration()
    {
        // The rollback path has to be reachable from a config file and a restart. An option that is
        // registered but bound to the wrong section is a knob that turns and does nothing.
        var configured = BuildServices(("Jobs:MaxConcurrency", "7"))
            .BuildServiceProvider()
            .GetRequiredService<IOptions<JobQueueOptions>>().Value;

        configured.MaxConcurrency.Should().Be(7);
    }

    [Fact]
    public void An_install_that_says_nothing_about_the_queue_gets_the_default()
    {
        var unconfigured = BuildServices()
            .BuildServiceProvider()
            .GetRequiredService<IOptions<JobQueueOptions>>().Value;

        unconfigured.MaxConcurrency.Should().Be(JobQueueOptions.DefaultMaxConcurrency);
    }

    [Fact]
    public void The_retention_sweeper_starts_after_the_gate_because_it_is_not_a_startup_reconciler()
    {
        // It runs on a nightly timer and settles nothing the job worker is waiting on. Registering
        // it above the opener would put a delete pass on the boot path for no benefit, and would
        // hold up the worker behind it.
        var hostedServices = BuildServices()
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        var sweeperIndex = hostedServices.IndexOf(typeof(DataRetentionSweeper));
        var openerIndex = hostedServices.IndexOf(typeof(JobStartupGateOpener));

        sweeperIndex.Should().BeGreaterThan(-1, "the sweeper must be registered as a hosted service at all");
        sweeperIndex.Should().BeGreaterThan(openerIndex,
            "a timer-driven sweeper is not a startup reconciler and belongs below the gate opener");
    }

    [Fact]
    public void Every_retention_cutoff_is_readable_from_configuration()
    {
        // "An operator can see and change every cutoff" is the acceptance criterion, and an option
        // bound to the wrong section is a knob that turns and does nothing.
        var configured = BuildServices(
                ("Retention:DeploymentLogDays", "30"),
                ("Retention:AuditLogDays", "2555"),
                ("Retention:CronRunDays", "14"),
                ("Retention:NodeCommandDays", "45"),
                ("Retention:NodeEventDays", "45"),
                ("Retention:PasswordResetTokenDays", "3"),
                ("Retention:SweepHourUtc", "11"))
            .BuildServiceProvider()
            .GetRequiredService<IOptions<RetentionOptions>>().Value;

        configured.DeploymentLogDays.Should().Be(30);
        configured.AuditLogDays.Should().Be(2555);
        configured.CronRunDays.Should().Be(14);
        configured.NodeCommandDays.Should().Be(45);
        configured.NodeEventDays.Should().Be(45);
        configured.PasswordResetTokenDays.Should().Be(3);
        configured.SweepHourUtc.Should().Be(11);
    }

    [Fact]
    public void An_install_that_says_nothing_about_retention_gets_the_shipped_defaults()
    {
        var unconfigured = BuildServices()
            .BuildServiceProvider()
            .GetRequiredService<IOptions<RetentionOptions>>().Value;

        unconfigured.DeploymentLogDays.Should().Be(90);
        unconfigured.AuditLogDays.Should().Be(365);
        unconfigured.CronRunDays.Should().Be(90);
        unconfigured.NodeCommandDays.Should().Be(90);
        unconfigured.NodeEventDays.Should().Be(90);
        unconfigured.PasswordResetTokenDays.Should().Be(7);
        unconfigured.SweepHourUtc.Should().Be(3);
    }

    [Fact]
    public void The_defaults_an_operator_reads_in_appsettings_are_the_defaults_the_code_ships()
    {
        // The shipped file sets none of these keys, so the C# defaults are the single source of
        // truth for what they *are* — but the file still has to say what they are, or the only way
        // to discover a cutoff is to read the source. That left the values written twice again,
        // this time as prose beside code, with nothing comparing the two. This compares them.
        using var settings = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Harbora.Web", "appsettings.json")));
        var section = settings.RootElement.GetProperty(RetentionOptions.SectionName);

        var documented = section.GetProperty("_comment_defaults").GetString()!
            .TrimEnd('.')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(pair =>
            {
                // "DeploymentLogDays 90 days" would otherwise compare equal on "90" while saying
                // something the next reader has to interpret. A pair is a name and a number.
                pair.Should().HaveCount(2, "every documented default is one key and one value");
                return pair;
            })
            .ToDictionary(pair => pair[0], pair => pair[1]);

        var shipped = typeof(RetentionOptions).GetProperties()
            .ToDictionary(knob => knob.Name, knob => knob.GetValue(new RetentionOptions())!.ToString()!);

        documented.Should().Equal(shipped,
            "every retention knob has to be named in appsettings.json with the value it actually ships");
    }

    [Fact]
    public void The_shipped_retention_section_sets_no_key_at_all()
    {
        // The other half of the same guarantee. Comparing the comment against the C# defaults is
        // only worth anything while the file leaves the keys unset — add "AuditLogDays": 400 and
        // every test above still passes while the product ships 400 and the comment says 365.
        using var settings = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Harbora.Web", "appsettings.json")));

        settings.RootElement.GetProperty(RetentionOptions.SectionName).EnumerateObject()
            .Select(key => key.Name)
            .Should().OnlyContain(name => name.StartsWith("_comment"),
                "the section documents the knobs and sets none of them, so the C# defaults stay the "
                + "single source of truth for what an install actually does");
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
