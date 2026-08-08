using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Backups;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Harbora.Modules.Backup.Infrastructure;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The real storage adapter against a local destination, exercised with the keys its callers
/// actually pass it.
///
/// <para>
/// Every other test of the backup module reaches storage through a double, and the doubles were
/// kinder than the thing they stood for: they created the directory a key names and they resolved a
/// key back to a path. Production did neither, so a repository whose key carried a folder — which is
/// every repository the backup module writes to — could not store a snapshot, could not read one
/// back, and reported a successful delete of a file it never touched. These tests use
/// <see cref="BackupStorage"/> itself, because a double cannot be wrong about its own behaviour.
/// </para>
/// <para>
/// No Docker and no S3: a local destination is the branch that runs entirely on this process's own
/// filesystem, and it is the branch the defect lived in.
/// </para>
/// </summary>
public sealed class BackupStorageTests : IDisposable
{
    private readonly string _root;
    private readonly string _staging;
    private readonly string _destinationDir;

    public BackupStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbora-backup-storage", Guid.NewGuid().ToString("N"));
        _staging = Path.Combine(_root, "staging");
        _destinationDir = Path.Combine(_root, "destination");
        Directory.CreateDirectory(_staging);
        Directory.CreateDirectory(_destinationDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir — best effort */ }
    }

    // --- helpers ---------------------------------------------------------------------------

    private BackupStorage Storage() => new(
        Options.Create(new BackupOptions { StagingDir = _staging }),
        Options.Create(new Harbora.Infrastructure.Deployments.HarboraRuntimeOptions()),
        new PassthroughProtector(),
        new FakeDockerEngine());

    private BackupDestination LocalDestination() => new()
    {
        Name = "Local disk",
        Type = BackupDestinationType.Local,
        LocalPath = _destinationDir
    };

    private string Staged(string name, string content)
    {
        var path = Path.Combine(_staging, name);
        File.WriteAllText(path, content);
        return path;
    }

    // --- the local branch, key by key -------------------------------------------------------

    /// <summary>
    /// The key the backup module builds — repository folder, then artifact — must produce the file
    /// it names. The destination root is created; the folder inside it was not, and
    /// <c>File.Copy</c> into a directory that is not there throws, which the engine's catch turned
    /// into a failed snapshot.
    /// </summary>
    [Fact]
    public async Task A_key_with_a_folder_in_it_creates_that_folder()
    {
        var repository = Guid.CreateVersion7();
        var key = $"{repository:N}/{Guid.CreateVersion7():N}.tar.gz.enc";

        var (artifactRef, size) = await Storage().PutFileAsync(
            LocalDestination(), key, Staged("archive.tar.gz.enc", "ciphertext"), default);

        var expected = Path.Combine(_destinationDir, repository.ToString("N"), Path.GetFileName(key));
        File.Exists(expected).Should().BeTrue("the artifact belongs in the folder its key names");
        // Compared resolved: the reference is the destination root joined to the key as given, and a
        // key that carries '/' on Windows produces a path that mixes separators and opens perfectly.
        Path.GetFullPath(artifactRef).Should().Be(expected);
        size.Should().Be("ciphertext".Length);
    }

    /// <summary>
    /// Reading takes the same key writing took. A local destination has nothing to download, so the
    /// answer is a path — but it has to be the path the artifact is at, not the key repeated back,
    /// which resolves against whatever directory the panel process happens to have been started in.
    /// </summary>
    [Fact]
    public async Task An_artifact_is_read_back_by_the_key_it_was_written_under()
    {
        var storage = Storage();
        var key = $"{Guid.CreateVersion7():N}/{Guid.CreateVersion7():N}.tar.gz.enc";
        await storage.PutFileAsync(LocalDestination(), key, Staged("archive.tar.gz.enc", "ciphertext"), default);

        var fetched = await storage.GetToLocalAsync(LocalDestination(), key, default);

        File.Exists(fetched).Should().BeTrue("a restore reads the artifact from the path this returns");
        (await File.ReadAllTextAsync(fetched)).Should().Be("ciphertext");
    }

    /// <summary>
    /// And so does deleting. A retention pass that cannot find the artifact removes the row and
    /// leaves the file — the repository grows for ever and every screen says it was pruned.
    /// </summary>
    [Fact]
    public async Task An_artifact_is_deleted_by_the_key_it_was_written_under()
    {
        var storage = Storage();
        var key = $"{Guid.CreateVersion7():N}/{Guid.CreateVersion7():N}.tar.gz.enc";
        var (artifactRef, _) = await storage.PutFileAsync(
            LocalDestination(), key, Staged("archive.tar.gz.enc", "ciphertext"), default);

        await storage.DeleteAsync(LocalDestination(), key, default);

        File.Exists(artifactRef).Should().BeFalse("retention asked for this artifact by its key");
    }

    /// <summary>
    /// The platform's own engine records the absolute path <c>PutFileAsync</c> returned and hands
    /// that back on every later read. Resolving a key under the destination must not disturb it —
    /// including when the destination has since been pointed somewhere else, which is the ordinary
    /// way an old backup's path ends up outside the current root.
    /// </summary>
    [Fact]
    public async Task An_absolute_reference_is_read_and_deleted_where_it_stands()
    {
        var storage = Storage();
        var elsewhere = Path.Combine(_root, "an-older-destination");
        Directory.CreateDirectory(elsewhere);
        var artifact = Path.Combine(elsewhere, "volume-web-20260101-000000.tgz.enc");
        await File.WriteAllTextAsync(artifact, "ciphertext");

        (await storage.GetToLocalAsync(LocalDestination(), artifact, default)).Should().Be(artifact);

        await storage.DeleteAsync(LocalDestination(), artifact, default);
        File.Exists(artifact).Should().BeFalse();
    }

    /// <summary>
    /// A key is a name inside the destination, and the storage layer is what makes that true. Its
    /// callers build keys from Guids today, which is why nothing has escaped yet — but "the caller
    /// is careful" is not a containment property, and this is the layer that resolves the path.
    /// </summary>
    [Fact]
    public async Task A_key_that_climbs_out_of_the_destination_is_refused()
    {
        var act = async () => await Storage().PutFileAsync(
            LocalDestination(), "../escaped.tar.gz.enc", Staged("archive.tar.gz.enc", "ciphertext"), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(Path.Combine(_root, "escaped.tar.gz.enc")).Should().BeFalse("nothing may be written outside the destination");
    }

    // --- the same question, asked of an S3 destination ---------------------------------------

    /// <summary>
    /// A reference the platform recorded carries its own bucket, and keeps it. An artifact stays
    /// readable after the destination has been pointed at a different bucket, which is the whole
    /// reason the bucket was written into the reference in the first place.
    /// </summary>
    [Fact]
    public void A_recorded_reference_names_the_bucket_it_was_stored_in()
    {
        var dest = new BackupDestination { Type = BackupDestinationType.S3, Bucket = "current-bucket" };

        BackupStorage.S3Location(dest, "s3://where-it-went/019abc/019def.tar.gz.enc")
            .Should().Be(("where-it-went", "019abc/019def.tar.gz.enc"));
    }

    /// <summary>
    /// And a bare key is a key: the object it names lives in the bucket the destination names.
    ///
    /// <para>
    /// The backup module keeps no reference — it rebuilds the key from the repository and snapshot
    /// ids on every read — so this is the only form its restores, browses, deletes and repository
    /// probes ever pass. Split on the first slash without asking whether there was a scheme, that
    /// key read as <c>bucket/object</c>: every one of those operations went looking in a bucket
    /// named after the repository's own Guid, which exists nowhere.
    /// </para>
    /// </summary>
    [Fact]
    public void A_bare_key_names_an_object_in_the_destinations_own_bucket()
    {
        var dest = new BackupDestination { Type = BackupDestinationType.S3, Bucket = "harbora-backups" };

        BackupStorage.S3Location(dest, "019abc/019def.tar.gz.enc")
            .Should().Be(("harbora-backups", "019abc/019def.tar.gz.enc"));
    }

    // --- and the same thing, through the engine that reported the defect ---------------------

    /// <summary>
    /// The whole reason this matters: the built-in engine over a <b>local</b> repository, with the
    /// real storage adapter underneath it. Snapshot, restore, delete — the three things an operator
    /// does — with nothing standing in for the filesystem.
    /// </summary>
    [Fact]
    public async Task A_local_repository_snapshots_restores_and_deletes_through_the_real_storage()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "config.yml"), "port: 8080");

        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("backup-storage-" + Guid.NewGuid()).Options);

        var repository = new BackupRepository
        {
            WorkspaceId = Guid.CreateVersion7(),
            Name = "Local repository",
            Type = BackupRepositoryType.Local,
            Engine = BackupEngineKind.Native,
            BasePath = _destinationDir
        };
        db.BackupRepositories.Add(repository);
        await db.SaveChangesAsync();

        var protector = new PassthroughProtector();
        var engine = new HarboraNativeBackupEngine(
            db,
            Storage(),
            new RepositoryCredentialReader(db, protector, NullLogger<RepositoryCredentialReader>.Instance),
            new RepositoryDestinationFactory(protector),
            protector,
            Options.Create(new BackupModuleOptions
            {
                StagingDirectory = _staging,
                RestoreRoot = Path.Combine(_root, "restore")
            }),
            NullLogger<HarboraNativeBackupEngine>.Instance);

        var snapshotId = Guid.CreateVersion7();
        var snapshot = await engine.CreateSnapshotAsync(new CreateBackupSnapshotRequest(
            repository.Id, snapshotId, source, "unused-for-native",
            BackupTargetType.Directory, "source"), default);

        snapshot.Error.Should().BeNull();
        snapshot.Succeeded.Should().BeTrue("a local repository is the default one an operator gets");

        var destination = Path.Combine(_root, "restored");
        var restore = await engine.RestoreAsync(new RestoreBackupRequest(
            repository.Id, snapshot.EngineSnapshotId!, "unused-for-native",
            destination, RestoreConflictStrategy.Fail, null), default);

        restore.Error.Should().BeNull();
        restore.Succeeded.Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(destination, "config.yml"))).Should().Be("port: 8080");

        var artifact = Path.Combine(
            _destinationDir, repository.Id.ToString("N"), $"{snapshotId:N}.tar.gz.enc");
        File.Exists(artifact).Should().BeTrue();

        var deleted = await engine.DeleteSnapshotAsync(
            new DeleteSnapshotRequest(repository.Id, snapshot.EngineSnapshotId!, "unused-for-native"), default);

        deleted.Succeeded.Should().BeTrue();
        File.Exists(artifact).Should().BeFalse("retention reported this artifact removed");

        db.Dispose();
    }
}
