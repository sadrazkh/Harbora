using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
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
/// The vertical slice, end to end and for real: a repository, a snapshot of a directory, a restore,
/// and a check that what came back is byte-for-byte what went in.
///
/// <para>
/// No Docker and no Kopia binary involved — a local repository with the built-in engine exercises
/// the archive, the encryption, the storage hand-off and the extraction path, which is where the
/// bugs that lose data would live.
/// </para>
/// </summary>
public sealed class NativeBackupEngineTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _repository;
    private readonly HarboraDbContext _db;
    private readonly PassthroughProtector _protector = new();
    private readonly HarboraNativeBackupEngine _engine;
    private readonly BackupRepository _repositoryRow;
    private readonly IOptions<BackupModuleOptions> _options;

    public NativeBackupEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbora-native-engine", Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "source");
        _repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_repository);

        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("native-engine-" + Guid.NewGuid()).Options);

        _repositoryRow = new BackupRepository
        {
            WorkspaceId = Guid.CreateVersion7(),
            Name = "Local repository",
            Type = BackupRepositoryType.Local,
            Engine = BackupEngineKind.Native,
            BasePath = _repository
        };
        _db.BackupRepositories.Add(_repositoryRow);
        _db.SaveChanges();

        _options = Options.Create(new BackupModuleOptions
        {
            StagingDirectory = Path.Combine(_root, "staging"),
            RestoreRoot = Path.Combine(_root, "restore")
        });

        _engine = EngineWith(_protector);
    }

    // --- helpers ---------------------------------------------------------------------------

    /// <summary>
    /// The platform's own storage adapter, not a stand-in for it.
    ///
    /// <para>
    /// There used to be a double here — <c>KeyedFileStorage</c> — whose comment said it behaved
    /// "exactly as BackupStorage" and which created the directory a key names, something production
    /// did not do. Every test in this file passed while no local repository could store a snapshot
    /// at all. A double of the layer under test is the one place a claim like that cannot be
    /// checked, so the claim is gone and so is the double: a local destination needs no Docker and
    /// no network, which is exactly why there was never a reason to fake it.
    /// </para>
    /// </summary>
    private BackupStorage Storage() => new(
        Options.Create(new BackupOptions { StagingDir = _options.Value.StagingDirectory }),
        Options.Create(new Harbora.Infrastructure.Deployments.HarboraRuntimeOptions()),
        _protector,
        new FakeDockerEngine());

    /// <summary>The same engine, over a protector a test can hold open mid-decrypt.</summary>
    private HarboraNativeBackupEngine EngineWith(ISecretProtector protector) => new(
        _db,
        Storage(),
        new RepositoryCredentialReader(_db, _protector, NullLogger<RepositoryCredentialReader>.Instance),
        new RepositoryDestinationFactory(_protector),
        protector,
        _options,
        NullLogger<HarboraNativeBackupEngine>.Instance);

    private void WriteSource(string relativePath, string content)
    {
        var full = Path.Combine(_source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<BackupSnapshotResult> SnapshotAsync()
    {
        var request = new CreateBackupSnapshotRequest(
            _repositoryRow.Id, Guid.CreateVersion7(), _source, "unused-for-native",
            BackupTargetType.Directory, "source");

        return await _engine.CreateSnapshotAsync(request, default);
    }

    private async Task<RestoreResult> RestoreAsync(
        string engineSnapshotId,
        string destination,
        RestoreConflictStrategy strategy = RestoreConflictStrategy.Fail,
        IReadOnlyList<string>? entries = null)
    {
        return await _engine.RestoreAsync(new RestoreBackupRequest(
            _repositoryRow.Id, engineSnapshotId, "unused-for-native", destination, strategy, entries), default);
    }

    // --- the slice -------------------------------------------------------------------------

    [Fact]
    public async Task Snapshots_and_restores_a_directory_exactly()
    {
        WriteSource("config.yml", "port: 8080");
        WriteSource(Path.Combine("data", "records.csv"), "id,name\n1,harbora");
        WriteSource(Path.Combine("data", "nested", "deep.txt"), "still here");

        var snapshot = await SnapshotAsync();

        snapshot.Succeeded.Should().BeTrue(snapshot.Error);
        snapshot.EngineSnapshotId.Should().NotBeNullOrWhiteSpace();
        snapshot.FilesCount.Should().Be(3);
        snapshot.OriginalSizeBytes.Should().BeGreaterThan(0);

        var destination = Path.Combine(_root, "restored");
        var restore = await RestoreAsync(snapshot.EngineSnapshotId!, destination);

        restore.Succeeded.Should().BeTrue(restore.Error);
        restore.RestoredFilesCount.Should().Be(3);

        File.ReadAllText(Path.Combine(destination, "config.yml")).Should().Be("port: 8080");
        File.ReadAllText(Path.Combine(destination, "data", "records.csv")).Should().Be("id,name\n1,harbora");
        File.ReadAllText(Path.Combine(destination, "data", "nested", "deep.txt")).Should().Be("still here");
    }

    /// <summary>
    /// The artifact on disk must not be readable without the key. A repository sitting on a bucket
    /// someone else can list is the ordinary case, not the exotic one.
    /// </summary>
    [Fact]
    public async Task The_stored_artifact_is_encrypted_at_rest()
    {
        WriteSource("secrets.env", "DATABASE_PASSWORD=hunter2");

        var snapshot = await SnapshotAsync();
        snapshot.Succeeded.Should().BeTrue(snapshot.Error);

        var artifacts = Directory.GetFiles(_repository, "*", SearchOption.AllDirectories);
        artifacts.Should().NotBeEmpty();

        foreach (var artifact in artifacts)
        {
            var bytes = await File.ReadAllBytesAsync(artifact);
            Encoding.UTF8.GetString(bytes).Should().NotContain("hunter2");
            (await ArchiveCipher.IsEncryptedArchiveAsync(artifact, default)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Restores_only_the_entries_that_were_asked_for()
    {
        WriteSource(Path.Combine("wanted", "keep.txt"), "keep me");
        WriteSource(Path.Combine("ignored", "skip.txt"), "not this one");

        var snapshot = await SnapshotAsync();
        var destination = Path.Combine(_root, "partial");

        var restore = await RestoreAsync(snapshot.EngineSnapshotId!, destination, entries: ["wanted"]);

        restore.Succeeded.Should().BeTrue(restore.Error);
        File.Exists(Path.Combine(destination, "wanted", "keep.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(destination, "ignored")).Should().BeFalse();
    }

    /// <summary>
    /// Fail must mean fail. A "safe" default that quietly merges into an occupied directory is how
    /// a restore destroys data nobody asked it to touch.
    /// </summary>
    [Fact]
    public async Task Refuses_to_overwrite_when_the_strategy_is_fail()
    {
        WriteSource("config.yml", "from the backup");
        var snapshot = await SnapshotAsync();

        var destination = Path.Combine(_root, "occupied");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "config.yml"), "LIVE DATA");

        var restore = await RestoreAsync(snapshot.EngineSnapshotId!, destination);

        restore.Succeeded.Should().BeFalse();
        File.ReadAllText(Path.Combine(destination, "config.yml")).Should().Be("LIVE DATA",
            "the live file must be untouched when the restore refuses");
    }

    [Fact]
    public async Task Overwrite_replaces_the_existing_file()
    {
        WriteSource("config.yml", "from the backup");
        var snapshot = await SnapshotAsync();

        var destination = Path.Combine(_root, "overwritten");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "config.yml"), "stale");

        var restore = await RestoreAsync(
            snapshot.EngineSnapshotId!, destination, RestoreConflictStrategy.Overwrite);

        restore.Succeeded.Should().BeTrue(restore.Error);
        File.ReadAllText(Path.Combine(destination, "config.yml")).Should().Be("from the backup");
    }

    [Fact]
    public async Task Skip_leaves_the_existing_file_alone()
    {
        WriteSource("config.yml", "from the backup");
        WriteSource("fresh.txt", "new file");
        var snapshot = await SnapshotAsync();

        var destination = Path.Combine(_root, "skipped");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "config.yml"), "keep mine");

        var restore = await RestoreAsync(
            snapshot.EngineSnapshotId!, destination, RestoreConflictStrategy.Skip);

        restore.Succeeded.Should().BeTrue(restore.Error);
        File.ReadAllText(Path.Combine(destination, "config.yml")).Should().Be("keep mine");
        File.Exists(Path.Combine(destination, "fresh.txt")).Should().BeTrue();
    }

    /// <summary>
    /// The Zip-Slip case, with a real hostile archive rather than a unit test of the guard alone.
    /// Anyone able to write a file into a backed-up volume chooses an entry name in the snapshot.
    /// </summary>
    [Fact]
    public async Task A_snapshot_containing_an_escaping_entry_cannot_write_outside_the_destination()
    {
        var snapshotId = Guid.CreateVersion7();
        await PlantHostileArchiveAsync(snapshotId, "../../escaped.txt", "owned");

        var destination = Path.Combine(_root, "guarded");
        var restore = await RestoreAsync(snapshotId.ToString("N"), destination);

        restore.Succeeded.Should().BeTrue("the safe entries still restore");
        restore.Warnings.Should().NotBeNull();
        restore.Warnings!.Should().Contain(w => w.Contains("Refused", StringComparison.Ordinal));

        File.Exists(Path.Combine(_root, "escaped.txt")).Should().BeFalse();
        File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.txt")).Should().BeFalse();
        Directory.EnumerateFiles(destination, "escaped.txt", SearchOption.AllDirectories)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Browsing_a_snapshot_lists_one_level_with_directories_first()
    {
        WriteSource("readme.md", "top level");
        WriteSource(Path.Combine("data", "one.txt"), "1");
        WriteSource(Path.Combine("data", "two.txt"), "2");

        var snapshot = await SnapshotAsync();

        var entries = await _engine.BrowseSnapshotAsync(
            new BrowseSnapshotRequest(_repositoryRow.Id, snapshot.EngineSnapshotId!, "unused"), default);

        entries.Should().HaveCount(2);
        entries[0].IsDirectory.Should().BeTrue();
        entries[0].Name.Should().Be("data");
        entries[1].Name.Should().Be("readme.md");

        var inside = await _engine.BrowseSnapshotAsync(
            new BrowseSnapshotRequest(_repositoryRow.Id, snapshot.EngineSnapshotId!, "unused", "data"), default);

        inside.Select(e => e.Name).Should().BeEquivalentTo(["one.txt", "two.txt"]);
    }

    [Fact]
    public async Task Reports_a_missing_source_rather_than_writing_an_empty_snapshot()
    {
        var request = new CreateBackupSnapshotRequest(
            _repositoryRow.Id, Guid.CreateVersion7(), Path.Combine(_root, "does-not-exist"),
            "unused", BackupTargetType.Directory, "missing");

        var result = await _engine.CreateSnapshotAsync(request, default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("nothing to back up");
    }

    [Fact]
    public async Task Health_check_reports_a_missing_local_repository()
    {
        Directory.Delete(_repository, recursive: true);

        var health = await _engine.CheckHealthAsync(_repositoryRow.Id, default);

        health.Reachable.Should().BeFalse();
        health.Error.Should().Contain("not there");
    }

    [Fact]
    public async Task Lists_completed_snapshots_newest_first()
    {
        WriteSource("a.txt", "a");
        var first = await SnapshotAsync();
        var second = await SnapshotAsync();

        // The engine lists from Harbora's own table, which is the index for this format.
        foreach (var (result, order) in new[] { (first, -2), (second, -1) })
        {
            _db.BackupSnapshots.Add(new BackupSnapshot
            {
                WorkspaceId = _repositoryRow.WorkspaceId,
                RepositoryId = _repositoryRow.Id,
                EngineSnapshotId = result.EngineSnapshotId,
                TargetRef = "source",
                TargetType = BackupTargetType.Directory,
                Status = BackupSnapshotStatus.Completed,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(order)
            });
        }
        await _db.SaveChangesAsync();

        var listed = await _engine.ListSnapshotsAsync(
            new ListSnapshotsRequest(_repositoryRow.Id, "unused"), default);

        listed.Should().HaveCount(2);
        listed[0].EngineSnapshotId.Should().Be(second.EngineSnapshotId);
    }

    /// <summary>
    /// Two reads of one snapshot must not name the same file.
    ///
    /// <para>
    /// The decrypted copy used to be derived from the archive's own name, so a browse and a restore
    /// of the same snapshot produced the same path — and both delete it in a <c>finally</c>. One
    /// truncates or removes what the other is reading. Automatic verification made that pairing
    /// reachable without anyone browsing: the check is queued the moment a backup finishes, on the
    /// same page the restore is launched from.
    /// </para>
    /// <para>
    /// Deterministic rather than timed. The gate holds the first read <i>inside</i> its decrypt, with
    /// its output file open, and lets the second run to completion beside it — which is the exact
    /// interleaving the race needs, and one that never happens by luck.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_reads_of_one_snapshot_do_not_share_a_decrypted_copy()
    {
        WriteSource("config.yml", "port: 8080");
        var snapshot = await SnapshotAsync();
        snapshot.Succeeded.Should().BeTrue(snapshot.Error);

        var gate = new GatingProtector(_protector);
        var engine = EngineWith(gate);

        // Started on the pool: the engine runs synchronously as far as the gate, and blocking the
        // test thread there would deadlock before the task was ever handed back.
        var browse = Task.Run(() => engine.BrowseSnapshotAsync(
            new BrowseSnapshotRequest(_repositoryRow.Id, snapshot.EngineSnapshotId!, "unused"), default));

        await gate.FirstIsInside;

        var destination = Path.Combine(_root, "beside-a-browse");
        var restore = await engine.RestoreAsync(new RestoreBackupRequest(
            _repositoryRow.Id, snapshot.EngineSnapshotId!, "unused", destination,
            RestoreConflictStrategy.Overwrite, null), default);

        restore.Succeeded.Should().BeTrue(
            restore.Error ?? "a restore must not be blocked or corrupted by a check running beside it");
        (await File.ReadAllTextAsync(Path.Combine(destination, "config.yml"))).Should().Be("port: 8080");

        gate.ReleaseFirst();

        var entries = await browse;
        entries.Should().Contain(e => e.Name == "config.yml",
            "and the check must still be able to read the copy it decrypted");
    }

    /// <summary>
    /// And for a LOCAL repository the decrypted copy used to land in the repository itself, because
    /// the fetch of a local artifact returns the artifact's own path and the target was derived from
    /// it. Asserted <i>during</i> the read: the file is removed in a <c>finally</c>, so afterwards
    /// the repository looks innocent either way.
    /// </summary>
    [Fact]
    public async Task A_read_never_leaves_plaintext_beside_the_artifact()
    {
        WriteSource("secrets.env", "DATABASE_PASSWORD=hunter2");
        var snapshot = await SnapshotAsync();
        snapshot.Succeeded.Should().BeTrue(snapshot.Error);

        var gate = new GatingProtector(_protector);
        var engine = EngineWith(gate);

        var browse = Task.Run(() => engine.BrowseSnapshotAsync(
            new BrowseSnapshotRequest(_repositoryRow.Id, snapshot.EngineSnapshotId!, "unused"), default));

        await gate.FirstIsInside;

        // The decrypt is holding its output file open at this instant.
        Directory.GetFiles(_repository, "*", SearchOption.AllDirectories)
            .Should().OnlyContain(a => a.EndsWith(ArchiveCipher.Extension, StringComparison.Ordinal),
                "a repository holds encrypted artifacts and nothing else, mid-read included");

        gate.ReleaseFirst();
        await browse;
    }

    /// <summary>
    /// Writes an archive the engine will accept but whose contents are hostile, straight into the
    /// repository — the shape of a snapshot taken from a volume an attacker could write into.
    /// </summary>
    private async Task PlantHostileArchiveAsync(Guid snapshotId, string entryName, string content)
    {
        var plain = Path.Combine(_root, $"{snapshotId:N}.plain.tar.gz");

        await using (var file = File.Create(plain))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var hostile = new PaxTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(bytes)
            };
            await tar.WriteEntryAsync(hostile);

            var safe = new PaxTarEntry(TarEntryType.RegularFile, "harmless.txt")
            {
                DataStream = new MemoryStream("fine"u8.ToArray())
            };
            await tar.WriteEntryAsync(safe);
        }

        var target = Path.Combine(_repository, $"{_repositoryRow.Id:N}",
            $"{snapshotId:N}.tar.gz{ArchiveCipher.Extension}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        await using (var source = File.OpenRead(plain))
        await using (var cipher = File.Create(target))
            await ArchiveCipher.EncryptAsync(source, cipher, _protector.DeriveKey("backup-archive"), default);

        File.Delete(plain);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
    }
}

/// <summary>
/// Holds the FIRST caller inside <see cref="DeriveKey"/> until it is let go, and waves every later
/// one straight through.
///
/// <para>
/// <c>DeriveKey</c> is evaluated after the decrypt has opened its output file and before a byte is
/// written to it, which makes it the one seam where a test can pin two reads of the same snapshot
/// on top of each other on purpose. Keys are delegated, so both callers still read the same archive.
/// </para>
/// </summary>
internal sealed class GatingProtector(ISecretProtector inner) : ISecretProtector
{
    private readonly TaskCompletionSource _inside =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ManualResetEventSlim _released = new(false);
    private int _callers;

    /// <summary>Completes once the first caller is holding its output file open.</summary>
    public Task FirstIsInside => _inside.Task;

    public void ReleaseFirst() => _released.Set();

    public byte[] DeriveKey(string purpose)
    {
        if (Interlocked.Increment(ref _callers) == 1)
        {
            _inside.TrySetResult();
            _released.Wait(TimeSpan.FromSeconds(30));
        }

        return inner.DeriveKey(purpose);
    }

    public string Protect(string plaintext) => inner.Protect(plaintext);
    public string Unprotect(string ciphertext) => inner.Unprotect(ciphertext);
}

