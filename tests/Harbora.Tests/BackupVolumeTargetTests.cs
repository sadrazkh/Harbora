using FluentAssertions;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Infrastructure;
using Harbora.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Backing up a Docker volume.
///
/// <para>
/// The panel runs in a container and cannot see a volume's host path, so the data is brought to it
/// by a helper that mounts the volume read-only. Docker itself is not available on the machine this
/// was written on, so what is asserted here is the orchestration — which mounts, which arguments,
/// and what happens to the staged copy afterwards. Whether the daemon honours it is a CI question.
/// </para>
/// </summary>
public sealed class BackupVolumeTargetTests : IDisposable
{
    private readonly string _root;
    private readonly FakeDockerEngine _docker = new();
    private readonly BackupModuleOptions _options;
    private readonly BackupTargetResolver _resolver;

    public BackupVolumeTargetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbora-volume-target", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _options = new BackupModuleOptions
        {
            StagingDirectory = Path.Combine(_root, "staging"),
            StagingVolume = "harbora_backups",
            HelperImage = "alpine:3.20",
            AllowedSourceRoots = [Path.Combine(_root, "allowed")]
        };
        Directory.CreateDirectory(_options.StagingDirectory);
        Directory.CreateDirectory(_options.AllowedSourceRoots[0]);

        _resolver = new BackupTargetResolver(
            _docker, Options.Create(_options), NullLogger<BackupTargetResolver>.Instance);
    }

    [Fact]
    public void Accepts_a_valid_volume_name_without_touching_docker()
    {
        var result = _resolver.Validate(BackupTargetType.DockerVolume, "harbora_app_data");

        result.Succeeded.Should().BeTrue(result.Error);
        _docker.OneOffRequests.Should().BeEmpty(
            "queue-time validation must not stage a 200 GB volume inside an HTTP request");
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("vol; rm -rf /")]
    [InlineData("-v")]
    [InlineData("")]
    public void Refuses_a_volume_name_that_is_not_a_name(string volumeName)
    {
        _resolver.Validate(BackupTargetType.DockerVolume, volumeName).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Stages_the_volume_through_a_helper_that_mounts_it_read_only()
    {
        await using var lease = await _resolver.AcquireAsync(
            BackupTargetType.DockerVolume, "harbora_app_data", default);

        lease.Succeeded.Should().BeTrue(lease.Error);
        lease.SourcePath.Should().StartWith(_options.StagingDirectory);
        Directory.Exists(lease.SourcePath).Should().BeTrue();

        var request = _docker.OneOffRequests.Should().ContainSingle().Subject;
        request.Image.Should().Be("alpine:3.20");

        var source = request.Binds.Should().ContainSingle(b => b.Source == "harbora_app_data").Subject;
        source.ReadOnly.Should().BeTrue("a backup must not be able to modify what it is reading");
        source.Target.Should().Be("/data");

        var staging = request.Binds.Should().ContainSingle(b => b.Source == "harbora_backups").Subject;
        staging.ReadOnly.Should().BeFalse();
    }

    /// <summary>
    /// The platform's older helper builds a shell string; this module does not. Without a shell,
    /// a volume name containing <c>;</c> is a name (THREAT_MODEL T1).
    /// </summary>
    [Fact]
    public async Task Invokes_the_copy_directly_rather_than_through_a_shell()
    {
        await using var lease = await _resolver.AcquireAsync(
            BackupTargetType.DockerVolume, "harbora_app_data", default);

        var command = _docker.OneOffRequests.Single().Command;

        command[0].Should().Be("cp");
        command.Should().NotContain("sh");
        command.Should().NotContain("-c");
        command.Should().NotContain(a => a.Contains("&&", StringComparison.Ordinal));
    }

    /// <summary>
    /// A staged copy is plaintext application data. It must not outlive the backup that needed it.
    /// </summary>
    [Fact]
    public async Task Removes_the_staged_copy_when_the_lease_is_released()
    {
        string stagedPath;

        await using (var lease = await _resolver.AcquireAsync(
            BackupTargetType.DockerVolume, "harbora_app_data", default))
        {
            stagedPath = lease.SourcePath!;
            await File.WriteAllTextAsync(Path.Combine(stagedPath, "data.txt"), "application data");
            Directory.Exists(stagedPath).Should().BeTrue();
        }

        Directory.Exists(stagedPath).Should().BeFalse("the staged copy must be gone once released");
    }

    [Fact]
    public async Task Reports_a_failing_helper_and_leaves_nothing_behind()
    {
        _docker.OneOffExitCode = 1;

        var before = Directory.GetDirectories(_options.StagingDirectory).Length;

        await using var lease = await _resolver.AcquireAsync(
            BackupTargetType.DockerVolume, "harbora_app_data", default);

        lease.Succeeded.Should().BeFalse();
        lease.Error.Should().Contain("could not be read");
        Directory.GetDirectories(_options.StagingDirectory).Should().HaveCount(before,
            "a failed staging attempt must not leave a partial copy on disk");
    }

    [Fact]
    public async Task Each_staging_run_gets_its_own_directory()
    {
        await using var first = await _resolver.AcquireAsync(
            BackupTargetType.DockerVolume, "harbora_app_data", default);
        await using var second = await _resolver.AcquireAsync(
            BackupTargetType.DockerVolume, "harbora_app_data", default);

        first.SourcePath.Should().NotBe(second.SourcePath,
            "two concurrent backups of one volume must not write into the same staging directory");
    }

    [Fact]
    public async Task A_directory_target_needs_no_helper_and_no_cleanup()
    {
        var path = _options.AllowedSourceRoots[0];

        await using var lease = await _resolver.AcquireAsync(BackupTargetType.Directory, path, default);

        lease.Succeeded.Should().BeTrue(lease.Error);
        lease.SourcePath.Should().Be(Path.GetFullPath(path));
        _docker.OneOffRequests.Should().BeEmpty();

        await lease.DisposeAsync();
        Directory.Exists(path).Should().BeTrue("releasing a directory lease must not delete the source");
    }

    [Theory]
    [InlineData(BackupTargetType.Application)]
    [InlineData(BackupTargetType.Database)]
    public void Refuses_target_types_that_are_not_implemented_rather_than_guessing(BackupTargetType type)
    {
        var result = _resolver.Validate(type, "anything");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("not implemented");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
    }
}
