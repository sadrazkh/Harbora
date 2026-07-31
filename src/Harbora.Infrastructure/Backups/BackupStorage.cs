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
public sealed class BackupStorage(
    IOptions<BackupOptions> options,
    IOptions<Deployments.HarboraRuntimeOptions> runtime,
    ISecretProtector protector,
    IDockerEngine docker) : IBackupStorage
{
    private readonly BackupOptions _opt = options.Value;
    private readonly Deployments.HarboraRuntimeOptions _runtime = runtime.Value;

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

        if (dest.Type == BackupDestinationType.Sftp)
        {
            // The artifact is already staged in the volume the transfer container mounts, so the
            // upload only has to name it.
            await RunSftpAsync(dest, SftpTransfer.Upload(
                dest.SftpHost!, dest.SftpPort, dest.SftpUsername!, SftpPassword(dest),
                dest.SftpDirectory, key), "upload", ct);

            // The reference records where it went, so a later fetch does not depend on the
            // destination still being configured the same way.
            var directory = string.IsNullOrWhiteSpace(dest.SftpDirectory) ? "" : dest.SftpDirectory!.TrimEnd('/');
            return ($"sftp://{dest.SftpHost}{directory}/{key}", size);
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

        if (dest.Type == BackupDestinationType.Sftp)
        {
            var name = Path.GetFileName(artifactRef);
            await RunSftpAsync(dest, SftpTransfer.Download(
                dest.SftpHost!, dest.SftpPort, dest.SftpUsername!, SftpPassword(dest),
                dest.SftpDirectory, name), "download", ct);

            var staged = Path.Combine(_opt.StagingDir, name);
            if (!File.Exists(staged))
                throw new InvalidOperationException(
                    $"The transfer reported success but {name} did not arrive in {_opt.StagingDir}. " +
                    $"Check that the panel and the transfer container share the volume '{_opt.StagingVolume}'.");
            return staged;
        }

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
        if (dest.Type == BackupDestinationType.Sftp)
        {
            await RunSftpAsync(dest, SftpTransfer.Delete(
                dest.SftpHost!, dest.SftpPort, dest.SftpUsername!, SftpPassword(dest),
                dest.SftpDirectory, Path.GetFileName(artifactRef)), "delete", ct);
            return;
        }

        var (bucket, objectKey) = ParseS3(artifactRef);
        using var client = CreateS3(dest);
        await client.DeleteObjectAsync(bucket, objectKey, ct);
    }

    /// <summary>
    /// Runs an SFTP command in a one-off container with the staging volume mounted, refusing up
    /// front when the destination cannot be trusted — see <see cref="SftpTransfer.WhyUnusable"/>.
    /// </summary>
    private async Task RunSftpAsync(BackupDestination dest, SftpCommand command, string what, CancellationToken ct)
    {
        if (SftpTransfer.WhyUnusable(dest.SftpHost, dest.SftpUsername, dest.SftpHostKey) is { } refusal)
            throw new InvalidOperationException(refusal);

        var env = new Dictionary<string, string>(command.Env) { ["SFTP_HOST_KEY"] = dest.SftpHostKey! };

        var output = new System.Text.StringBuilder();
        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            SftpTransfer.ClientImage, command.Command,
            [(_opt.StagingVolume, "/backup", what == "upload" || what == "delete")],
            Env: env,
            // The panel's own connectivity, rather than whatever the default bridge can reach: a
            // destination the panel can resolve must be resolvable here too. Found live — a server
            // on an internal network failed with "Name does not resolve", which reads like a typo
            // in the address rather than a difference in networking.
            NetworkMode: $"container:{_runtime.PanelContainerName}"),
            new Deployments.InlineProgress<string>(l => { lock (output) output.AppendLine(l); }), ct);

        if (exit != 0)
            throw new InvalidOperationException(
                $"The SFTP {what} failed (exit {exit}). {Deployments.LogText.Clean(output.ToString()).Trim()}");
    }

    private string SftpPassword(BackupDestination dest) =>
        string.IsNullOrEmpty(dest.EncryptedSftpPassword) ? "" : protector.Unprotect(dest.EncryptedSftpPassword);

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
