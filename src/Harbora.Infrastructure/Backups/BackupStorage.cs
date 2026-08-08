using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Harbora.Application.Abstractions;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Shared;
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
            var root = LocalRoot(dest);
            Directory.CreateDirectory(root);

            // A key may name a folder as well as a file — the backup module groups its artifacts by
            // repository, so every key it writes has one. Only the root was created here, and
            // File.Copy into a directory that is not there throws rather than making it, so every
            // snapshot into a local repository failed with a path error. Confined first and created
            // second: the check is what says this folder is one the destination may have.
            Directory.CreateDirectory(Path.GetDirectoryName(Confined(root, key, "written to"))!);

            // The reference is the root joined to the key as the caller spelled it, and deliberately
            // not the resolved form the check returned. BackupEngine compares this string against the
            // staging path it passed in to decide whether the staging copy is a second copy it may
            // delete — so normalising here would, for a destination that IS the staging directory
            // under a differently spelled path, turn "these are the same file" into "these differ"
            // and delete the artifact it had just stored.
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

    public async Task<string> GetToLocalAsync(
        BackupDestination dest, string artifactRef, CancellationToken ct, string? localFileName = null)
    {
        // Nothing to download: the artifact is a file already. A caller that recorded the reference
        // PutFileAsync returned hands back an absolute path and gets it straight back; a caller that
        // asks by the key it stored under gets that key resolved inside the destination, which is
        // where the artifact actually is.
        if (dest.Type == BackupDestinationType.Local)
            return LocalArtifact(dest, artifactRef, "read from");

        Directory.CreateDirectory(_opt.StagingDir);

        if (dest.Type == BackupDestinationType.Sftp)
        {
            var name = Path.GetFileName(artifactRef);

            // The remote name and the local one are separate questions: what to ask the server for,
            // and what to call the copy that lands in the volume the panel shares with the transfer
            // container. Only the second is the caller's to choose.
            var local = string.IsNullOrWhiteSpace(localFileName) ? name : Path.GetFileName(localFileName);

            await RunSftpAsync(dest, SftpTransfer.Download(
                dest.SftpHost!, dest.SftpPort, dest.SftpUsername!, SftpPassword(dest),
                dest.SftpDirectory, name, local), "download", ct);

            var staged = Path.Combine(_opt.StagingDir, local);
            if (!File.Exists(staged))
                throw new InvalidOperationException(
                    $"The transfer reported success but {local} did not arrive in {_opt.StagingDir}. " +
                    $"Check that the panel and the transfer container share the volume '{_opt.StagingVolume}'.");
            return staged;
        }

        var (bucket, objectKey) = S3Location(dest, artifactRef);
        var localPath = Path.Combine(_opt.StagingDir, string.IsNullOrWhiteSpace(localFileName)
            ? Path.GetFileName(objectKey)
            : Path.GetFileName(localFileName));

        using var client = CreateS3(dest);
        using var response = await client.GetObjectAsync(bucket, objectKey, ct);
        await response.WriteResponseStreamToFileAsync(localPath, append: false, ct);
        return localPath;
    }

    public async Task DeleteAsync(BackupDestination dest, string artifactRef, CancellationToken ct)
    {
        if (dest.Type == BackupDestinationType.Local)
        {
            // Resolved the same way a read resolves it. Retention deletes by whatever reference the
            // caller stored, and a key it could not resolve simply found no file — so the row went
            // and the artifact stayed, on every pass, for ever, while every screen said it had been
            // pruned. A delete that removes nothing must not be reachable by spelling.
            var path = LocalArtifact(dest, artifactRef, "deleted from");
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        if (dest.Type == BackupDestinationType.Sftp)
        {
            await RunSftpAsync(dest, SftpTransfer.Delete(
                dest.SftpHost!, dest.SftpPort, dest.SftpUsername!, SftpPassword(dest),
                dest.SftpDirectory, Path.GetFileName(artifactRef)), "delete", ct);
            return;
        }

        var (bucket, objectKey) = S3Location(dest, artifactRef);
        using var client = CreateS3(dest);
        await client.DeleteObjectAsync(bucket, objectKey, ct);
    }

    /// <summary>Where a local destination keeps its artifacts; staging when it names nowhere else.</summary>
    private string LocalRoot(BackupDestination dest) =>
        string.IsNullOrWhiteSpace(dest.LocalPath) ? _opt.StagingDir : dest.LocalPath;

    /// <summary>
    /// The file a reference names on a local destination.
    ///
    /// <para>
    /// Two kinds of reference arrive here and both are legitimate. The platform's own engine records
    /// the absolute path <see cref="PutFileAsync"/> returned and hands that back for the rest of the
    /// artifact's life — including after the destination has been pointed somewhere else, so it is
    /// taken exactly as given and never re-based. The backup module keeps no such path: its
    /// artifacts are found by the key they were stored under, which is relative to the destination
    /// and means nothing without it. Resolving a relative reference against the process's working
    /// directory — which is what returning it unchanged did — pointed a restore at the panel's own
    /// installation directory, and pointed a delete at nothing at all.
    /// </para>
    /// </summary>
    private string LocalArtifact(BackupDestination dest, string artifactRef, string what) =>
        Path.IsPathRooted(artifactRef) ? artifactRef : Confined(LocalRoot(dest), artifactRef, what);

    /// <summary>
    /// A key resolved inside the destination it belongs to, or a refusal.
    ///
    /// <para>
    /// Every key Harbora builds today is made of Guids and timestamps, so nothing has ever escaped.
    /// That is a property of the callers, though, and this is the layer that turns a key into a path
    /// on a disk — the one place that can promise a destination only ever holds what was meant for
    /// it. <see cref="PathGuard"/> resolves before it compares, which is what makes a "..", a
    /// symlinked parent and an absolute key fail the same way.
    /// </para>
    /// </summary>
    private static string Confined(string root, string key, string what)
    {
        var check = PathGuard.ResolveWithin(root, key);
        if (!check.Allowed)
            throw new InvalidOperationException(
                $"'{key}' does not name a file inside {root} ({check.Rejection}), so nothing was {what} it.");

        return check.ResolvedPath!;
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

    /// <summary>
    /// The bucket and object a reference names.
    ///
    /// <para>
    /// Two forms arrive here, exactly as they do on the local branch. A reference
    /// <see cref="PutFileAsync"/> returned carries its own bucket — recorded on the row so an
    /// artifact stays readable after the destination has been pointed at a different one — and a
    /// bare key does not, because the backup module stores no reference at all: it rebuilds the key
    /// from the repository and snapshot ids every time it reads.
    /// </para>
    /// <para>
    /// This used to split on the first slash without asking whether a scheme was there, so a key
    /// like <c>{repository}/{snapshot}.tar.gz.enc</c> read as bucket <c>{repository}</c> — a bucket
    /// named after a Guid, which exists nowhere. That is every restore, browse and delete a module
    /// repository performs, and the write probe that decides whether a new S3 repository can be
    /// created at all, since the probe deletes by the key it wrote.
    /// </para>
    /// </summary>
    public static (string Bucket, string Key) S3Location(BackupDestination dest, string artifactRef)
    {
        ArgumentNullException.ThrowIfNull(dest);

        const string scheme = "s3://";
        if (!artifactRef.StartsWith(scheme, StringComparison.Ordinal))
            return (dest.Bucket ?? "", artifactRef.TrimStart('/'));

        var withoutScheme = artifactRef[scheme.Length..];
        var slash = withoutScheme.IndexOf('/');

        // A scheme with no object after the bucket is not something that can be fetched or removed.
        // Said in words rather than left to crash on the substring, because the reference comes off
        // a row and the operator's question will be which backup it belongs to.
        if (slash <= 0 || slash == withoutScheme.Length - 1)
            throw new InvalidOperationException($"'{artifactRef}' does not name an object inside a bucket.");

        return (withoutScheme[..slash], withoutScheme[(slash + 1)..]);
    }
}
