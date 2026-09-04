using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;

namespace Harbora.Infrastructure.Apps;

/// <summary>
/// Assembles a merge's inputs from an already-loaded <see cref="App"/> — the attached groups,
/// storage buckets, email providers and managed services, each turned into the shape
/// <see cref="ConfigGroupMerge.Merge"/> expects — and calls it.
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
/// <c>EmailProvider</c>), <c>ManagedServices</c> (with each <c>ManagedService</c> and <c>Database</c>)
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
            attachedErrorTracking);
    }
}
