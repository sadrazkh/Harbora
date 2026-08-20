namespace Harbora.Domain.Storage;

/// <summary>
/// The env var names a bucket attach hands an application (F5, 2026-08-21 functions-and-services
/// plan).
///
/// <para>
/// Checked before inventing anything: the storage UI (<c>Views/Storage/Index.cshtml</c>) shows a
/// bucket's endpoint/access key/secret key as plain labels — "Endpoint", "Access key", "Secret
/// key" — and documents no environment-variable names anywhere. With nothing already documented,
/// these are the platform's one convention, matching what the market (Railway/Fly's S3-compatible
/// bindings) and the AWS SDKs both expect as env var names, so an app someone copy-pastes from
/// elsewhere is more likely to already read them.
/// </para>
/// </summary>
public static class BucketEnvKeys
{
    public const string Endpoint = "S3_ENDPOINT";
    public const string AccessKey = "S3_ACCESS_KEY";
    public const string SecretKey = "S3_SECRET_KEY";
    public const string Bucket = "S3_BUCKET";

    /// <summary>
    /// The four variables an attached bucket contributes. <paramref name="secretKeyPlaintext"/> is
    /// decrypted by the caller — this method never touches <c>ISecretProtector</c> itself, the same
    /// split <see cref="Apps.ConfigGroupMerge"/> draws between assembling the merge and deciding when
    /// to decrypt.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value, bool IsSecret)> EntriesFor(
        StorageBucket bucket, string customerEndpoint, string secretKeyPlaintext) =>
    [
        (Endpoint, customerEndpoint, false),
        (AccessKey, bucket.AccessKey, false),
        (SecretKey, secretKeyPlaintext, true),
        (Bucket, bucket.Name, false)
    ];
}
