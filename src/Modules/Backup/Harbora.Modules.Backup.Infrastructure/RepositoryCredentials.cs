using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Decrypts a repository's credentials at the moment they are needed.
///
/// <para>
/// A separate service so that exactly one place in the module can turn ciphertext into a usable
/// secret, and so nothing else is tempted to hold one. Plaintext returned here lives on the stack
/// for the duration of an engine call and is never stored, logged or returned by an API.
/// </para>
/// </summary>
public interface IRepositoryCredentialReader
{
    /// <summary>The repository password, or null if it cannot be decrypted.</summary>
    Task<string?> GetPasswordAsync(Guid repositoryId, CancellationToken cancellationToken);

    /// <summary>Storage credentials, or null when the repository needs none.</summary>
    Task<RepositoryCredentials?> GetCredentialsAsync(Guid repositoryId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class RepositoryCredentialReader(
    HarboraDbContext db,
    ISecretProtector protector,
    ILogger<RepositoryCredentialReader> logger) : IRepositoryCredentialReader
{
    public async Task<string?> GetPasswordAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters: called from background jobs that run unscoped AND from request paths
        // that are already scoped. The caller has established the repository belongs to the tenant
        // before asking for its password; adding a filter here would make the background path
        // silently return null instead.
        var encrypted = await db.BackupRepositories.IgnoreQueryFilters()
            .Where(r => r.Id == repositoryId)
            .Select(r => r.EncryptedPassword)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrEmpty(encrypted)) return null;

        try
        {
            return protector.Unprotect(encrypted);
        }
        catch (Exception ex)
        {
            // Logged without the ciphertext. The cause is almost always a changed master key, and
            // saying so is more useful than the exception, which names neither.
            logger.LogError(ex,
                "The password for repository {RepositoryId} could not be decrypted. The platform " +
                "master key has most likely changed since the repository was created.", repositoryId);
            return null;
        }
    }

    public async Task<RepositoryCredentials?> GetCredentialsAsync(
        Guid repositoryId, CancellationToken cancellationToken)
    {
        var encrypted = await db.BackupRepositories.IgnoreQueryFilters()
            .Where(r => r.Id == repositoryId)
            .Select(r => r.EncryptedCredentials)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrEmpty(encrypted)) return null;

        try
        {
            return JsonSerializer.Deserialize<RepositoryCredentials>(protector.Unprotect(encrypted));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            logger.LogError(ex,
                "The stored credentials for repository {RepositoryId} could not be read.", repositoryId);
            return null;
        }
    }
}

/// <summary>
/// Turns a <see cref="BackupRepository"/> into the <see cref="BackupDestination"/> the platform's
/// existing storage layer understands.
///
/// <para>
/// The adapter that lets the native engine reuse <c>IBackupStorage</c> — S3 upload and local
/// placement, already written and already exercised — instead of this module growing a second
/// implementation of each.
/// </para>
/// </summary>
public interface IRepositoryDestinationFactory
{
    BackupDestination ToDestination(BackupRepository repository, RepositoryCredentials? credentials);
}

/// <inheritdoc />
public sealed class RepositoryDestinationFactory(ISecretProtector protector) : IRepositoryDestinationFactory
{
    /// <summary>
    /// The returned destination is transient and must never be attached to the change tracker —
    /// saving it would write a duplicate row into the destinations table.
    ///
    /// <para>
    /// Credential fields are re-<b>protected</b> rather than passed in the clear, because
    /// <c>BackupStorage</c> calls <c>Unprotect</c> on them itself. Handing it plaintext would fail
    /// decryption at the point of upload — a confusing error a long way from its cause.
    /// </para>
    /// </summary>
    public BackupDestination ToDestination(BackupRepository repository, RepositoryCredentials? credentials)
    {
        ArgumentNullException.ThrowIfNull(repository);

        return repository.Type switch
        {
            BackupRepositoryType.Local => new BackupDestination
            {
                Id = repository.Id,
                WorkspaceId = repository.WorkspaceId,
                Name = repository.Name,
                Type = BackupDestinationType.Local,
                LocalPath = repository.BasePath
            },

            // Everything in the S3 family speaks the same protocol; only the endpoint differs.
            BackupRepositoryType.S3Compatible or BackupRepositoryType.AmazonS3
                or BackupRepositoryType.MinIO or BackupRepositoryType.BackblazeB2 => new BackupDestination
                {
                    Id = repository.Id,
                    WorkspaceId = repository.WorkspaceId,
                    Name = repository.Name,
                    Type = BackupDestinationType.S3,
                    Endpoint = repository.Endpoint,
                    Bucket = repository.Bucket,
                    Region = repository.Region,
                    AccessKey = credentials?.AccessKeyId,
                    EncryptedSecretKey = Protect(credentials?.SecretAccessKey)
                },

            // SFTP is deliberately not wired in this branch. The existing destination type requires
            // a pinned host key — without one, Harbora cannot tell the real server from anything
            // else answering on that address, and would hand it the backup and the password. Adding
            // that field to the repository model is follow-up work, and a repository that silently
            // skipped the check would be worse than one that is not offered yet.
            BackupRepositoryType.Sftp => throw new NotSupportedException(
                "SFTP repositories are not available yet. Use an existing SFTP backup destination, " +
                "or an S3-compatible repository."),

            _ => throw new NotSupportedException(
                $"{repository.Type} repositories are not supported by the built-in engine.")
        };
    }

    private string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null : protector.Protect(plaintext);
}
