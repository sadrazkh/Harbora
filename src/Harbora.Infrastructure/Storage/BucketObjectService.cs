using System.Text;
using Harbora.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Storage;

/// <summary>The outcome of a write or a delete, in words a page can show.</summary>
public sealed record ObjectOutcome(bool Ok, string Reason)
{
    public static readonly ObjectOutcome Success = new(true, "");
    public static ObjectOutcome Refused(string reason) => new(false, reason);
}

/// <summary>
/// Browsing what is actually in a bucket.
///
/// The bucket page could issue a key and report a size; it could not answer "what is in there",
/// which is the first question anybody asks — so the honest answer was "install a client".
///
/// Everything here runs as the <b>bucket's own credential</b>, in a throwaway container, exactly
/// like the volume browser runs as a container with the volume mounted. Using the storage root
/// would make one tenant's page a window onto every tenant's bucket, and no amount of careful key
/// handling would fix that.
/// </summary>
public sealed class BucketObjectService(
    IServerEngineFactory engines,
    Harbora.Data.HarboraDbContext db,
    ISecretProtector protector,
    IOptions<ObjectStorageOptions> options,
    ILogger<BucketObjectService> log)
{
    private readonly ObjectStorageOptions _opt = options.Value;

    /// <summary>
    /// The same ceiling the volume browser uses. A form post is not how somebody should move a
    /// large object, and the request buffer is not where it should be held while they try.
    /// </summary>
    public const long MaxObjectBytes = VolumeFileService.MaxFileBytes;

    public async Task<IReadOnlyList<BucketObject>> ListAsync(Guid bucketId, string? prefix, CancellationToken ct)
    {
        if (await CredentialAsync(bucketId, ct) is not { } c) return [];
        if (ObjectKey.NormalisePrefix(prefix) is not { } safe) return [];

        // A prefix must end in a slash or mc lists the sibling that merely starts with those
        // characters — "photos" would also match "photos-old".
        var listed = safe.Length == 0 ? "" : safe + "/";

        var output = new StringBuilder();
        var code = await RunAsync(
            BucketObjectCommands.List(_opt.Endpoint, c.AccessKey, c.SecretKey, c.Bucket, listed),
            ct, output);

        if (code != 0)
        {
            log.LogWarning("Listing bucket {Bucket} failed with {Code}.", c.Bucket, code);
            return [];
        }

        return BucketObjectCommands.ParseListing(output.ToString());
    }

    public async Task<byte[]?> ReadAsync(Guid bucketId, string key, CancellationToken ct)
    {
        if (await CredentialAsync(bucketId, ct) is not { } c) return null;
        if (ObjectKey.Normalise(key) is not { Length: > 0 } safe) return null;

        var output = new StringBuilder();
        var code = await RunAsync(
            BucketObjectCommands.Read(_opt.Endpoint, c.AccessKey, c.SecretKey, c.Bucket, safe),
            ct, output);

        return code == 0 ? BucketObjectCommands.ParseBase64(output.ToString()) : null;
    }

    public async Task<ObjectOutcome> WriteAsync(Guid bucketId, string key, byte[] content, CancellationToken ct)
    {
        if (content.LongLength > MaxObjectBytes)
            return ObjectOutcome.Refused($"Objects larger than {MaxObjectBytes / 1024 / 1024} MB cannot be uploaded here.");

        if (await CredentialAsync(bucketId, ct) is not { } c)
            return ObjectOutcome.Refused("That bucket is not available.");
        if (ObjectKey.Normalise(key) is not { Length: > 0 } safe)
            return ObjectOutcome.Refused("That is not a key this browser will write to.");

        var code = await RunAsync(
            BucketObjectCommands.Write(_opt.Endpoint, c.AccessKey, c.SecretKey, c.Bucket, safe,
                Convert.ToBase64String(content)), ct);

        // 12 is the copy step; on a bucket with a quota it is what "full" looks like, so it is
        // reported as the thing that happened rather than as a generic failure.
        return code switch
        {
            0 => ObjectOutcome.Success,
            12 => ObjectOutcome.Refused("The object could not be written — the bucket may be at its quota."),
            _ => ObjectOutcome.Refused("The object could not be written.")
        };
    }

    public async Task<ObjectOutcome> DeleteAsync(Guid bucketId, string key, CancellationToken ct)
    {
        if (await CredentialAsync(bucketId, ct) is not { } c)
            return ObjectOutcome.Refused("That bucket is not available.");
        if (ObjectKey.Normalise(key) is not { Length: > 0 } safe)
            return ObjectOutcome.Refused("That is not a key this browser will delete.");

        var code = await RunAsync(
            BucketObjectCommands.Delete(_opt.Endpoint, c.AccessKey, c.SecretKey, c.Bucket, safe), ct);

        return code == 0 ? ObjectOutcome.Success : ObjectOutcome.Refused("The object could not be deleted.");
    }

    private sealed record Credential(string Bucket, string AccessKey, string SecretKey);

    /// <summary>
    /// The bucket's own key, decrypted. Null when the bucket is gone, storage is not configured, or
    /// the secret cannot be read — each of which is a reason to do nothing rather than to fall back
    /// to a credential with more reach.
    /// </summary>
    private async Task<Credential?> CredentialAsync(Guid bucketId, CancellationToken ct)
    {
        if (!_opt.IsConfigured) return null;

        var row = await db.StorageBuckets
            .Where(b => b.Id == bucketId)
            .Select(b => new { b.Name, b.AccessKey, b.EncryptedSecretKey })
            .FirstOrDefaultAsync(ct);

        if (row is null || string.IsNullOrEmpty(row.AccessKey)) return null;

        try
        {
            return new Credential(row.Name, row.AccessKey, protector.Unprotect(row.EncryptedSecretKey));
        }
        catch (Exception e)
        {
            log.LogWarning(e, "The secret for bucket {Bucket} could not be read.", row.Name);
            return null;
        }
    }

    private async Task<int> RunAsync(IReadOnlyList<string> command, CancellationToken ct, StringBuilder? output = null)
    {
        try
        {
            var serverId = await db.Servers.Where(s => s.IsLocal).Select(s => s.Id).FirstOrDefaultAsync(ct);
            var docker = await engines.ResolveAsync(serverId, ct);

            return await docker.RunOneOffAsync(
                new DockerOneOffRequest(_opt.ClientImage, command, [], Env: null, NetworkMode: _opt.Network),
                output is null ? null : new Progress<string>(line =>
                {
                    lock (output) output.AppendLine(line);
                }),
                ct);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "An object command could not be run.");
            return -1;
        }
    }
}
