using System.Security.Cryptography;
using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Jobs;
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
}
