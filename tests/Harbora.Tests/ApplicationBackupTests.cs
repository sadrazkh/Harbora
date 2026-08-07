using System.Text.Json;
using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Modules.Backup.Infrastructure;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Backing up an application: its data volumes and its definition, in one snapshot.
///
/// <para>
/// A volume backup restores the data and leaves you guessing at the image tag and the domains; a
/// config backup restores the definition with no data in it. These tests are about the two together
/// — and about the one thing that must NOT be in there.
/// </para>
/// </summary>
public sealed class ApplicationBackupTests : IDisposable
{
    private readonly string _root;
    private readonly HarboraDbContext _db;
    private readonly FakeDockerEngine _docker = new();
    private readonly BackupModuleOptions _options;
    private readonly ApplicationTargetStager _stager;
    private readonly Guid _workspace = Guid.CreateVersion7();

    /// <summary>
    /// The snapshot the staging is for. The staged directory is named from it (see
    /// <see cref="BackupStagingLayout"/>) so a crash mid-assembly leaves something the row can
    /// still point at.
    /// </summary>
    private readonly Guid _snapshot = Guid.CreateVersion7();

    public ApplicationBackupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbora-app-backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("app-backup-" + Guid.NewGuid()).Options);

        _options = new BackupModuleOptions
        {
            StagingDirectory = Path.Combine(_root, "staging"),
            StagingVolume = "harbora_backups",
            HelperImage = "alpine:3.20"
        };
        Directory.CreateDirectory(_options.StagingDirectory);

        _stager = new ApplicationTargetStager(
            _db, _docker, Options.Create(_options), NullLogger<ApplicationTargetStager>.Instance);
    }

    private App AddApp(params Volume[] volumes)
    {
        var app = new App
        {
            WorkspaceId = _workspace,
            Name = "Storefront",
            Slug = "storefront",
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/storefront:2.4.1",
            ContainerPort = 8080,
            HealthCheckPath = "/healthz",
            EnvironmentVariables =
            [
                new EnvironmentVariable { Key = "LOG_LEVEL", Value = "info", IsSecret = false },
                new EnvironmentVariable
                {
                    Key = "DATABASE_PASSWORD",
                    Value = "hunter2-the-real-secret",
                    IsSecret = true
                }
            ],
            Domains = [new DomainName { Host = "shop.example.com", SslEnabled = true, ForceHttps = true }],
            Volumes = [.. volumes]
        };

        _db.Apps.Add(app);
        _db.SaveChanges();
        return app;
    }

    private static Volume Vol(string name, string mount = "/data") =>
        new() { Name = name, MountPath = mount };

    private async Task<JsonElement> MetadataOfAsync(string stagePath)
    {
        var path = Path.Combine(stagePath, ApplicationTargetStager.MetadataFileName);
        File.Exists(path).Should().BeTrue();
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Named from the snapshot rather than from a Guid the stager invents, so the reconciler can
    /// find this directory from the row while the assembly is still running — the window in which
    /// the row's own <c>StagingPath</c> is still null.
    /// </summary>
    [Fact]
    public async Task The_assembled_copy_is_named_from_the_snapshot_so_a_crash_leaves_a_trail()
    {
        AddApp(Vol("storefront_data"));
        var app = await _db.Apps.FirstAsync();

        await using var lease = await _stager.StageAsync(app.Id, _snapshot, default);

        lease.SourcePath.Should().Be(Path.Combine(
            _options.StagingDirectory, BackupStagingLayout.ApplicationDirectory(_snapshot)));
    }

    // --- the thing that must never be in a backup -------------------------------------------------

    /// <summary>
    /// A backup is copied, downloaded and restored into places the original secret never reached, and
    /// each one is a new copy of the credential. The archive is encrypted, so ciphertext would not be
    /// catastrophic — but a restore saying "set DATABASE_PASSWORD" is a small inconvenience, and that
    /// password living in six restored snapshots is not.
    /// </summary>
    [Fact]
    public async Task A_secret_value_never_reaches_the_metadata()
    {
        AddApp(Vol("storefront_data"));
        var app = await _db.Apps.FirstAsync();

        await using var lease = await _stager.StageAsync(app.Id, _snapshot, default);
        lease.Succeeded.Should().BeTrue(lease.Error);

        var raw = await File.ReadAllTextAsync(
            Path.Combine(lease.SourcePath!, ApplicationTargetStager.MetadataFileName));

        raw.Should().NotContain("hunter2-the-real-secret");

        // The NAME is kept, so a restore can say which variables the application needs.
        raw.Should().Contain("DATABASE_PASSWORD");
        raw.Should().Contain("LOG_LEVEL");
        raw.Should().Contain("info", "a non-secret value is useful and safe to carry");
    }

    [Fact]
    public async Task A_secret_is_marked_so_a_restore_knows_to_ask_for_it()
    {
        AddApp(Vol("storefront_data"));
        var app = await _db.Apps.FirstAsync();

        await using var lease = await _stager.StageAsync(app.Id, _snapshot, default);
        var metadata = await MetadataOfAsync(lease.SourcePath!);

        var secret = metadata.GetProperty("environment").EnumerateArray()
            .Single(e => e.GetProperty("Key").GetString() == "DATABASE_PASSWORD");

        secret.GetProperty("IsSecret").GetBoolean().Should().BeTrue();
        secret.GetProperty("Value").ValueKind.Should().Be(JsonValueKind.Null);
        secret.GetProperty("Note").GetString().Should().Contain("deliberately not included");
    }

    // --- what a restore actually needs ---------------------------------------------------------------

    [Fact]
    public async Task Captures_enough_to_rebuild_the_application()
    {
        AddApp(Vol("storefront_data", "/var/lib/data"));
        var app = await _db.Apps.FirstAsync();

        await using var lease = await _stager.StageAsync(app.Id, _snapshot, default);
        var metadata = await MetadataOfAsync(lease.SourcePath!);

        metadata.GetProperty("kind").GetString().Should().Be("harbora-application");

        var application = metadata.GetProperty("application");
        application.GetProperty("Slug").GetString().Should().Be("storefront");
        application.GetProperty("PrebuiltImage").GetString().Should().Be("ghcr.io/example/storefront:2.4.1");

        metadata.GetProperty("deployment").GetProperty("ContainerPort").GetInt32().Should().Be(8080);
        metadata.GetProperty("healthCheck").GetProperty("HealthCheckPath").GetString().Should().Be("/healthz");
        metadata.GetProperty("domains").EnumerateArray().Should().ContainSingle();

        var volume = metadata.GetProperty("volumes").EnumerateArray().Single();
        volume.GetProperty("MountPath").GetString().Should().Be("/var/lib/data");
        // Where the data is inside the snapshot, so a restore does not have to guess.
        volume.GetProperty("dataPath").GetString().Should().Be("volumes/storefront_data");
    }

    [Fact]
    public async Task Stages_every_volume_read_only()
    {
        AddApp(Vol("storefront_data"), Vol("storefront_uploads", "/uploads"));
        var app = await _db.Apps.FirstAsync();

        await using var lease = await _stager.StageAsync(app.Id, _snapshot, default);
        lease.Succeeded.Should().BeTrue(lease.Error);

        _docker.OneOffRequests.Should().HaveCount(2);
        foreach (var request in _docker.OneOffRequests)
        {
            request.Command[0].Should().Be("cp");
            request.Command.Should().NotContain("sh", "no shell on this path either");
            request.Binds.Should().Contain(b => b.Target == "/data" && b.ReadOnly);
        }

        Directory.Exists(Path.Combine(lease.SourcePath!, "volumes", "storefront_data")).Should().BeTrue();
        Directory.Exists(Path.Combine(lease.SourcePath!, "volumes", "storefront_uploads")).Should().BeTrue();
    }

    /// <summary>
    /// All or nothing. An application backup missing one of its volumes restores an application that
    /// has some of its data, which is worse than one that plainly failed.
    /// </summary>
    [Fact]
    public async Task One_unreadable_volume_fails_the_whole_backup()
    {
        _docker.OneOffExitCode = 1;
        AddApp(Vol("storefront_data"), Vol("storefront_uploads"));
        var app = await _db.Apps.FirstAsync();

        var before = Directory.GetDirectories(_options.StagingDirectory).Length;

        await using var lease = await _stager.StageAsync(app.Id, _snapshot, default);

        lease.Succeeded.Should().BeFalse();
        lease.Error.Should().Contain("Nothing was backed up");
        Directory.GetDirectories(_options.StagingDirectory).Should().HaveCount(before);
    }

    [Fact]
    public async Task Removes_the_staged_copy_when_the_lease_is_released()
    {
        AddApp(Vol("storefront_data"));
        var app = await _db.Apps.FirstAsync();

        string staged;
        await using (var lease = await _stager.StageAsync(app.Id, _snapshot, default))
        {
            staged = lease.SourcePath!;
            Directory.Exists(staged).Should().BeTrue();
        }

        Directory.Exists(staged).Should().BeFalse("staged application data must not outlive the backup");
    }

    [Fact]
    public async Task An_application_with_no_volumes_still_captures_its_definition()
    {
        AddApp();
        var app = await _db.Apps.FirstAsync();

        await using var lease = await _stager.StageAsync(app.Id, _snapshot, default);

        lease.Succeeded.Should().BeTrue(lease.Error);
        _docker.OneOffRequests.Should().BeEmpty();
        (await MetadataOfAsync(lease.SourcePath!)).GetProperty("volumes")
            .EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_a_volume_whose_name_docker_would_reject()
    {
        AddApp(Vol("../escape"));
        var app = await _db.Apps.FirstAsync();

        var (ok, error) = await _stager.ValidateAsync(app.Id, default);

        ok.Should().BeFalse();
        error.Should().Contain("../escape");
        _docker.OneOffRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Reports_an_application_that_no_longer_exists()
    {
        var (ok, error) = await _stager.ValidateAsync(Guid.CreateVersion7(), default);

        ok.Should().BeFalse();
        error.Should().Contain("no longer exists");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
    }
}
