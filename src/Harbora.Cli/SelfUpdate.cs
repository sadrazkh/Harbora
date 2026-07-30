using System.Reflection;
using System.Runtime.InteropServices;

namespace Harbora.Cli;

/// <summary>
/// Deciding whether this CLI is behind, and which file would replace it.
///
/// Kept pure and separate from the downloading so the comparison can be tested: getting it wrong
/// either nags people who are up to date, or stays quiet while a CLI too old for the panel keeps
/// failing in ways that look like server bugs.
/// </summary>
public static class SelfUpdate
{
    public const string Repository = "sadrazkh/Harbora";

    /// <summary>This binary's version, as published.</summary>
    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>
    /// Whether <paramref name="candidate"/> is newer than <paramref name="current"/>.
    ///
    /// Anything unparseable answers "no". A version we cannot read is not evidence of being behind,
    /// and telling someone to update on the strength of a string we did not understand is worse than
    /// staying quiet.
    /// </summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!TryParse(candidate, out var a) || !TryParse(current, out var b)) return false;
        return a > b;
    }

    /// <summary>Accepts "1.2.3", "v1.2.3", and trailing pre-release/build parts.</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim().TrimStart('v', 'V');
        var cut = value.IndexOfAny(['-', '+']);
        if (cut >= 0) value = value[..cut];

        // At least major.minor. A bare number is not a version we can reason about: "2026-07-30"
        // otherwise cuts at the dash and reads as version 2026, which would tell every user of a
        // date-versioned panel that their CLI is years behind.
        var parts = value.Split('.');
        if (parts.Length < 2) return false;

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length) { numbers[i] = 0; continue; }
            if (!int.TryParse(parts[i], out numbers[i])) return false;
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>
    /// The release asset for the machine running this. Matches the names the release workflow
    /// publishes, so a mismatch here means downloading a binary for the wrong architecture — the
    /// "exec format error" class of failure, which is invisible until the moment it is run.
    /// </summary>
    public static string? AssetNameFor(OSPlatform platform, Architecture architecture)
    {
        var os = platform == OSPlatform.Windows ? "win"
               : platform == OSPlatform.OSX ? "osx"
               : platform == OSPlatform.Linux ? "linux"
               : null;
        if (os is null) return null;

        var arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null
        };
        if (arch is null) return null;

        return $"harbora-{os}-{arch}" + (os == "win" ? ".exe" : "");
    }

    /// <summary>The asset for the current process.</summary>
    public static string? AssetNameForThisMachine() =>
        AssetNameFor(
            OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX
            : OSPlatform.Linux,
            RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// Where the replacement is staged before it takes over.
    ///
    /// Windows will not let a running executable be overwritten, so the old one is renamed aside and
    /// the new file takes its name — the rename is permitted while the file is open, and the leftover
    /// is cleaned up on the next run.
    /// </summary>
    public static string RetiredPathFor(string executable) => executable + ".old";

    /// <summary>Removes the previous binary left behind by an update, if it is no longer in use.</summary>
    public static void CleanUpPreviousBinary(string executable)
    {
        var retired = RetiredPathFor(executable);
        try { if (File.Exists(retired)) File.Delete(retired); }
        catch { /* still running, or not ours to delete — it is 30 MB, not a correctness problem */ }
    }
}
