using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Harbora.Application.Abstractions;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// One storage adapter covering both destination types. Artifacts are always staged locally
/// first; for S3 destinations they're then uploaded (any S3-compatible endpoint via a custom
/// ServiceURL + path-style addressing). S3 secret keys are decrypted per call.
/// </summary>
public sealed class BackupStorage(IOptions<BackupOptions> options, ISecretProtector protector) : IBackupStorage
{
    private readonly BackupOptions _opt = options.Value;

    public string LocalStagingDir => _opt.StagingDir;

    public async Task<(string ArtifactRef, long SizeBytes)> PutFileAsync(BackupDestination dest, string key, string localFilePath, CancellationToken ct)
    {
        var size = new FileInfo(localFilePath).Length;

        if (dest.Type == BackupDestinationType.Local)
        {
            var root = string.IsNullOrWhiteSpace(dest.LocalPath) ? _opt.StagingDir : dest.LocalPath;
            Directory.CreateDirectory(root);
            var finalPath = Path.Combine(root, key);
            if (!string.Equals(Path.GetFullPath(finalPath), Path.GetFullPath(localFilePath), StringComparison.OrdinalIgnoreCase))
                File.Copy(localFilePath, finalPath, overwrite: true);
            return (finalPath, size);
        }

        // S3-compatible
        using var client = CreateS3(dest);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = dest.Bucket,
            Key = key,
            FilePath = localFilePath
        }, ct);
        return ($"s3://{dest.Bucket}/{key}", size);
    }

    public async Task<string> GetToLocalAsync(BackupDestination dest, string artifactRef, CancellationToken ct)
    {
        if (dest.Type == BackupDestinationType.Local)
            return artifactRef; // already a local path

        Directory.CreateDirectory(_opt.StagingDir);
        var (bucket, objectKey) = ParseS3(artifactRef);
        var localPath = Path.Combine(_opt.StagingDir, Path.GetFileName(objectKey));
        using var client = CreateS3(dest);
        using var response = await client.GetObjectAsync(bucket, objectKey, ct);
        await response.WriteResponseStreamToFileAsync(localPath, append: false, ct);
        return localPath;
    }

    public async Task DeleteAsync(BackupDestination dest, string artifactRef, CancellationToken ct)
    {
        if (dest.Type == BackupDestinationType.Local)
        {
            if (File.Exists(artifactRef)) File.Delete(artifactRef);
            return;
        }
        var (bucket, objectKey) = ParseS3(artifactRef);
        using var client = CreateS3(dest);
        await client.DeleteObjectAsync(bucket, objectKey, ct);
    }

    private AmazonS3Client CreateS3(BackupDestination dest)
    {
        var secret = string.IsNullOrEmpty(dest.EncryptedSecretKey) ? "" : protector.Unprotect(dest.EncryptedSecretKey);
        var creds = new BasicAWSCredentials(dest.AccessKey, secret);
        var config = new AmazonS3Config { ForcePathStyle = true };
        if (!string.IsNullOrWhiteSpace(dest.Endpoint)) config.ServiceURL = dest.Endpoint;
        if (!string.IsNullOrWhiteSpace(dest.Region)) config.AuthenticationRegion = dest.Region;
        return new AmazonS3Client(creds, config);
    }

    private static (string Bucket, string Key) ParseS3(string artifactRef)
    {
        var withoutScheme = artifactRef.Replace("s3://", "");
        var slash = withoutScheme.IndexOf('/');
        return (withoutScheme[..slash], withoutScheme[(slash + 1)..]);
    }
}
