using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Storage;

/// <summary>The credential issued for one bucket.</summary>
public sealed record BucketCredential(string AccessKey, string SecretKey);

/// <summary>What a provisioning attempt did, and why it did not.</summary>
public sealed record BucketProvisionResult(bool Ok, BucketCredential? Credential, string? Reason)
{
    public static BucketProvisionResult Failed(string reason) => new(false, null, reason);
}

/// <summary>
/// Creating and removing buckets on the platform's object storage.
///
/// It talks S3 rather than a vendor's admin API on purpose: creating a bucket, deleting one and
/// reading its size are plain S3 operations that MinIO, Ceph and AWS all answer the same way. The
/// one thing that is not — issuing a per-bucket credential — is done by deriving one deterministically
/// and is documented as the compromise it is, rather than by binding the platform to MinIO's
/// admin protocol.
///
/// Every method reports rather than throws. An operator who has not set object storage up at all is
/// the common case, and a page that 500s is a worse answer than one that says what is missing.
/// </summary>
public sealed class ObjectStorageAdmin(
    IHttpClientFactory httpFactory,
    IOptions<ObjectStorageOptions> options,
    ILogger<ObjectStorageAdmin> log)
{
    private readonly ObjectStorageOptions _opt = options.Value;

    public bool IsConfigured => _opt.IsConfigured;
    public string? WhatIsMissing() => _opt.WhatIsMissing();
    public string CustomerEndpoint => _opt.CustomerEndpoint;

    /// <summary>
    /// Creates the bucket and returns the credential for it.
    ///
    /// The credential is derived from the platform's own secret and the bucket name, so it is
    /// reproducible without storing a second copy anywhere — and it is still stored encrypted on
    /// the row, because deriving it again requires the platform secret and the page has to be able
    /// to show it to somebody who asks.
    /// </summary>
    public async Task<BucketProvisionResult> CreateAsync(string bucket, CancellationToken ct)
    {
        if (!IsConfigured)
            return BucketProvisionResult.Failed(
                $"Object storage is not configured on this installation. Missing: {WhatIsMissing()}.");

        if (!BucketName.IsValid(bucket))
            return BucketProvisionResult.Failed("That is not a name a bucket can have.");

        try
        {
            var response = await SendAsync(HttpMethod.Put, bucket, ct);

            // 409 is "you already own this", which for a create is the desired end state and not a
            // failure — a retried provision must not look like a problem to investigate.
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Conflict)
                return BucketProvisionResult.Failed(
                    $"The storage server refused to create the bucket ({(int)response.StatusCode}).");

            return new BucketProvisionResult(true, Derive(bucket), null);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not create bucket {Bucket}.", bucket);
            return BucketProvisionResult.Failed("The storage server could not be reached.");
        }
    }

    /// <summary>Removes a bucket. Only succeeds when it is empty, which is the server's rule and a good one.</summary>
    public async Task<BucketProvisionResult> DeleteAsync(string bucket, CancellationToken ct)
    {
        if (!IsConfigured) return BucketProvisionResult.Failed("Object storage is not configured.");

        try
        {
            var response = await SendAsync(HttpMethod.Delete, bucket, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                return BucketProvisionResult.Failed(
                    "The bucket still has objects in it. Empty it first — Harbora will not delete somebody's data to remove a container for it.");

            // Already gone is the desired end state.
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                return BucketProvisionResult.Failed($"The storage server refused ({(int)response.StatusCode}).");

            return new BucketProvisionResult(true, null, null);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not delete bucket {Bucket}.", bucket);
            return BucketProvisionResult.Failed("The storage server could not be reached.");
        }
    }

    /// <summary>
    /// The credential for a bucket.
    ///
    /// Derived rather than random so it can be recomputed after a restore, and salted with the
    /// administrative secret so knowing a bucket name is not enough to know its key. This is the
    /// compromise: a real per-user credential needs a vendor admin API, and binding the platform to
    /// one is the thing this class exists to avoid.
    /// </summary>
    private BucketCredential Derive(string bucket)
    {
        var access = "hb" + Hash($"access:{bucket}")[..18].ToLowerInvariant();
        var secret = Hash($"secret:{bucket}")[..40];

        return new BucketCredential(access, secret);
    }

    private string Hash(string input)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_opt.SecretKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(input)));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string bucket, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(nameof(ObjectStorageAdmin));
        client.Timeout = TimeSpan.FromSeconds(20);

        var request = new HttpRequestMessage(method, $"{_opt.Endpoint.TrimEnd('/')}/{bucket}");

        // Basic rather than SigV4: MinIO accepts it for administrative calls over a private
        // network, and a hand-rolled SigV4 implementation is a large amount of subtle code to get
        // wrong in a way that only shows up against one vendor.
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_opt.AccessKey}:{_opt.SecretKey}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);

        return await client.SendAsync(request, ct);
    }
}
