using Docker.DotNet;
using FluentAssertions;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// The real Docker adapter, against a real daemon, using throwaway containers.
///
/// <para>
/// Everything above <see cref="IContainerRuntime"/> is tested with a fake, which is the right trade
/// for the hundreds of tests that care about policy and protocol rather than about Docker. What a
/// fake cannot check is whether <see cref="DockerContainerRuntime"/> speaks the Engine API
/// correctly — so these do, and they are the only tests that need a daemon.
/// </para>
///
/// <para>
/// They report an environmental skip where Docker is unreachable rather than failing: a developer on
/// a laptop without Docker should see "not run here", not a red suite. CI runs on a host with
/// Docker, so they execute there on every release.
/// </para>
/// </summary>
[Collection("docker")]
public sealed class DockerIntegrationTests : IAsyncLifetime
{
    /// <summary>Small, multi-arch, and has a shell — enough to exercise exec and logs.</summary>
    private const string TestImage = "docker.io/library/busybox:1.36";

    private readonly string _prefix = $"harbora-it-{Guid.NewGuid().ToString("n")[..8]}";
    private readonly List<string> _containers = [];
    private readonly List<string> _volumes = [];
    private readonly List<string> _networks = [];

    private IDockerClient? _client;
    private DockerContainerRuntime? _runtime;
    private string? _digestReference;

    public async Task InitializeAsync()
    {
        var endpoint = OperatingSystem.IsWindows()
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock";

        try
        {
            _client = new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
            _runtime = new DockerContainerRuntime(_client, NullLogger<DockerContainerRuntime>.Instance);

            var info = await _runtime.GetInfoAsync(CancellationToken.None);
            if (!info.Available) _runtime = null;
        }
        catch (Exception e) when (e is DockerApiException or HttpRequestException or IOException or TimeoutException or UriFormatException)
        {
            _runtime = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_runtime is null) { _client?.Dispose(); return; }

        foreach (var container in _containers)
            try { await _runtime.RemoveAsync(container, force: true, CancellationToken.None); } catch (Exception) { }

        foreach (var volume in _volumes)
            try { await _runtime.RemoveVolumeAsync(volume, CancellationToken.None); } catch (Exception) { }

        foreach (var network in _networks)
            try { await _runtime.RemoveNetworkAsync(network, CancellationToken.None); } catch (Exception) { }

        _client?.Dispose();
    }

    /// <summary>
    /// The runtime. Never null in a test body — <see cref="DockerFactAttribute"/> has already
    /// skipped the test when there is no daemon, so reaching here without one is a bug.
    /// </summary>
    private DockerContainerRuntime Runtime() =>
        _runtime ?? throw new InvalidOperationException(
            "The Docker probe said a daemon was reachable but the runtime could not be created.");

    /// <summary>
    /// Pull the test image and resolve it to a digest, so the rest of the test uses the same
    /// digest-pinned path a real deployment does.
    /// </summary>
    private async Task<string> PinnedImageAsync()
    {
        if (_digestReference is not null) return _digestReference;

        var runtime = Runtime();
        await runtime.PullImageAsync(TestImage, null, CancellationToken.None);

        var digest = await runtime.ResolveDigestAsync(TestImage, CancellationToken.None);
        digest.Should().NotBeNull("a pulled image must resolve to something");

        _digestReference = digest!.StartsWith("sha256:", StringComparison.Ordinal)
            ? $"docker.io/library/busybox@{digest}"
            : TestImage;

        return _digestReference;
    }

    private string Name(string suffix) => $"{_prefix}-{suffix}";

    [DockerFact]
    public async Task The_runtime_reports_the_daemon_version()
    {
        var info = await Runtime().GetInfoAsync(CancellationToken.None);

        info.Available.Should().BeTrue();
        info.Name.Should().Be("docker");
        info.Version.Should().NotBeNullOrWhiteSpace();
    }

    [DockerFact]
    public async Task An_image_pulled_by_tag_resolves_to_a_digest()
    {
        var reference = await PinnedImageAsync();

        reference.Should().Contain("busybox");
        (await Runtime().ResolveDigestAsync(reference, CancellationToken.None)).Should().NotBeNull();
    }

    [DockerFact]
    public async Task A_missing_image_resolves_to_null_rather_than_throwing()
    {
        (await Runtime().ResolveDigestAsync("docker.io/library/harbora-does-not-exist:1", CancellationToken.None))
            .Should().BeNull();
    }

