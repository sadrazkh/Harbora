using System.Globalization;
using Harbora.Modules.Sync.Contracts;

namespace Harbora.Modules.Sync.Domain;

/// <summary>One thing wrong with a sync configuration, in words the person who wrote it can act on.</summary>
public sealed record SyncValidationError(string Field, string Message);

/// <summary>
/// Rules that decide whether a sync configuration means what its author thinks it means.
///
/// <para>
/// Pure and synchronous, because the mistakes worth catching here are the ones that produce a space
/// which looks configured and quietly syncs nothing — or worse, one where a mode combination sends
/// plaintext to a device that was supposed to hold ciphertext.
/// </para>
/// </summary>
public static class SyncValidation
{
    /// <summary>
    /// A Syncthing device id: 8 groups of 7 characters, base32 alphabet, separated by hyphens, with
    /// the hyphens optional.
    ///
    /// <para>
    /// Validated because it reaches the engine's API and identifies who receives the files. A typo
    /// is not a security hole — the wrong id simply never connects — but a value carrying other
    /// characters has no business being sent at all.
    /// </para>
    /// </summary>
    public static bool IsValidDeviceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var compact = value.Replace("-", "", StringComparison.Ordinal).Trim();
        if (compact.Length != 56) return false;

        // RFC 4648 base32 without padding, as Syncthing prints it. '0', '1' and '8' are excluded
        // from the alphabet, but accepting them here would only mean the engine rejects the id
        // later with a worse message, so the check stays on the character class.
        return compact.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));
    }

    /// <summary>Normalises a device id to the grouped form Syncthing displays.</summary>
    public static string NormaliseDeviceId(string value)
    {
        var compact = value.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();

        return string.Join('-', Enumerable.Range(0, 8).Select(i => compact.Substring(i * 7, 7)));
    }

    public static IReadOnlyList<SyncValidationError> ValidateSpace(SyncSpace space)
    {
        ArgumentNullException.ThrowIfNull(space);

        var errors = new List<SyncValidationError>();

        if (string.IsNullOrWhiteSpace(space.Name))
            errors.Add(new SyncValidationError(nameof(space.Name), "Give the sync space a name."));
        else if (space.Name.Length > 128)
            errors.Add(new SyncValidationError(nameof(space.Name), "That name is too long."));

        if (string.IsNullOrWhiteSpace(space.LocalPath))
            errors.Add(new SyncValidationError(nameof(space.LocalPath), "Choose a folder to synchronise."));

        errors.AddRange(ValidateVersioning(space.VersioningMode, space.VersioningParameter));

        return errors;
    }

    private static IEnumerable<SyncValidationError> ValidateVersioning(
        SyncVersioningMode mode, int parameter)
    {
        switch (mode)
        {
            case SyncVersioningMode.Trash when parameter < 0:
                yield return new SyncValidationError(nameof(SyncSpace.VersioningParameter),
                    "Days to keep deleted files cannot be negative. Use 0 to keep them indefinitely.");
                break;

            case SyncVersioningMode.Simple when parameter < 1:
                yield return new SyncValidationError(nameof(SyncSpace.VersioningParameter),
                    "Keep at least one old version, or choose 'None' instead — a versioning mode that " +
                    "keeps nothing is just versioning switched off with extra steps.");
                break;
        }
    }

    /// <summary>
    /// Whether a device may join a space in a given mode.
    ///
    /// <para>
    /// The combination that matters: an untrusted device — one that exists to store data it cannot
    /// read — must be joined as <see cref="SyncMode.EncryptedReceiveOnly"/>. Any other mode sends it
    /// plaintext, which silently removes the only protection the arrangement had.
    /// </para>
    /// </summary>
    public static SyncValidationError? ValidateMembership(
        SyncDevice device, SyncMode mode, string? encryptionPassword)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.IsUntrusted && mode is not SyncMode.EncryptedReceiveOnly)
            return new SyncValidationError(nameof(mode),
                $"'{device.Name}' is an untrusted device, so it can only receive an encrypted copy. " +
                "Any other mode would send it readable files.");

        if (mode is SyncMode.EncryptedReceiveOnly)
        {
            if (!device.IsUntrusted)
                return new SyncValidationError(nameof(mode),
                    $"'{device.Name}' is a trusted device. Mark it untrusted before sending it an " +
                    "encrypted-only copy, so it is clear everywhere that it cannot read these files.");

            if (string.IsNullOrWhiteSpace(encryptionPassword))
                return new SyncValidationError(nameof(encryptionPassword),
                    "An encrypted copy needs a password. Without it there is nothing to encrypt with, " +
                    "and the device would receive readable files.");

            if (encryptionPassword.Length < 12)
                return new SyncValidationError(nameof(encryptionPassword),
                    "Use at least 12 characters. This password is the only thing standing between the " +
                    "storage device and your files.");
        }

        if (device.Status is SyncDeviceStatus.Revoked)
            return new SyncValidationError(nameof(device),
                $"'{device.Name}' was revoked. Pair it again before sharing anything with it.");

        return null;
    }

    /// <summary>
    /// True when a space would send changes nowhere and receive them from nowhere.
    ///
    /// <para>
    /// Two receive-only ends, or two send-only ends, is a configuration that looks complete and moves
    /// no data. It is not an error the engine reports — it simply sits there.
    /// </para>
    /// </summary>
    public static bool WouldNeverSync(IReadOnlyCollection<SyncMode> modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        if (modes.Count < 2) return false;

        var anySends = modes.Any(m => m is SyncMode.SendAndReceive or SyncMode.SendOnly);
        var anyReceives = modes.Any(m =>
            m is SyncMode.SendAndReceive or SyncMode.ReceiveOnly or SyncMode.EncryptedReceiveOnly);

        return !anySends || !anyReceives;
    }
}

