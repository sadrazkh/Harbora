using FluentAssertions;
using Harbora.Infrastructure.Backups;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The restore shell is the most destructive thing Harbora runs. It used to be
/// <c>rm -rf /data/* &amp;&amp; tar xzf …</c> — the wipe first, unconditionally — so any later failure
/// left an empty volume and no way back. These tests pin the property that replaced it: nothing
/// destructive may happen before the archive has been successfully extracted.
/// </summary>
public class RestoreScriptTests
{
    private static string Script() => RestoreScript.Build("database-blog-20260729-184909.tgz.enc");

    [Fact]
    public void Extraction_happens_before_anything_is_moved_or_deleted()
    {
        var s = Script();

        var extract = s.IndexOf("tar xzf", StringComparison.Ordinal);
        var moveAside = s.IndexOf("mv {} \"$PREV\"/", StringComparison.Ordinal);

        extract.Should().BeGreaterThan(-1);
        moveAside.Should().BeGreaterThan(extract,
            "a failed extraction must leave the live data exactly where it was");
    }

    [Fact]
    public void The_live_data_is_moved_aside_not_deleted()
    {
        var s = Script();

        // The only rm of live contents is inside the rollback branch; the swap path renames.
        var swapPath = s[..s.IndexOf("if [ $moved", StringComparison.Ordinal)];
        swapPath.Should().NotContain("rm -rf /data/*");
        swapPath.Should().Contain("mv {} \"$PREV\"/");
    }

    [Fact]
    public void The_previous_copy_is_discarded_only_after_the_swap_succeeds()
    {
        var s = Script();

        var placed = s.IndexOf("placed=$?", StringComparison.Ordinal);
        var discard = s.LastIndexOf("rm -rf \"$PREV\" \"$STAGE\"", StringComparison.Ordinal);

        discard.Should().BeGreaterThan(placed,
            "deleting the fallback before the new tree is in place would recreate the original bug");
    }

    [Fact]
    public void A_failed_swap_puts_the_original_contents_back()
    {
        var s = Script();

        s.Should().Contain("if [ $moved -ne 0 ] || [ $placed -ne 0 ]");
        s.Should().Contain($"exit {RestoreScript.RolledBackExitCode}");
        // The rollback moves $PREV back into /data.
        s.Should().Contain("find \"$PREV\" -mindepth 1 -maxdepth 1 -exec mv {} /data/");
    }

    [Fact]
    public void Staging_lives_inside_the_volume()
    {
        // Same filesystem keeps the swap a rename rather than a copy, and keeps the doubled disk
        // usage inside the volume's own quota instead of the container's writable layer.
        RestoreScript.StageDir.Should().StartWith("/data/");
        RestoreScript.PreviousDir.Should().StartWith("/data/");
    }

    [Fact]
    public void Its_own_working_directories_are_never_swept_up()
    {
        var s = Script();

        // Without these exclusions the script would move its own staging dir aside mid-restore.
        s.Should().Contain("! -name .harbora-restore ! -name .harbora-previous");
    }

    [Fact]
    public void Residue_from_an_interrupted_attempt_is_cleared_first()
    {
        Script().Should().Contain("rm -rf \"$STAGE\" \"$PREV\"");
    }

    [Fact]
    public void The_archive_name_is_quoted()
    {
        RestoreScript.Build("a b.tgz").Should().Contain("'/backup/a b.tgz'");
    }

    [Fact]
    public void A_quote_in_the_archive_name_cannot_break_out()
    {
        // Names are Harbora-generated, but a shell string assembled from a variable gets the
        // defensive treatment regardless. The payload stays inside one quoted word: each ' becomes
        // '\'' (close quote, escaped quote, reopen), so the shell sees a single argument and the
        // injected `; rm -rf /data` is data, not a command.
        var script = RestoreScript.Build("evil'; rm -rf /data; echo '.tgz");

        script.Should().Contain(@"tar xzf '/backup/evil'\''; rm -rf /data; echo '\''.tgz' -C");
    }

    [Fact]
    public async Task A_restore_that_rolled_back_is_reported_as_lost_nothing()
    {
        using var h = new BackupHarness();
        var backup = await h.SeedVolumeBackupAsync();
        h.Docker.OneOffExitCode = RestoreScript.RolledBackExitCode;
        h.Db.ManagedServices.Add(new Harbora.Domain.Services.ManagedService
        { Id = Guid.NewGuid(), WorkspaceId = h.WorkspaceId, Name = "svc", VolumeName = "blog-data" });
        await h.Db.SaveChangesAsync();

        var restore = async () => await h.Engine().RestoreAsync(backup.Id, default);

        (await restore.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Nothing was lost*",
                "the operator's first question after a failed restore is whether their data survived");
    }
}