    [DockerFact]
    public async Task A_container_is_created_started_inspected_and_removed()
    {
        var runtime = Runtime();
        var image = await PinnedImageAsync();
        var name = Name("lifecycle");

        var id = await runtime.CreateAndStartAsync(new ContainerCreateRequest
        {
            Name = name,
            ImageReference = image,
            Command = ["sleep", "300"],
            Labels = new Dictionary<string, string>
            {
                [NodeLabels.Managed] = "true",
                [NodeLabels.Tenant] = "integration",
            },
            Resources = new ResourceLimits { MemoryBytes = 64 * 1024 * 1024, PidsLimit = 64 },
        }, CancellationToken.None);

        _containers.Add(id);

        var inspected = await runtime.InspectAsync(name, CancellationToken.None);

        inspected.Should().NotBeNull();
        inspected!.State.Should().Be("running");
        inspected.Labels[NodeLabels.Tenant].Should().Be("integration");
        inspected.StartedAt.Should().NotBeNull("the timestamp is parsed from the daemon's format");

        await runtime.StopAsync(name, gracePeriodSeconds: 1, CancellationToken.None);
        (await runtime.InspectAsync(name, CancellationToken.None))!.State.Should().Be("exited");

        await runtime.RemoveAsync(name, force: true, CancellationToken.None);
        (await runtime.InspectAsync(name, CancellationToken.None)).Should().BeNull();

        _containers.Remove(id);
    }

    [DockerFact]
    public async Task Lifecycle_verbs_tolerate_a_container_that_is_already_gone()
    {
        // Every lifecycle verb on IContainerRuntime is idempotent by contract, and "it is already
        // gone" is success for all of them.
        var runtime = Runtime();
        var missing = Name("never-created");

        await runtime.StopAsync(missing, 1, CancellationToken.None);
        await runtime.RemoveAsync(missing, force: true, CancellationToken.None);
        await runtime.RestartAsync(missing, CancellationToken.None);
    }

    [DockerFact]
    public async Task Container_labels_survive_a_filtered_list()
    {
        var runtime = Runtime();
        var image = await PinnedImageAsync();
        var name = Name("labelled");

        var id = await runtime.CreateAndStartAsync(new ContainerCreateRequest
        {
            Name = name,
            ImageReference = image,
            Command = ["sleep", "300"],
            Labels = new Dictionary<string, string>
            {
                [NodeLabels.Managed] = "true",
                [NodeLabels.Tenant] = _prefix,
            },
            Resources = new ResourceLimits { MemoryBytes = 64 * 1024 * 1024 },
        }, CancellationToken.None);

        _containers.Add(id);

        var listed = await runtime.ListContainersAsync(
            new Dictionary<string, string> { [NodeLabels.Tenant] = _prefix }, includeStopped: true, CancellationToken.None);

        listed.Should().ContainSingle().Which.Name.Should().Be(name);
    }

    [DockerFact]
    public async Task Logs_can_be_read_back()
    {
        var runtime = Runtime();
        var image = await PinnedImageAsync();
        var name = Name("logs");

        var id = await runtime.CreateAndStartAsync(new ContainerCreateRequest
        {
            Name = name,
            ImageReference = image,
            Command = ["echo", "harbora-integration-marker"],
            Resources = new ResourceLimits { MemoryBytes = 64 * 1024 * 1024 },
        }, CancellationToken.None);

        _containers.Add(id);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var logs = await runtime.GetLogsAsync(name, tailLines: 50, CancellationToken.None);
        logs.Should().Contain("harbora-integration-marker");
    }

    [DockerFact]
    public async Task Exec_runs_an_argv_array_and_returns_its_exit_code_and_output()
    {
        // This is the path every database credential operation goes through.
        var runtime = Runtime();
        var image = await PinnedImageAsync();
        var name = Name("exec");

        var id = await runtime.CreateAndStartAsync(new ContainerCreateRequest
        {
            Name = name,
            ImageReference = image,
            Command = ["sleep", "300"],
            Resources = new ResourceLimits { MemoryBytes = 64 * 1024 * 1024 },
        }, CancellationToken.None);

        _containers.Add(id);

        var ok = await runtime.ExecAsync(name, ["echo", "from-exec"], null, null, CancellationToken.None);
        ok.ExitCode.Should().Be(0);
        ok.Stdout.Should().Contain("from-exec");

        var failed = await runtime.ExecAsync(name, ["false"], null, null, CancellationToken.None);
        failed.ExitCode.Should().NotBe(0);
    }

