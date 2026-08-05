using System.Security.Cryptography;
using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Deployments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Storage;

/// <summary>The credential issued for one bucket, and only that bucket.</summary>
public sealed record BucketCredential(string AccessKey, string SecretKey);

/// <summary>What an attempt did, and why it did not.</summary>
public sealed record BucketProvisionResult(bool Ok, BucketCredential? Credential, string? Reason)
{
    public static BucketProvisionResult Failed(string reason) => new(false, null, reason);
}

/// <summary>
/// Creating, measuring and removing buckets on the platform's object storage.
///
/// It drives the MinIO client in a throwaway container rather than speaking S3 over HTTP. That is
/// what makes a real per-bucket credential possible: creating a scoped user, attaching a policy and
/// setting a quota have no plain-S3 equivalent, and the alternatives were a single shared key —
/// which cannot be revoked for one tenant — or a key derived from the bucket name, which cannot be
/// rotated at all.
///
/// The client comes out of the storage server's own image, so there is no second image to pin.
///
/// Every method reports rather than throws. An operator who has not set object storage up is the
/// common case, and a page that 500s is a worse answer than one that says what is missing.
/// </summary>
public sealed class ObjectStorageAdmin(
    IServerEngineFactory engines,
    Harbora.Data.HarboraDbContext db,
    IOptions<ObjectStorageOptions> options,
    ILogger<ObjectStorageAdmin> log)
{
    private readonly ObjectStorageOptions _opt = options.Value;

    public bool IsConfigured => _opt.IsConfigured;
    public string? WhatIsMissing() => _opt.WhatIsMissing();
    public string CustomerEndpoint => _opt.CustomerEndpoint;

    /// <summary>
    /// Creates the bucket, a user that can reach only it, and its quota.
    ///
    /// The secret is generated rather than derived: a derived one is the same secret every time the
    /// same bucket name is used, on every installation that shares the platform key, and it cannot
    /// be rotated without renaming the bucket.
    /// </summary>
    public async Task<BucketProvisionResult> CreateAsync(string bucket, long quotaBytes, CancellationToken ct)
    {
        if (!IsConfigured)
            return BucketProvisionResult.Failed(
                $"Object storage is not configured on this installation. Missing: {WhatIsMissing()}.");

        if (!BucketName.IsValid(bucket))
            return BucketProvisionResult.Failed("That is not a name a bucket can have.");

        var credential = new BucketCredential(
            // Prefixed so a key is recognisable as Harbora's when somebody finds one in a log.
            "hb" + Random(12).ToLowerInvariant(),
            Random(28));

        var exit = await RunAsync(BucketCommands.Provision(
            _opt.Endpoint, _opt.AccessKey, _opt.SecretKey,
            bucket, credential.AccessKey, credential.SecretKey,
            BucketPolicy.For(bucket), BucketPolicy.NameFor(bucket),
            BucketCommands.QuotaArgument(quotaBytes)), ct);

        // Named by the step that failed. "It did not work" sends an operator to the logs; "the
        // storage server could not be reached" and "the bucket was made but its key was not" want
        // very different things done about them.
        return exit switch
        {
            0 => new BucketProvisionResult(true, credential, null),
            11 => BucketProvisionResult.Failed(
                "The storage server refused the platform's own credentials. Check Storage:S3:AccessKey and SecretKey."),
            12 => BucketProvisionResult.Failed("The storage server would not create the bucket."),
            14 => BucketProvisionResult.Failed("The bucket was created but its access policy could not be stored."),
            15 => BucketProvisionResult.Failed("The bucket was created but its key could not be issued."),
            16 => BucketProvisionResult.Failed("The bucket was created but its quota could not be set."),
            _ => BucketProvisionResult.Failed("The storage server could not be reached.")
        };
    }

    /// <summary>Removes the bucket and the credential that could reach it. Refused while it holds objects.</summary>
    public async Task<BucketProvisionResult> DeleteAsync(string bucket, string accessKey, CancellationToken ct)
    {
        if (!IsConfigured) return BucketProvisionResult.Failed("Object storage is not configured.");

        var exit = await RunAsync(BucketCommands.Remove(
            _opt.Endpoint, _opt.AccessKey, _opt.SecretKey,
            bucket, accessKey, BucketPolicy.NameFor(bucket)), ct);

        return exit switch
        {
            0 => new BucketProvisionResult(true, null, null),
            21 => BucketProvisionResult.Failed(
                "The bucket still has objects in it. Empty it first — Harbora will not delete somebody's data to remove the container for it."),
            _ => BucketProvisionResult.Failed("The storage server could not be reached.")
        };
    }

    /// <summary>
    /// What the bucket holds, or null when nobody could ask. Null is reported as never measured
    /// rather than as empty.
    /// </summary>
    public async Task<long?> MeasureAsync(string bucket, CancellationToken ct)
    {
        if (!IsConfigured) return null;

        var output = new StringBuilder();
        var exit = await RunAsync(
            BucketCommands.Measure(_opt.Endpoint, _opt.AccessKey, _opt.SecretKey, bucket),
            ct, output);

        return exit == 0 ? BucketCommands.ParseUsage(output.ToString()) : null;
    }

    private static string Random(int length) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(length))[..length];

    private async Task<int> RunAsync(
        IReadOnlyList<string> command, CancellationToken ct, StringBuilder? output = null)
    {
        try
        {
            // On the control plane's own machine: the storage server is reached over the platform
            // network, which is where the panel is, not wherever a tenant's workload happens to run.
            var serverId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(db.Servers.Where(s => s.IsLocal).Select(s => s.Id), ct);

            var docker = await engines.ResolveAsync(serverId, ct);

            return await docker.RunOneOffAsync(
                new DockerOneOffRequest(
                    _opt.ClientImage, command, [],
                    Env: null,
                    // The helper has to resolve the storage server's name, which only exists on the
                    // platform network. Without this it dials a name that does not resolve and the
                    // failure reads as bad credentials.
                    NetworkMode: _opt.Network),
                output is null ? null : new InlineProgress<string>(line =>
                {
                    lock (output) output.AppendLine(line);
                }),
                ct);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "An object storage command could not be run.");
            return -1;
        }
    }
}
