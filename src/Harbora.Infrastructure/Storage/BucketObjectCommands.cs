namespace Harbora.Infrastructure.Storage;

/// <summary>One thing inside a bucket.</summary>
/// <param name="Key">The object's key relative to the prefix being listed, or the folder name.</param>
/// <param name="IsFolder">Whether opening it lists more things — a common prefix, not a real object.</param>
/// <param name="SizeBytes">Size in bytes; meaningless for a folder and reported as 0.</param>
/// <param name="ModifiedAt">Last modification, or null when the runtime gave something unreadable.</param>
public sealed record BucketObject(string Key, bool IsFolder, long SizeBytes, DateTimeOffset? ModifiedAt);

/// <summary>
/// The commands that read and write inside a bucket, and the parser for what they print.
///
/// The same discipline as <see cref="VolumeFileCommands"/>, for the same reason: <b>every script is
/// a constant</b> and every key travels as a positional argument. An object key is
/// attacker-controlled input on a multi-tenant platform, and a script assembled by interpolation is
/// a shell waiting for one with a quote in it.
///
/// Everything runs as the bucket's own credential, never the storage root. A browser that reached
/// the root would be a browser that could list every tenant's bucket from one tenant's page.
/// </summary>
public static class BucketObjectCommands
{
    private const string Alias = "hb";

    /// <summary>Entries directly under a prefix, as one JSON object per line.</summary>
    private const string ListScript =
        "mc alias set " + Alias + " \"$1\" \"$2\" \"$3\" >/dev/null || exit 11; " +
        "mc ls " + Alias + "/\"$4\"/\"$5\" --json 2>/dev/null || exit 12";

    private const string ReadScript =
        "mc alias set " + Alias + " \"$1\" \"$2\" \"$3\" >/dev/null || exit 11; " +
        "mc cat " + Alias + "/\"$4\"/\"$5\" 2>/dev/null | base64 -w 0 || exit 12";

    // The directory is created implicitly by S3 semantics; the pipe writes the object in one go.
    private const string WriteScript =
        "mc alias set " + Alias + " \"$1\" \"$2\" \"$3\" >/dev/null || exit 11; " +
        "printf %s \"$6\" | base64 -d > /tmp/upload || exit 13; " +
        "mc cp /tmp/upload " + Alias + "/\"$4\"/\"$5\" >/dev/null || exit 12";

    private const string DeleteScript =
        "mc alias set " + Alias + " \"$1\" \"$2\" \"$3\" >/dev/null || exit 11; " +
        "mc rm --recursive --force " + Alias + "/\"$4\"/\"$5\" >/dev/null || exit 12";

    public static IReadOnlyList<string> List(string endpoint, string accessKey, string secretKey, string bucket, string prefix) =>
        ["sh", "-c", ListScript, "sh", endpoint, accessKey, secretKey, bucket, prefix];

    public static IReadOnlyList<string> Read(string endpoint, string accessKey, string secretKey, string bucket, string key) =>
        ["sh", "-c", ReadScript, "sh", endpoint, accessKey, secretKey, bucket, key];

    public static IReadOnlyList<string> Write(string endpoint, string accessKey, string secretKey, string bucket, string key, string base64) =>
        ["sh", "-c", WriteScript, "sh", endpoint, accessKey, secretKey, bucket, key, base64];

    public static IReadOnlyList<string> Delete(string endpoint, string accessKey, string secretKey, string bucket, string key) =>
        ["sh", "-c", DeleteScript, "sh", endpoint, accessKey, secretKey, bucket, key];

    /// <summary>
    /// Reads what <c>mc ls --json</c> printed.
    ///
    /// A line it cannot make sense of is skipped rather than guessed at: the alternative is an
    /// entry with an invented name or size in somebody's object list, which they then click. And
    /// Docker's non-TTY framing again — the sixth parser written against this stream.
    /// </summary>
    public static IReadOnlyList<BucketObject> ParseListing(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        var entries = new List<BucketObject>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // From the first brace rather than position zero, for the framing reason above.
            var start = line.IndexOf('{');
            if (start < 0) continue;

            System.Text.Json.Nodes.JsonNode? node;
            try { node = System.Text.Json.Nodes.JsonNode.Parse(line[start..].TrimEnd('\r')); }
            catch (System.Text.Json.JsonException) { continue; }
            if (node is null) continue;

            // mc reports a failure as an ordinary JSON line in the same stream. It carries no key,
            // so this is also what keeps the word "error" from appearing in the listing as a file.
            var key = node["key"]?.GetValue<string>();
            if (string.IsNullOrEmpty(key)) continue;

            // A common prefix comes back with a trailing slash and no useful size.
            var isFolder = key.EndsWith('/')
                           || string.Equals(node["type"]?.GetValue<string>(), "folder", StringComparison.Ordinal);

            long size = 0;
            if (!isFolder)
            {
                if (node["size"] is not { } sizeNode) continue;
                try { size = sizeNode.GetValue<long>(); }
                catch (Exception e) when (e is FormatException or InvalidOperationException) { continue; }
                if (size < 0) continue;
            }

            DateTimeOffset? modified = null;
            if (node["lastModified"]?.GetValue<string>() is { Length: > 0 } stamp
                && DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                // An unreadable timestamp stays null rather than becoming 1970, which would sort
                // every such object to one end and read as data.
                modified = parsed;
            }

            entries.Add(new BucketObject(key.TrimEnd('/'), isFolder, size, modified));
        }

        // Folders first, then by name — the order every file browser uses, decided here so the page
        // cannot sort it differently from the links it draws.
        return entries
            .OrderByDescending(e => e.IsFolder)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The bytes of an object the helper printed as base64, or null when the stream held none.
    /// Shares <see cref="VolumeFileCommands.ParseBase64"/> because the trap is identical: image-pull
    /// chatter lands in the same buffer and is almost all inside the base64 alphabet.
    /// </summary>
    public static byte[]? ParseBase64(string? output) => VolumeFileCommands.ParseBase64(output);
}