/// <summary>
/// Reads the engine's conflict filenames.
///
/// <para>
/// Syncthing names a losing copy
/// <c>&lt;name&gt;.sync-conflict-&lt;date&gt;-&lt;time&gt;-&lt;device&gt;&lt;ext&gt;</c>. Parsing it
/// is how a conflict gets shown as "your copy of report.docx from Tuesday" instead of as a file with
/// an alarming name that people delete without reading.
/// </para>
/// </summary>
public static class SyncConflictName
{
    public const string Marker = ".sync-conflict-";

    public static bool IsConflictFile(string fileName) =>
        !string.IsNullOrEmpty(fileName) && fileName.Contains(Marker, StringComparison.Ordinal);

    /// <summary>
    /// The original path a conflicting copy belongs to, and the device that produced it.
    ///
    /// <para>
    /// Returns null for a name that does not carry the marker. The device id is only returned when
    /// it is actually present and well-formed — a guess about who changed a file is worse than
    /// saying nothing, because it is acted on.
    /// </para>
    /// </summary>
    public static (string OriginalPath, string? Device, DateTimeOffset? At)? Parse(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        var normalised = relativePath.Replace('\\', '/');
        var marker = normalised.IndexOf(Marker, StringComparison.Ordinal);
        if (marker < 0) return null;

        var stem = normalised[..marker];
        var remainder = normalised[(marker + Marker.Length)..];

        // The extension is carried past the suffix, so "notes.sync-conflict-…-ABC.txt" belongs to
        // "notes.txt" rather than to "notes".
        var extension = Path.GetExtension(remainder);
        var original = stem + extension;

        var body = extension.Length > 0 ? remainder[..^extension.Length] : remainder;
        var parts = body.Split('-', StringSplitOptions.RemoveEmptyEntries);

        DateTimeOffset? at = null;
        if (parts.Length >= 2
            && DateTimeOffset.TryParseExact($"{parts[0]}{parts[1]}", "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            at = parsed;
        }

        // Syncthing writes the first seven characters of the device id here.
        var device = parts.Length >= 3 && parts[2].Length > 0 && parts[2].All(char.IsAsciiLetterOrDigit)
            ? parts[2]
            : null;

        return (original, device, at);
    }
}
