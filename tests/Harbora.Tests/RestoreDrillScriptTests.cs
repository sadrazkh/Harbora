using System.Diagnostics;
using System.IO.Compression;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Runs the real, unmodified <c>deploy/restore-drill.sh</c> against a fake <c>docker</c> and a fake
/// <c>harbora</c> on <c>PATH</c> (both under <c>tests/Harbora.Tests/Fixtures/restore-drill-fakebin</c>),
/// so its failure-detection logic is proven without a Docker daemon or a live PostgreSQL anywhere
/// near this machine — see <c>no-docker-on-dev-machine</c> in the project's memory. Only the real
/// drill, run on the server, proves a real restore; that lane is deliberately not duplicated here.
///
/// <para>
/// Each fact below is the same fact <c>deploy/restore-drill.sh</c>'s own header comment states as
/// the point of the whole sub-project: a drill that reports success for work it never did is the
/// defect class this codebase has spent weeks removing. So every test here is really asking one
/// question — "does the script FAIL LOUDLY, with the right stated reason, when it should?" — for
/// each of the four ways the plan names: no backup, a truncated dump, a failed restore, and a
/// sanity query that could not be answered. A fifth proves the opposite: that a clean run actually
/// passes, so "fails loudly" is not proven by a script that simply always fails.
/// </para>
/// </summary>
public class RestoreDrillScriptTests
{
    private static string ScriptPath => Path.Combine(TestPaths.RepoRoot, "deploy", "restore-drill.sh");
    private static string FakeBinDir => Path.Combine(
        TestPaths.RepoRoot, "tests", "Harbora.Tests", "Fixtures", "restore-drill-fakebin");

    private sealed record DrillRun(
        int ExitCode, string StdOut, string StdErr,
        IReadOnlyList<string> HarboraCalls, IReadOnlyList<string> DockerCalls);

    /// <summary>
    /// Writes a well-formed <c>.sql.gz</c> fixture — real gzip bytes, produced the same way .NET
    /// produces them anywhere else, so the "is this actually gzip" check the script performs
    /// (<c>gzip -t</c>) has something genuine to say yes to.
    /// </summary>
    private static void WriteValidBackup(string backupDir, string fileName = "manual-20260101-000000.sql.gz")
    {
        Directory.CreateDirectory(backupDir);
        using var file = File.Create(Path.Combine(backupDir, fileName));
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new StreamWriter(gzip);
        writer.Write("-- a fixture dump, not a real one\nSELECT 1;\n");
    }