    [DockerFact]
    public async Task Exec_passes_the_environment_rather_than_the_command_line()
    {
        var runtime = Runtime();
        var image = await PinnedImageAsync();
        var name = Name("exec-env");

        var id = await runtime.CreateAndStartAsync(new ContainerCreateRequest
        {
            Name = name,
            ImageReference = image,
            Command = ["sleep", "300"],
            Resources = new ResourceLimits { MemoryBytes = 64 * 1024 * 1024 },
        }, CancellationToken.None);

        _containers.Add(id);

        var result = await runtime.ExecAsync(
            name, ["printenv", "SECRET_LIKE_VALUE"],
            new Dictionary<string, string> { ["SECRET_LIKE_VALUE"] = "not-on-the-command-line" },
            null, CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("not-on-the-command-line");
    }

    [DockerFact]
    public async Task Exec_can_take_a_script_on_stdin()
    {
        // How the PostgreSQL path keeps SQL — including a fresh password — off the command line.
        var runtime = Runtime();
        var image = await PinnedImageAsync();
        var name = Name("exec-stdin");

        var id = await runtime.CreateAndStartAsync(new ContainerCreateRequest
        {
            Name = name,
            ImageReference = image,
            Command = ["sleep", "300"],
            Resources = new ResourceLimits { MemoryBytes = 64 * 1024 * 1024 },
        }, CancellationToken.None);

        _containers.Add(id);

        var result = await runtime.ExecAsync(name, ["cat"], null, "piped-through-stdin\n", CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("piped-through-stdin");
    }

    [DockerFact]
    public async Task A_volume_is_created_found_and_removed()
    {
        var runtime = Runtime();
        var name = Name("volume");

        await runtime.EnsureVolumeAsync(name, new Dictionary<string, string> { [NodeLabels.Managed] = "true" }, CancellationToken.None);
        _volumes.Add(name);

        (await runtime.VolumeExistsAsync(name, CancellationToken.None)).Should().BeTrue();

        // Creating twice is the idempotent path, not an error.
        await runtime.EnsureVolumeAsync(name, new Dictionary<string, string>(), CancellationToken.None);

        await runtime.RemoveVolumeAsync(name, CancellationToken.None);
        _volumes.Remove(name);

        (await runtime.VolumeExistsAsync(name, CancellationToken.None)).Should().BeFalse();
    }

    [DockerFact]
    public async Task A_named_volume_is_mounted_and_its_contents_survive_the_container()
    {
        var runtime = Runtime();
        var image = await PinnedImageAsync();
        var volume = Name("persistent");

        await runtime.EnsureVolumeAsync(volume, new Dictionary<string, string>(), CancellationToken.None);
        _volumes.Add(volume);

        var writer = await runtime.CreateAndStartAsync(new ContainerCreateRequest
        {
            Name = Name("writer"),
            ImageReference = image,
            Command = ["sh", "-c", "echo persisted > /data/marker; sleep 300"],
            Mounts = [new VolumeMount(volume, "/data", ReadOnly: false)],
            Resources = new ResourceLimits { MemoryBytes = 64 * 1024 * 1024 },
        }, CancellationToken.None);

        _containers.Add(writer);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await runtime.RemoveAsync(writer, force: true, CancellationToken.None);
        _containers.Remove(writer);

        var reader = await runtime.CreateAndStartAsync(new ContainerCreateRequest
        {
            Name = Name("reader"),
            ImageReference = image,
            Command = ["sleep", "300"],
            Mounts = [new VolumeMount(volume, "/data", ReadOnly: true)],
            Resources = new ResourceLimits { MemoryBytes = 64 * 1024 * 1024 },
        }, CancellationToken.None);

        _containers.Add(reader);

        var result = await runtime.ExecAsync(Name("reader"), ["cat", "/data/marker"], null, null, CancellationToken.None);
        result.Stdout.Should().Contain("persisted");
    }

    [DockerFact]
    public async Task A_network_is_created_attached_and_removed()
    {
        var runtime = Runtime();
        var image = await PinnedImageAsync();
        var network = Name("net");

        await runtime.EnsureNetworkAsync(
            new NetworkSpec { Name = network },
            new Dictionary<string, string> { [NodeLabels.Managed] = "true" },
            CancellationToken.None);

        _networks.Add(network);

        // Creating twice must not fail — two commands racing to create a tenant network is normal.
        await runtime.EnsureNetworkAsync(new NetworkSpec { Name = network }, new Dictionary<string, string>(), CancellationToken.None);

        var id = await runtime.CreateAndStartAsync(new ContainerCreateRequest
        {
            Name = Name("networked"),
            ImageReference = image,
            Command = ["sleep", "300"],
            Network = network,
            NetworkAliases = ["db"],
            Resources = new ResourceLimits { MemoryBytes = 64 * 1024 * 1024 },
        }, CancellationToken.None);

        _containers.Add(id);

        var inspected = await runtime.InspectAsync(Name("networked"), CancellationToken.None);
        inspected!.NetworkIpAddresses.Should().ContainKey(network);
        inspected.NetworkIpAddresses[network].Should().NotBeNullOrWhiteSpace();
    }

    [DockerFact]
    public async Task A_one_off_helper_runs_to_completion_and_is_removed()
    {
        // The path volume archiving uses.
        var runtime = Runtime();
        var image = await PinnedImageAsync();
        var output = new List<string>();

        var exit = await runtime.RunOneOffAsync(new OneOffRequest
        {
            ImageReference = image,
            Command = ["echo", "one-off-output"],
            Resources = new ResourceLimits { MemoryBytes = 64 * 1024 * 1024 },
            TimeoutSeconds = 60,
        }, new Harbora.NodeAgent.Commands.InlineProgress<string>(output.Add), CancellationToken.None);

        exit.Should().Be(0);
        string.Join("\n", output).Should().Contain("one-off-output");

        var leftovers = await runtime.ListContainersAsync(null, includeStopped: true, CancellationToken.None);
        leftovers.Should().NotContain(c => c.Name.StartsWith(_prefix, StringComparison.Ordinal) && c.State == "exited");
    }

    [DockerFact]
    public async Task A_one_off_that_fails_returns_its_exit_code()
    {
        var exit = await Runtime().RunOneOffAsync(new OneOffRequest
        {
            ImageReference = await PinnedImageAsync(),
            Command = ["sh", "-c", "exit 42"],
            Resources = new ResourceLimits { MemoryBytes = 64 * 1024 * 1024 },
            TimeoutSeconds = 60,
        }, null, CancellationToken.None);

        exit.Should().Be(42);
    }

    [DockerFact]
    public async Task Pulling_an_image_that_does_not_exist_reports_a_contract_error_code()
    {
        var act = async () => await Runtime().PullImageAsync(
            "docker.io/library/harbora-definitely-not-a-real-image:0.0.0", null, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ContainerRuntimeException>();
        thrown.Which.Code.Should().Be(NodeErrorCode.ImagePullFailed);
    }

    [DockerFact]
    public async Task A_container_that_cannot_start_is_not_left_behind()
    {
        // A created-but-unstartable container blocks the next deploy by name collision.
        var runtime = Runtime();
        var name = Name("unstartable");

        var act = async () => await runtime.CreateAndStartAsync(new ContainerCreateRequest
        {
            Name = name,
            ImageReference = await PinnedImageAsync(),
            Command = ["/definitely/not/a/binary"],
            Resources = new ResourceLimits { MemoryBytes = 64 * 1024 * 1024 },
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ContainerRuntimeException>();

        (await runtime.InspectAsync(name, CancellationToken.None)).Should().BeNull();
    }
}

/// <summary>Docker tests share one daemon; running them in parallel makes cleanup racy.</summary>
[CollectionDefinition("docker", DisableParallelization = true)]
public sealed class DockerCollection;

/// <summary>
/// A fact that runs only where a Docker daemon is reachable, and reports an explicit skip reason
/// where one is not.
///
/// <para>
/// A skip rather than a pass, deliberately: a test that quietly succeeds because it did nothing is
/// worse than one that says it did not run. The probe happens once per test session.
/// </para>
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Unavailable = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public DockerFactAttribute()
    {
        if (Unavailable.Value is { } reason) Skip = reason;
    }

    /// <summary>Returns null when Docker answered, or the reason it did not.</summary>
    private static string? Probe()
    {
        var endpoint = OperatingSystem.IsWindows()
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock";

        try
        {
            using var client = new DockerClientConfiguration(new Uri(endpoint)).CreateClient();

            // Bounded: an unresponsive socket must not hold the whole suite.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _ = client.System.GetVersionAsync(timeout.Token).GetAwaiter().GetResult();

            return null;
        }
        catch (Exception e)
        {
            return $"No reachable Docker daemon at {endpoint} ({e.GetType().Name}). " +
                   "These are the only tests that need one; CI runs them on a host that has it.";
        }
    }
}
