namespace Harbora.Infrastructure.Backups;

/// <summary>
/// The shell run inside the helper container to replace a volume's contents from an archive.
///
/// The original was one line: <c>rm -rf /data/* &amp;&amp; tar xzf …</c>. The wipe ran
/// <b>unconditionally and first</b>, so anything that went wrong afterwards — a truncated archive, a
/// full disk, the wrong file — left the volume empty with nothing to put back. The gates in front of
/// it (checksum, archive probe) make reaching that state unlikely, but "unlikely" is the wrong
/// safety property for the single most destructive operation in the product.
///
/// This extracts first and only swaps once the new tree exists:
/// <list type="number">
/// <item>Extract into a staging directory <i>inside the same volume</i> — a failure here cannot
/// touch the live data, and staying on one filesystem keeps the later moves cheap renames rather
/// than copies.</item>
/// <item>Move the current contents aside (not delete).</item>
/// <item>Move the extracted tree into place.</item>
/// <item>Only now discard the set-aside copy.</item>
/// </list>
/// If a move fails mid-swap, the script puts the original contents back and exits non-zero.
///
/// Cost: peak disk usage is roughly twice the volume during a restore. That is the price of not
/// being able to lose the data, and it is paid only while a restore runs.
/// </summary>
public static class RestoreScript
{
    /// <summary>Staging directory for the freshly extracted tree.</summary>
    public const string StageDir = "/data/.harbora-restore";

    /// <summary>Where the current contents are held until the swap has succeeded.</summary>
    public const string PreviousDir = "/data/.harbora-previous";

    /// <summary>Exit code used when the swap failed and the original contents were put back.</summary>
    public const int RolledBackExitCode = 90;

    /// <summary>
    /// Builds the script. <paramref name="archiveFileName"/> is a Harbora-generated name (type,
    /// slug and a timestamp), never user input, and is single-quoted here regardless.
    /// </summary>
    public static string Build(string archiveFileName)
    {
        var archive = "'/backup/" + archiveFileName.Replace("'", @"'\''") + "'";

        // POSIX sh (busybox ash in the alpine helper): no arrays, no `mv -t`, no bashisms.
        // $$ so a single { } stays literal — `find -exec … {} \;` needs real braces.
        return $$"""
            set -e
            STAGE={{StageDir}}
            PREV={{PreviousDir}}

            # Clear any residue from an interrupted earlier attempt.
            rm -rf "$STAGE" "$PREV"
            mkdir -p "$STAGE"

            # 1. EXTRACT FIRST. If the archive is bad, we fail here and /data is untouched.
            tar xzf {{archive}} -C "$STAGE"

            # From here on failures are handled by hand so the original data can be put back.
            set +e

            # 2. Move the live contents aside — a rename within the volume, not a delete.
            mkdir -p "$PREV"
            find /data -mindepth 1 -maxdepth 1 \
                 ! -name .harbora-restore ! -name .harbora-previous \
                 -exec mv {} "$PREV"/ \;
            moved=$?

            # 3. Put the extracted tree in place.
            find "$STAGE" -mindepth 1 -maxdepth 1 -exec mv {} /data/ \;
            placed=$?

            if [ $moved -ne 0 ] || [ $placed -ne 0 ]; then
                # Swap failed halfway: discard whatever landed and restore the original contents.
                find /data -mindepth 1 -maxdepth 1 \
                     ! -name .harbora-restore ! -name .harbora-previous \
                     -exec rm -rf {} \;
                find "$PREV" -mindepth 1 -maxdepth 1 -exec mv {} /data/ \;
                rm -rf "$STAGE" "$PREV"
                echo "restore: swap failed; original contents were put back" >&2
                exit {{RolledBackExitCode}}
            fi

            # 4. The new tree is in place — only now is the old copy expendable.
            rm -rf "$PREV" "$STAGE"
            """;
    }
}