    /// <summary>
    /// Runs <c>deploy/restore-drill.sh</c> for real, through the resolved <c>bash</c> interpreter,
    /// with the fake <c>docker</c>/<c>harbora</c> ahead of everything else on <c>PATH</c> and
    /// <c>mode</c> steering which branch of the fake <c>docker</c> responds how — see
    /// <c>Fixtures/restore-drill-fakebin/docker</c>'s own header for what each mode simulates.
    /// </summary>
    private static async Task<DrillRun> RunAsync(string backupDir, string? mode = null, TimeSpan? timeout = null)
    {
        var harboraLog = Path.Combine(Path.GetTempPath(), "harbora-drill-log-" + Guid.NewGuid().ToString("N") + ".txt");
        var dockerLog = Path.Combine(Path.GetTempPath(), "harbora-drill-docker-log-" + Guid.NewGuid().ToString("N") + ".txt");

        var psi = new ProcessStartInfo
        {
            FileName = ResolveBash(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ScriptPath);

        // Windows-native PATH joining (';' via Path.PathSeparator) with ordinary Windows-style
        // paths, left untranslated: this is exactly the PATH format an ordinary Windows process
        // hands a freshly spawned Git-Bash bash.exe, which is what turns it into a working POSIX
        // $PATH at the interpreter's own startup. Hand-joining a drive-letter path into an
        // already-POSIX colon-separated string instead corrupts it — the drive letter's own colon
        // splits into a second, bogus PATH entry.
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        psi.Environment["PATH"] = FakeBinDir + Path.PathSeparator + existingPath;

        psi.Environment["HARBORA_BACKUP_DIR"] = backupDir;
        psi.Environment["HARBORA_DRILL_CONTAINER"] = "harbora-restore-drill-test";
        // 2 rather than the script's real 30-second default: a fixture proving the "never becomes
        // ready" path only needs to know the script gives up and fails, not to actually wait out
        // the production timeout to prove it.
        psi.Environment["HARBORA_DRILL_READY_RETRIES"] = "2";
        psi.Environment["DRILL_TEST_HARBORA_LOG"] = harboraLog;
        psi.Environment["DRILL_TEST_DOCKER_LOG"] = dockerLog;
        if (mode is not null)
            psi.Environment["DRILL_TEST_MODE"] = mode;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        var completed = process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds);
        if (!completed)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"deploy/restore-drill.sh did not exit within {timeout ?? TimeSpan.FromSeconds(30)} " +
                "(mode=" + (mode ?? "(default)") + "). A fixture test must never touch a real Docker " +
                "daemon or wait on a real container, so a hang here means the fake docker did not " +
                "answer a call the script made.");
        }

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        var harboraCalls = File.Exists(harboraLog) ? await File.ReadAllLinesAsync(harboraLog) : [];
        var dockerCalls = File.Exists(dockerLog) ? await File.ReadAllLinesAsync(dockerLog) : [];
        if (File.Exists(harboraLog)) File.Delete(harboraLog);
        if (File.Exists(dockerLog)) File.Delete(dockerLog);

        return new DrillRun(process.ExitCode, stdOut, stdErr, harboraCalls, dockerCalls);
    }

    /// <summary>
    /// Windows ships no <c>bash</c> the .NET host resolves through <c>PATH</c> lookup by itself in
    /// every environment, so this tries the ordinary lookup first and falls back to where Git for
    /// Windows puts it — the same interpreter <c>tests/Harbora.Tests/*.sh</c>-adjacent tooling and
    /// this developer's own shell already use. Linux CI needs none of this; plain "bash" resolves.
    /// </summary>
    private static string ResolveBash()
    {
        if (!OperatingSystem.IsWindows()) return "bash";

        string[] candidates =
        [
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
        ];
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        return "bash"; // let PATH resolution try, so a differently-installed Git still has a chance
    }

    // ---- the four required failure modes ----

    [Fact]
    public async Task No_backup_directory_at_all_fails_loudly_and_records_it()
    {
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-missing-" + Guid.NewGuid().ToString("N"));
        // Deliberately never created.

        var run = await RunAsync(backupDir);

        run.ExitCode.Should().NotBe(0, "there is nothing to restore, and that must not read as success");
        run.StdOut.Should().Contain("FAIL");
        run.StdOut.Should().Contain("no backup found",
            "an operator reading this later must be told WHY, not just that something failed");
        run.HarboraCalls.Should().ContainSingle(c => c.Contains("--verdict fail") && c.Contains("no backup found"),
            "the admin settings page's \"last drill\" surface is fed by exactly this call");
    }

    [Fact]
    public async Task An_empty_backup_directory_fails_loudly_with_no_backup_found()
    {
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDir); // exists, but nothing in it

        var run = await RunAsync(backupDir);

        run.ExitCode.Should().NotBe(0);
        run.StdOut.Should().Contain("no backup found");
    }

    [Fact]
    public async Task A_zero_byte_dump_fails_loudly_as_truncated_rather_than_being_restored()
    {
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-zerobyte-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDir);
        File.WriteAllBytes(Path.Combine(backupDir, "manual-20260101-000000.sql.gz"), []);

        var run = await RunAsync(backupDir);

        run.ExitCode.Should().NotBe(0);
        run.StdOut.Should().Contain("truncated");
        run.HarboraCalls.Should().ContainSingle(c => c.Contains("--verdict fail") && c.Contains("truncated"));
    }

    [Fact]
    public async Task A_corrupt_non_gzip_file_named_sql_gz_fails_loudly_as_truncated()
    {
        // The extension alone would fool a check that only looked at the file name — this is the
        // one gzip -t itself is meant to catch: bytes that are not actually a gzip archive at all.
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "manual-20260101-000000.sql.gz"), "not actually gzip data");

        var run = await RunAsync(backupDir);

        run.ExitCode.Should().NotBe(0);
        run.StdOut.Should().Contain("truncated");
    }

    [Fact]
    public async Task A_failed_restore_fails_loudly_as_restore_errored_rather_than_a_bare_exit_code()
    {
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-restorefail-" + Guid.NewGuid().ToString("N"));
        WriteValidBackup(backupDir);

        var run = await RunAsync(backupDir, mode: "restore-fails");

        run.ExitCode.Should().NotBe(0);
        run.StdOut.Should().Contain("restore errored");
        run.HarboraCalls.Should().ContainSingle(c => c.Contains("--verdict fail") && c.Contains("restore errored"));
    }

    [Fact]
    public async Task A_scratch_container_that_never_becomes_ready_fails_loudly_as_restore_errored()
    {
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-neverready-" + Guid.NewGuid().ToString("N"));
        WriteValidBackup(backupDir);

        var run = await RunAsync(backupDir, mode: "never-ready");

        run.ExitCode.Should().NotBe(0);
        run.StdOut.Should().Contain("restore errored");
    }

    [Fact]
    public async Task A_sanity_query_that_cannot_be_answered_fails_loudly_rather_than_reading_as_a_clean_zero()
    {
        // The migrations-history table cannot be read at all in this mode — the fake psql exits
        // non-zero, the way a real one would against a database missing the table entirely. This is
        // the failure mode the whole sub-project is named for: a check that could not run must never
        // print the same "0" a genuinely empty, successfully-queried table would.
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-sanitynothing-" + Guid.NewGuid().ToString("N"));
        WriteValidBackup(backupDir);

        var run = await RunAsync(backupDir, mode: "sanity-nothing");

        run.ExitCode.Should().NotBe(0);
        run.StdOut.Should().Contain("sanity query returned nothing");
        run.HarboraCalls.Should().ContainSingle(c =>
            c.Contains("--verdict fail") && c.Contains("sanity query returned nothing"));
    }

    [Fact]
    public async Task An_empty_migrations_history_fails_loudly_as_a_distinct_reason_from_an_unreadable_table()
    {
        // Zero migrations is a real, successfully-obtained answer — not "nothing" — but it is still
        // proof the restored database has no real schema, so this is its own failure with its own
        // wording rather than being folded into "sanity query returned nothing".
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-zeromig-" + Guid.NewGuid().ToString("N"));
        WriteValidBackup(backupDir);

        var run = await RunAsync(backupDir, mode: "sanity-zero-migrations");

        run.ExitCode.Should().NotBe(0);
        run.StdOut.Should().Contain("empty migrations history");
        run.StdOut.Should().NotContain("returned nothing",
            "a real zero from a query that actually ran is a different failure from a query that could not run at all");
    }

    [Fact]
    public async Task An_unreadable_workspaces_table_fails_loudly_as_sanity_query_returned_nothing()
    {
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-wsfail-" + Guid.NewGuid().ToString("N"));
        WriteValidBackup(backupDir);

        var run = await RunAsync(backupDir, mode: "workspaces-query-fails");

        run.ExitCode.Should().NotBe(0);
        run.StdOut.Should().Contain("sanity query returned nothing");
        run.StdOut.Should().Contain("Workspaces");
    }

    // ---- the ledger table is allowed to be legitimately empty ----

    [Fact]
    public async Task An_empty_billing_ledger_is_reported_honestly_rather_than_failing_the_drill()
    {
        // A real, successfully-queried empty ledger (billing off, or a genuinely quiet install) is
        // not the same fact as a query that could not be answered — the drill must say so plainly
        // and still pass, matching the "unmeasured ≠ zero, but a real zero is not an error either"
        // rule this codebase applies everywhere else.
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-emptyledger-" + Guid.NewGuid().ToString("N"));
        WriteValidBackup(backupDir);

        var run = await RunAsync(backupDir, mode: "empty-ledger");

        run.ExitCode.Should().Be(0, "an empty ledger is a legitimate state, not a broken restore");
        run.StdOut.Should().Contain("PASS");
        run.StdOut.Should().Contain("no ledger rows");
    }

    // ---- the happy path, so "fails loudly" is not proven by a script that always fails ----

    [Fact]
    public async Task A_clean_restore_passes_and_records_a_pass_verdict_with_the_numbers_it_found()
    {
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-happy-" + Guid.NewGuid().ToString("N"));
        WriteValidBackup(backupDir, "manual-20260115-030000.sql.gz");

        var run = await RunAsync(backupDir, mode: "happy");

        run.ExitCode.Should().Be(0, run.StdOut + run.StdErr);
        run.StdOut.Should().Contain("PASS");
        run.StdOut.Should().Contain("manual-20260115-030000.sql.gz");
        run.HarboraCalls.Should().ContainSingle(c => c.Contains("--verdict pass"));
    }

    [Fact]
    public async Task The_newest_of_several_backups_is_the_one_restored()
    {
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-newest-" + Guid.NewGuid().ToString("N"));
        WriteValidBackup(backupDir, "manual-20260101-000000.sql.gz");
        WriteValidBackup(backupDir, "pre-upgrade-20260201-000000.sql.gz");
        // Same content, but must sort newest-by-mtime — touch it forward explicitly so the test does
        // not depend on how fast the two writes above happened to land.
        File.SetLastWriteTimeUtc(
            Path.Combine(backupDir, "pre-upgrade-20260201-000000.sql.gz"), DateTime.UtcNow.AddMinutes(5));

        var run = await RunAsync(backupDir, mode: "happy");

        run.ExitCode.Should().Be(0, run.StdOut + run.StdErr);
        run.StdOut.Should().Contain("pre-upgrade-20260201-000000.sql.gz");
        run.StdOut.Should().NotContain("manual-20260101-000000.sql.gz");
    }

    // ---- the drill must never leave a scratch container running, pass or fail ----

    [Fact]
    public async Task A_failed_drill_still_removes_its_scratch_container()
    {
        // The fake docker does not track real container lifecycle, so this asserts the *call* was
        // made — `docker rm -f <name>` — rather than a real daemon state. That call happening on a
        // FAILED run specifically is the property that matters: the exit trap must fire on every
        // path out of this script, not only the clean one, or a failed drill leaves a container
        // behind every time it is run. Real-daemon cleanup is exactly what only the server can
        // prove; see this class's own remarks and docs/disaster-recovery.md.
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-cleanup-fail-" + Guid.NewGuid().ToString("N"));
        WriteValidBackup(backupDir);

        var run = await RunAsync(backupDir, mode: "restore-fails");

        run.ExitCode.Should().NotBe(0);
        run.DockerCalls.Should().Contain(c => c.StartsWith("rm -f harbora-restore-drill-test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_passing_drill_also_removes_its_scratch_container()
    {
        var backupDir = Path.Combine(Path.GetTempPath(), "harbora-drill-cleanup-pass-" + Guid.NewGuid().ToString("N"));
        WriteValidBackup(backupDir);

        var run = await RunAsync(backupDir, mode: "happy");

        run.ExitCode.Should().Be(0, run.StdOut + run.StdErr);
        run.DockerCalls.Should().Contain(c => c.StartsWith("rm -f harbora-restore-drill-test", StringComparison.Ordinal));
    }
}
