using System.Text;
using FluentAssertions;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project 10's self-serve import calls the existing <c>BackupEngine.ImportAsync</c> — already
/// wired to <c>BackupManagementActions.Upload</c>, just never previously exercised by a test — and
/// then <c>RestoreAsync</c> immediately reads the artifact it just wrote back. That round trip found a
/// real bug: a Local destination with no <c>LocalPath</c> of its own (the workspace default every
/// <c>BackupsController.EnsureDefaultDestinationAsync</c> creates) publishes an artifact IN PLACE —
/// <c>BackupStorage.PutFileAsync</c> returns the same path it was given, not a copy — and
/// <c>ImportAsync</c>'s own cleanup unconditionally deleted "the staging copy" afterwards, deleting the
/// only file the new backup row named. A restore of that backup then failed with "the backup artifact
/// is missing from its destination" every single time. These tests pin the fix down.
/// </summary>
public sealed class DatabaseImportArtifactTests
{
    [Fact]
    public async Task An_imported_artifact_survives_when_the_destination_publishes_in_place()
    {
        using var h = new BackupHarness();
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("an uploaded dump"));

        var id = await h.Engine().ImportAsync(
            h.WorkspaceId, BackupType.Database, Guid.NewGuid().ToString(), h.Destination.Id,
            "dump.sql.gz", content, default);

        var backup = await h.Db.Backups.SingleAsync(b => b.Id == id);
        File.Exists(backup.ArtifactPath!).Should().BeTrue(
            "a Local destination with no LocalPath of its own publishes in place — deleting 'the " +
            "staging copy' here would delete the only file this backup names");
        File.ReadAllText(backup.ArtifactPath!).Should().Be("an uploaded dump");
    }

    [Fact]
    public async Task A_restore_of_a_freshly_imported_artifact_can_actually_find_it()
    {
        // The consequence, proved end to end: without the fix, RestoreAsync's own integrity gate
        // refused every import with "artifact is missing", which is a worse failure than the one
        // sub-project 10 exists to close — an import that can never be undone because it can never
        // even be REPEATED, let alone restored from.
        using var h = new BackupHarness();
        var svc = await h.SeedDatabaseAsync(Guid.NewGuid());
        using var content = new MemoryStream(GzipOf("-- a dump\n"));

        var id = await h.Engine().ImportAsync(
            h.WorkspaceId, BackupType.Database, svc.Id.ToString(), h.Destination.Id,
            "dump.sql.gz", content, default);

        h.Docker.OneOffExitCode = 0;
        var restore = async () => await h.Engine().RestoreAsync(id, default);

        await restore.Should().NotThrowAsync("the artifact ImportAsync just wrote must still be there");
    }

    // A destination that copies the artifact somewhere else entirely (S3, SFTP, or a Local
    // destination with its own LocalPath) is unaffected by this fix: the guard added to ImportAsync's
    // cleanup only SKIPS the delete when the published reference and the staging path are the same
    // file, so a destination that genuinely differs still has its staging copy removed exactly as
    // before. LocalOnlyStorage — the fake this test file's harness uses — always publishes in place
    // (it returns the path it was given, regardless of destination), so that branch is proved by
    // inspection of BackupEngine.ImportAsync's finally block rather than by a fake that cannot
    // distinguish "published elsewhere" from "published in place" in the first place.

    private static byte[] GzipOf(string text)
    {
        using var buffer = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(
                   buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(text));
        return buffer.ToArray();
    }
}
