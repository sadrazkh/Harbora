namespace Harbora.Infrastructure.Storage;

/// <summary>
/// The MinIO client commands that create, measure and remove a bucket.
///
/// Run in a throwaway container, like everything else this platform does to something it cannot
/// reach directly — and out of the storage server's own image, which ships <c>mc</c>, so there is
/// no second image to pin and keep current.
///
/// Using the client rather than speaking S3 over HTTP is what makes a real per-bucket credential
/// possible: creating a scoped user, attaching a policy and setting a quota are administrative
/// operations with no plain-S3 equivalent, and the alternative was one shared key or a key derived
/// from the bucket name — neither of which can be revoked for one tenant.
///
/// Same rule as the volume browser: **every script is a constant**, and every value travels as a
/// positional argument. A bucket name, an access key and a policy document are all inputs, and an
/// interpolated script is a shell waiting for one of them to contain a quote.
/// </summary>
public static class BucketCommands
{
    /// <summary>An alias name inside the throwaway container. Never leaves it.</summary>
    private const string Alias = "hb";

    // Exit codes are distinct so a failure can be reported as the step that failed rather than as
    // "it did not work" — creating the bucket and attaching the policy fail for very different
    // reasons and want different things done about them.
    private const string ProvisionScript =
        "set -e; " +
        "mc alias set " + Alias + " \"$1\" \"$2\" \"$3\" >/dev/null || exit 11; " +
        "mc mb --ignore-existing " + Alias + "/\"$4\" >/dev/null || exit 12; " +
        "printf %s \"$7\" > /tmp/policy.json || exit 13; " +
        // Removed first so a re-run replaces the document instead of failing on a name that exists,
        // which is what a retried provision would otherwise do.
        "mc admin policy rm " + Alias + " \"$8\" >/dev/null 2>&1 || true; " +
        "mc admin policy create " + Alias + " \"$8\" /tmp/policy.json >/dev/null || exit 14; " +
        "mc admin user add " + Alias + " \"$5\" \"$6\" >/dev/null || exit 15; " +
        "mc admin policy attach " + Alias + " \"$8\" --user \"$5\" >/dev/null 2>&1 || true; " +
        "if [ -n \"$9\" ]; then mc quota set " + Alias + "/\"$4\" --size \"$9\" >/dev/null || exit 16; fi";

    private const string RemoveScript =
        "mc alias set " + Alias + " \"$1\" \"$2\" \"$3\" >/dev/null || exit 11; " +
        // Deliberately not --force. Emptying somebody's bucket is not something "delete the bucket"
        // should be allowed to mean, and the refusal is reported as "empty it first".
        "mc rb " + Alias + "/\"$4\" >/dev/null 2>&1 || exit 21; " +
        "mc admin user remove " + Alias + " \"$5\" >/dev/null 2>&1 || true; " +
        "mc admin policy rm " + Alias + " \"$6\" >/dev/null 2>&1 || true";

    private const string MeasureScript =
        "mc alias set " + Alias + " \"$1\" \"$2\" \"$3\" >/dev/null || exit 11; " +
        "mc du --json " + Alias + "/\"$4\"";

    /// <summary>Creates the bucket, its user, its policy and its quota.</summary>
    /// <param name="quota">An mc size such as <c>10GiB</c>, or empty for no quota.</param>
    public static IReadOnlyList<string> Provision(
        string endpoint, string rootUser, string rootPassword,
        string bucket, string accessKey, string secretKey, string policyJson, string policyName, string quota) =>
        ["sh", "-c", ProvisionScript, "sh",
         endpoint, rootUser, rootPassword, bucket, accessKey, secretKey, policyJson, policyName, quota];

    /// <summary>Removes the bucket and the credential that could reach it.</summary>
    public static IReadOnlyList<string> Remove(
        string endpoint, string rootUser, string rootPassword,
        string bucket, string accessKey, string policyName) =>
        ["sh", "-c", RemoveScript, "sh", endpoint, rootUser, rootPassword, bucket, accessKey, policyName];

    /// <summary>Asks how much is in the bucket.</summary>
    public static IReadOnlyList<string> Measure(
        string endpoint, string rootUser, string rootPassword, string bucket) =>
        ["sh", "-c", MeasureScript, "sh", endpoint, rootUser, rootPassword, bucket];

    /// <summary>
    /// A byte count as an mc size. Empty when there is no quota, because <c>--size 0</c> means a
    /// quota of nothing rather than no quota, and a bucket nobody can write to is not what "no
    /// limit" is supposed to produce.
    /// </summary>
    public static string QuotaArgument(long bytes) => bytes <= 0 ? string.Empty : $"{bytes}B";

    /// <summary>
    /// Reads what <c>mc du --json</c> printed. Null when it said nothing usable — which is not the
    /// same as a bucket holding nothing, and is reported as never measured.
    /// </summary>
    public static long? ParseUsage(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        // One JSON object per line. The last one with a size wins: mc prints a line per path and
        // the total last, and taking the first would report one prefix as the whole bucket.
        long? found = null;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Read from the first brace rather than requiring the line to start with one. Docker
            // frames the output of a container with no TTY, so each line arrives with a few control
            // bytes stuck to the front — StorageMeasurement documents the same trap from the same
            // source, and requiring position zero meant every measurement came back unreadable and
            // the bucket was reported as never measured.
            //
            // A pass that stripped control characters first went in here and came out again: this
            // line already skips them, so the strip changed no outcome, and a redundant guard reads
            // as the one doing the work until somebody weakens the one that is.
            var start = line.IndexOf('{');
            if (start < 0) continue;

            var trimmed = line[start..].Trim();

            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(trimmed);
                if (node?["size"] is { } size && size.GetValue<long>() is var bytes && bytes >= 0)
                    found = bytes;
            }
            catch (System.Text.Json.JsonException)
            {
                // A line that is not JSON is mc talking about something else — a warning, a
                // progress note. Skipped rather than treated as a measurement of zero.
            }
        }

        return found;
    }
}
