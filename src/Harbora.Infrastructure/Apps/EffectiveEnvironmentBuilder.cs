using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Apps;

/// <summary>
/// Assembles a merge's inputs from an already-loaded <see cref="App"/> — the attached groups,
/// storage buckets, email providers, managed services and their read replicas, each turned into the
/// shape <see cref="ConfigGroupMerge.Merge"/> expects — and calls it.
///
/// <para>
/// Before this existed, <c>DeploymentPipeline.BuildEnv</c> (what a container actually receives) and
/// <c>AppsController.Details</c> (what the env page renders) each built this assembly separately,
/// kept identical only by a matching comment at each call site ("same shape as X above") rather than
/// by shared code — the two could drift the moment one was touched without the other. This is now
/// the one place: <c>BuildEnv</c>, the env page, and <c>ApiV1Controller.Env</c> (the CLI's
/// <c>harbora env pull</c>) all call it, so a value the CLI writes to <c>.env.local</c> can never be
/// one a deploy would not actually inject — the same guarantee <see cref="ConfigGroupMerge"/>'s own
/// doc already claims for the page and the container, extended to the third caller.
/// </para>
///
/// <para>
/// <paramref name="app"/>'s <c>ConfigGroups</c> (with each <c>ConfigGroup.Entries</c>),
/// <c>StorageBuckets</c> (with each <c>StorageBucket</c>), <c>EmailProviders</c> (with each
/// <c>EmailProvider</c>), <c>ManagedServices</c> (with each <c>ManagedService</c>, that
/// <c>ManagedService</c>'s own <c>Replicas</c> — 3.2, round-2 market-gaps plan — and <c>Database</c>)
/// and <c>EnvironmentVariables</c> must already be <c>Include</c>d — this does no loading of its own.
/// </para>
///
/// <para>
/// Returns ciphertext for every <c>IsSecret</c> entry, exactly as <see cref="ConfigGroupMerge.Merge"/>
/// does — decryption is left to the caller: <c>BuildEnv</c> decrypts because the container needs
/// plaintext, the env page never does because it only ever masks a secret, and the CLI endpoint
/// decrypts because the developer needs the real value to run locally.
/// </para>
/// </summary>
public static class EffectiveEnvironmentBuilder
{
    public static IReadOnlyList<EffectiveEnvironmentEntry> Compute(
        App app, ISecretProtector protector, string storageCustomerEndpoint)
    {
        var attachedBuckets = app.StorageBuckets
            .Where(sb => sb.StorageBucket is not null)
            .Select(sb => new AttachedBucketEnv(
                sb.AttachOrder, sb.StorageBucketId, sb.StorageBucket!.Name,
                // Ciphertext stays ciphertext here — decrypted exactly once, downstream, wherever
                // every other IsSecret entry in the merge is.
                Harbora.Domain.Storage.BucketEnvKeys.EntriesFor(
                        sb.StorageBucket!, storageCustomerEndpoint, sb.StorageBucket!.EncryptedSecretKey)
                    .Select(e => new BucketEnvEntry(e.Key, e.Value, e.IsSecret)).ToList()));

        var attachedEmailProviders = app.EmailProviders
            .Where(ep => ep.EmailProvider is not null)
            .Select(ep => new AttachedEmailProviderEnv(
                ep.AttachOrder, ep.EmailProviderId, ep.EmailProvider!.Name,
                Harbora.Domain.Email.EmailProviderEnvKeys.EntriesFor(
                        ep.EmailProvider!, ep.EmailProvider!.EncryptedPassword)
                    .Select(e => new EmailProviderEnvEntry(e.Key, e.Value, e.IsSecret)).ToList()));

        var attachedDatabases = app.ManagedServices
            .Where(ms => ms.ManagedService is not null)
            .Select(ms => new AttachedDatabaseEnv(
                ms.AttachOrder, ms.ManagedServiceId, ms.ManagedService!.Name,
                Harbora.Infrastructure.Services.ManagedServiceAttachEnv.EntriesFor(ms, protector)
                    .Select(e => new DatabaseEnvEntry(e.Key, e.Value, e.IsSecret)).ToList()));

        // 3.2 (round-2 market-gaps plan): a NEW attachment kind, not a change bolted onto the database
        // one above — see AttachedReplicaEnv's own doc for why it rides along with the primary's
        // AttachOrder/identity rather than getting its own. Only ever a Running replica: a replica
        // still seeding from pg_basebackup has no data an app could safely read yet, and surfacing its
        // REPLICA_URL before that would hand out a connection string to a container nothing is
        // listening on. The oldest Running replica is picked when more than one exists, for the same
        // "earliest wins, deterministically" reason CreatedAt already orders other lists on this
        // platform — a replica an app reads from must not change identity from one deploy to the next
        // just because a newer sibling was created in between.
        var attachedReplicas = app.ManagedServices
            .Where(ms => ms.ManagedService is not null)
            .Select(ms => new
            {
                Attachment = ms,
                Replica = ms.ManagedService!.Replicas
                    .Where(r => r.Status == Domain.Common.ServiceStatus.Running)
                    .OrderBy(r => r.CreatedAt)
                    .FirstOrDefault()
            })
            .Where(x => x.Replica is not null)
            .Select(x => new AttachedReplicaEnv(
                x.Attachment.AttachOrder, x.Attachment.ManagedServiceId, x.Attachment.ManagedService!.Name,
                Harbora.Infrastructure.Services.ReplicaAttachEnv.EntriesFor(
                        Harbora.Infrastructure.Services.AttachedDatabaseCreds.Resolve(
                            x.Attachment.ManagedService!, x.Attachment.Database, protector),
                        x.Replica!, x.Attachment.Alias, protector)
                    .Select(e => new ReplicaEnvEntry(e.Key, e.Value, e.IsSecret)).ToList()));

        // 1.8 landed on master while this extraction was being written, and the two conflicted in
        // exactly the way git cannot see: master added a fifth attachment kind inline in BuildEnv,
        // this branch replaced that whole block with a call to here. Taking either side alone
        // silently loses one of the two features — the error-tracking DSN would just stop being
        // injected, with nothing anywhere to say so. Both are kept.
        var attachedErrorTracking = app.ErrorTrackingProviders
            .Where(et => et.ErrorTrackingProvider is not null)
            .Select(et => new AttachedErrorTrackingEnv(
                et.AttachOrder, et.ErrorTrackingProviderId, et.ErrorTrackingProvider!.Name,
                Harbora.Domain.ErrorTracking.ErrorTrackingEnvKeys.EntriesFor(
                        et.ErrorTrackingProvider!.EncryptedDsn)
                    .Select(e => new ErrorTrackingEnvEntry(e.Key, e.Value, e.IsSecret)).ToList()));

        return ConfigGroupMerge.Merge(
            app.EnvironmentVariables,
            app.ConfigGroups.Select(cg => new AttachedGroupEntries(
                cg.AttachOrder, cg.ConfigGroupId, cg.ConfigGroup?.Name ?? "", cg.ConfigGroup?.Entries.ToList() ?? [])),
            attachedBuckets,
            attachedEmailProviders,
            attachedDatabases,
            attachedErrorTracking,
            attachedReplicas);
    }

