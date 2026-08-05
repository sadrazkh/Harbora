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
            _docker, new StubDatabaseStager(), new StubApplicationStager(), Options.Create(_options),
            NullLogger<BackupTargetResolver>.Instance);
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
    [InlineData(BackupTargetType.Server)]
    [InlineData(BackupTargetType.Device)]
    [InlineData(BackupTargetType.Configuration)]
    public void Refuses_target_types_it_cannot_read_rather_than_guessing(BackupTargetType type)
    {
        var result = _resolver.Validate(type, "anything");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("not a target this module can read");
    }

    [Fact]
    public void An_application_target_is_checked_for_shape_only()
    {
        _resolver.Validate(BackupTargetType.Application, Guid.CreateVersion7().ToString())
            .Succeeded.Should().BeTrue();

        _resolver.Validate(BackupTargetType.Application, "the-main-app")
            .Error.Should().Contain("id");
    }

    /// <summary>
    /// Only the shape is checked at queue time. Whether the service exists, which engine it runs and
    /// whether its password still decrypts all need the database and the master key, and this method
    /// is the one that must stay free of side effects.
    /// </summary>
    [Fact]
    public void A_database_target_is_checked_for_shape_only()
    {
        _resolver.Validate(BackupTargetType.Database, Guid.CreateVersion7().ToString())
            .Succeeded.Should().BeTrue();

        var malformed = _resolver.Validate(BackupTargetType.Database, "the-production-one");
        malformed.Succeeded.Should().BeFalse();
        malformed.Error.Should().Contain("id");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
    }
}

/// <summary>
/// Stands in for the database stager where a test is about some other target. Database staging has
/// its own tests; letting it be exercised incidentally here would only make failures ambiguous.
/// </summary>
internal sealed class StubDatabaseStager : IDatabaseTargetStager
{
    public Task<(DatabasePlan? Plan, string? Error)> PlanAsync(Guid serviceId, CancellationToken ct)
        => Task.FromResult<(DatabasePlan?, string?)>((null, "not used in this test"));

    public Task<TargetLease> StageAsync(Guid serviceId, CancellationToken ct)
        => Task.FromResult(TargetLease.Fail("not used in this test"));
}

/// <summary>Stands in for the application stager where a test is about some other target.</summary>
internal sealed class StubApplicationStager : IApplicationTargetStager
{
    public Task<(bool Ok, string? Error)> ValidateAsync(Guid appId, CancellationToken ct)
        => Task.FromResult((false, (string?)"not used in this test"));

    public Task<TargetLease> StageAsync(Guid appId, CancellationToken ct)
        => Task.FromResult(TargetLease.Fail("not used in this test"));
}

/// <summary>Stands in for the database half of a restore, for tests about the generic path.</summary>
internal sealed class StubDatabaseRestores : IDatabaseRestoreExecutor
{
    public string? Name { get; init; }
    public List<(Guid ServiceId, string Directory)> Loaded { get; } = [];

    public Task<DatabaseRestoreResult> LoadAsync(Guid serviceId, string restoredDirectory, CancellationToken ct)
    {
        Loaded.Add((serviceId, restoredDirectory));
        return Task.FromResult(new DatabaseRestoreResult(true));
    }

    public Task<string?> DescribeAsync(Guid serviceId, CancellationToken ct) => Task.FromResult(Name);
}