    /// <summary>
    /// What a container is handed: <see cref="Compute"/>, with every secret decrypted. A value this
    /// panel cannot decrypt (an old key, a damaged row) becomes empty rather than failing the whole
    /// environment over one entry, which is what <c>DeploymentPipeline.BuildEnv</c> has always done.
    ///
    /// <para>
    /// Shared so that a deployed container and a scheduled run cannot be handed two different
    /// environments. <c>CronJobRunner</c> used to build its own from the app's typed variables alone,
    /// so a job attached to a database never received <c>DATABASE_URL</c> while its own page listed
    /// it — and a backup script that cannot find its database looks like a script with a bug in it.
    /// </para>
    /// </summary>
    public static Dictionary<string, string> Decrypted(
        App app, ISecretProtector protector, string storageCustomerEndpoint) =>
        Compute(app, protector, storageCustomerEndpoint)
            .ToDictionary(e => e.Key, e => e.IsSecret ? SafeUnprotect(protector, e.Value) : e.Value);

    /// <summary>
    /// Everything <see cref="Compute"/> reads, loaded with the app: the attachments of every kind and
    /// the rows behind them, to the depth each one needs (a database's logical database and its read
    /// replicas, a group's entries).
    ///
    /// <para>
    /// For a caller that has nothing else to load, so it cannot miss one. The scheduled-job runner
    /// reaches an app two ways, the scheduler loading it and the queue loading it by id, and two
    /// hand-written lists of Includes are how one of them came to give a job less than the other —
    /// or than a deployment. An attachment kind added to <see cref="Compute"/> belongs here as well.
    /// </para>
    /// </summary>
    public static IQueryable<App> IncludeEnvironmentSources(this IQueryable<App> apps) =>
        apps
            .Include(a => a.EnvironmentVariables)
            .Include(a => a.ConfigGroups).ThenInclude(cg => cg.ConfigGroup!).ThenInclude(g => g.Entries)
            .Include(a => a.StorageBuckets).ThenInclude(sb => sb.StorageBucket)
            .Include(a => a.EmailProviders).ThenInclude(ep => ep.EmailProvider)
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.ManagedService)
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.Database)
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.ManagedService!).ThenInclude(m => m.Replicas)
            .Include(a => a.ErrorTrackingProviders).ThenInclude(et => et.ErrorTrackingProvider);

    private static string SafeUnprotect(ISecretProtector protector, string ciphertext)
    {
        try { return protector.Unprotect(ciphertext); }
        catch { return string.Empty; }
    }
}
