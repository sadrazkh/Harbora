using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Apps;

/// <summary>
/// See <see cref="IAppConfigurationService"/>. Moved out of <c>AppsController</c>'s <c>AddEnv</c>,
/// <c>DeleteEnv</c>, <c>AddDomain</c>, <c>DeleteDomain</c>, <c>AddVolume</c> and <c>RemoveVolume</c>,
/// with the checks in the order those actions made them. Each mutation and its validate method share
/// one check method, so the two cannot drift; validation reads without tracking, takes no creation
/// lock, and stops before the first write.
///
/// <para>
/// Unlike the actions they came from, every mutation here is audited (only the volume ones were), and a
/// delete that finds nothing to delete says so instead of reporting a removal that did not happen.
/// </para>
/// </summary>
public sealed class AppConfigurationService(
    HarboraDbContext db,
    IQuotaService quota,
    ISecretProtector protector,
    IAuditLogger audit,
    ProjectAccessService access,
    AppAddressAssigner addresses,
    IServerEngineFactory engines,
    IConfiguration config,
    ILogger<AppConfigurationService> logger) : IAppConfigurationService
{
    // ---- environment variables ----

    public async Task<IReadOnlyList<ServiceRefusal>> ValidateSetEnvAsync(
        AppActor actor, Guid appId, IReadOnlyList<AppEnvVarInput> variables, CancellationToken ct)
    {
        var app = await ReachAsync(actor, appId, track: false, q => q, ct);
        return app is null ? [AppRefusals.AppNotReachable] : CheckVariables(variables);
    }

    public async Task<ServiceOutcome<IReadOnlyList<AppEnvVarChange>>> SetEnvAsync(
        AppActor actor, Guid appId, IReadOnlyList<AppEnvVarInput> variables, CancellationToken ct)
    {
        var app = await ReachAsync(actor, appId, track: true, q => q.Include(a => a.EnvironmentVariables), ct);
        if (app is null) return ServiceOutcome<IReadOnlyList<AppEnvVarChange>>.Refused(AppRefusals.AppNotReachable);

        var refusals = CheckVariables(variables);
        if (refusals.Count > 0) return ServiceOutcome<IReadOnlyList<AppEnvVarChange>>.Refused(refusals);

        // The same key twice in one call: the last one wins, as it would have across two form posts.
        var changes = new List<AppEnvVarChange>();
        foreach (var variable in variables.GroupBy(v => v.Key, StringComparer.Ordinal).Select(g => g.Last()))
        {
            var existing = app.EnvironmentVariables.FirstOrDefault(e => e.Key == variable.Key);
            var stored = variable.IsSecret ? protector.Protect(variable.Value ?? "") : variable.Value ?? "";
            if (existing is null)
                app.EnvironmentVariables.Add(new EnvironmentVariable
                {
                    Key = variable.Key, Value = stored, IsSecret = variable.IsSecret, AvailableAtBuild = variable.AvailableAtBuild
                });
            else
            {
                existing.Value = stored;
                existing.IsSecret = variable.IsSecret;
                existing.AvailableAtBuild = variable.AvailableAtBuild;
            }

            changes.Add(new AppEnvVarChange(variable.Key, existing is null, variable.IsSecret, variable.AvailableAtBuild));
        }

        await db.SaveChangesAsync(ct);

        // Keys and flags only. The value — a secret's above all — is exactly what an audit log must
        // never become a second copy of.
        foreach (var change in changes)
            await audit.LogAsync("app.env_set", "app", $"{app.Name}:{change.Key}", actor.ClientIp,
                metadataJson: JsonSerializer.Serialize(new
                {
                    key = change.Key, secret = change.IsSecret, availableAtBuild = change.AvailableAtBuild, created = change.Created
                }),
                workspaceId: actor.WorkspaceId, ct: ct);

        return ServiceOutcome<IReadOnlyList<AppEnvVarChange>>.Success(changes);
    }

    private static IReadOnlyList<ServiceRefusal> CheckVariables(IReadOnlyList<AppEnvVarInput>? variables)
    {
        if (variables is null || variables.Count == 0) return [AppRefusals.EnvVariablesRequired];
        return variables.Any(v => string.IsNullOrWhiteSpace(v.Key)) ? [AppRefusals.EnvKeyRequired] : [];
    }

    public async Task<IReadOnlyList<ServiceRefusal>> ValidateDeleteEnvAsync(
        AppActor actor, Guid appId, AppItemRef variable, CancellationToken ct)
    {
        var app = await ReachAsync(actor, appId, track: false, q => q, ct);
        if (app is null) return [AppRefusals.AppNotReachable];
        return await FindVariableAsync(appId, variable, track: false, ct) is null ? [AppRefusals.EnvVariableNotFound] : [];
    }

    public async Task<ServiceOutcome<string>> DeleteEnvAsync(
        AppActor actor, Guid appId, AppItemRef variable, CancellationToken ct)
    {
        var app = await ReachAsync(actor, appId, track: false, q => q, ct);
        if (app is null) return ServiceOutcome<string>.Refused(AppRefusals.AppNotReachable);

        var row = await FindVariableAsync(appId, variable, track: true, ct);
        if (row is null) return ServiceOutcome<string>.Refused(AppRefusals.EnvVariableNotFound);

        db.EnvironmentVariables.Remove(row);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("app.env_deleted", "app", $"{app.Name}:{row.Key}", actor.ClientIp,
            metadataJson: JsonSerializer.Serialize(new { key = row.Key, secret = row.IsSecret }),
            workspaceId: actor.WorkspaceId, ct: ct);

        return ServiceOutcome<string>.Success(row.Key);
    }

    private async Task<EnvironmentVariable?> FindVariableAsync(Guid appId, AppItemRef target, bool track, CancellationToken ct)
    {
        var rows = db.EnvironmentVariables.Where(e => e.AppId == appId);
        if (!track) rows = rows.AsNoTracking();

        if (target.Id is { } id) return await rows.FirstOrDefaultAsync(e => e.Id == id, ct);
        return string.IsNullOrWhiteSpace(target.Name) ? null : await rows.FirstOrDefaultAsync(e => e.Key == target.Name, ct);
    }

    // ---- domains ----

    public async Task<IReadOnlyList<ServiceRefusal>> ValidateAddDomainAsync(
        AppActor actor, Guid appId, string? host, CancellationToken ct)
    {
        var check = await CheckDomainAsync(actor, appId, host, track: false, ct);
        if (check.Refusal is { } refusal) return [refusal];

        var quotaCheck = await quota.CanAddGovernedResourcesAsync(actor.WorkspaceId, new GovernanceQuotaDelta(Domains: 1), ct);
        return quotaCheck.Allowed ? [] : [AppRefusals.Quota(quotaCheck, "Plan quota exceeded.", "سقف پلن پر شده است.")];
    }

    public async Task<ServiceOutcome<AppDomainAdded>> AddDomainAsync(
        AppActor actor, Guid appId, string? host, bool ssl, CancellationToken ct)
    {
        var check = await CheckDomainAsync(actor, appId, host, track: true, ct);
        if (check.Refusal is { } refusal) return ServiceOutcome<AppDomainAdded>.Refused(refusal);
        var app = check.App!;

        await using var quotaReservation = await quota.AcquireCreationLockAsync(actor.WorkspaceId, ct);
        var quotaCheck = await quota.CanAddGovernedResourcesAsync(actor.WorkspaceId, new GovernanceQuotaDelta(Domains: 1), ct);
        if (!quotaCheck.Allowed)
            return ServiceOutcome<AppDomainAdded>.Refused(
                AppRefusals.Quota(quotaCheck, "Plan quota exceeded.", "سقف پلن پر شده است."));

        var domain = new DomainName
        {
            Host = check.Host, SslEnabled = ssl, ForceHttps = ssl, IsPrimary = app.Domains.Count == 0
        };
        app.Domains.Add(domain);
        await db.SaveChangesAsync(ct);
        await quotaReservation.CommitAsync(ct);

        await audit.LogAsync("app.domain_added", "app", $"{app.Name}:{domain.Host}", actor.ClientIp,
            metadataJson: JsonSerializer.Serialize(new { host = domain.Host, ssl, primary = domain.IsPrimary }),
            workspaceId: actor.WorkspaceId, ct: ct);

        return ServiceOutcome<AppDomainAdded>.Success(new AppDomainAdded(domain.Id, domain.Host, ssl, domain.IsPrimary));
    }

    private sealed record DomainCheck(App? App, string Host, ServiceRefusal? Refusal);

    /// <summary>Everything but the quota, which the add asks under the creation lock.</summary>
    private async Task<DomainCheck> CheckDomainAsync(AppActor actor, Guid appId, string? requested, bool track, CancellationToken ct)
    {
        var app = await ReachAsync(actor, appId, track, q => q.Include(a => a.Domains), ct);
        if (app is null) return new DomainCheck(null, "", AppRefusals.AppNotReachable);

        var host = (requested ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host)) return new DomainCheck(app, host, AppRefusals.HostRequired);

        // Across every workspace: DNS is not multi-tenant, so a host another tenant routes is taken.
        // DomainName carries no tenant filter — see AppAddressAssigner.AssignAsync's own remark on why.
        if (await db.Domains.AnyAsync(d => d.Host == host, ct)) return new DomainCheck(app, host, AppRefusals.DomainInUse);

        // A uniqueness check is not an ownership check. The platform's own host names are not in
        // db.Domains — nothing ever inserted them — so until this guard they were free to claim, and
        // the node channel's host is the one where taking it costs more than it looks: see
        // ReservedHosts for why a second router on that SNI name turns mTLS off instead of adding a
        // route.
        if (await IsReservedHostAsync(host, ct)) return new DomainCheck(app, host, AppRefusals.ReservedHost(host, "Host"));

        return new DomainCheck(app, host, null);
    }

    /// <summary>
    /// Whether a typed host is one the platform answers on itself — see <see cref="ReservedHosts"/> for
    /// what claiming the node channel's host silently costs — or a status page's own reserved prefix
    /// under the platform's root domain, the same rule <see cref="AppAddress.Decide"/> already applies to
    /// a new app's derived/typed address. Adding a domain is the one path that attaches a host to an
    /// existing app without going through <c>AppAddressAssigner.AssignAsync</c>, so it is the one path
    /// that has to ask both questions itself.
    ///
    /// <para>
    /// Read from configuration on each call rather than cached or mirrored into the database: these are
    /// per-install values (<c>PANEL_DOMAIN</c>, <c>NodeAgent__PublicUrl</c>,
    /// <c>Storage__S3__PublicEndpoint</c> in <c>deploy/.env</c>) that <c>harbora set-domain</c> can
    /// change under a running panel, and a second copy is a second thing to be wrong.
    /// </para>
    /// </summary>
    private async Task<bool> IsReservedHostAsync(string host, CancellationToken ct)
    {
        if (ReservedHosts.IsReserved(host, ReservedHosts.ForPlatform(
                config["PANEL_DOMAIN"], config["NodeAgent:PublicUrl"], config["Storage:S3:PublicEndpoint"])))
            return true;

        return ReservedHosts.IsReservedPrefix(host, await addresses.RootDomainAsync(ct));
    }

    public async Task<IReadOnlyList<ServiceRefusal>> ValidateRemoveDomainAsync(
        AppActor actor, Guid appId, AppItemRef domain, CancellationToken ct)
    {
        var app = await ReachAsync(actor, appId, track: false, q => q, ct);
        if (app is null) return [AppRefusals.AppNotReachable];
        return await FindDomainAsync(appId, domain, track: false, ct) is null ? [AppRefusals.DomainNotFound] : [];
    }

    public async Task<ServiceOutcome<AppDomainRemoved>> RemoveDomainAsync(
        AppActor actor, Guid appId, AppItemRef domain, CancellationToken ct)
    {
        var app = await ReachAsync(actor, appId, track: false, q => q, ct);
        if (app is null) return ServiceOutcome<AppDomainRemoved>.Refused(AppRefusals.AppNotReachable);

        var row = await FindDomainAsync(appId, domain, track: true, ct);
        if (row is null) return ServiceOutcome<AppDomainRemoved>.Refused(AppRefusals.DomainNotFound);

        db.Domains.Remove(row);
        var routes = await db.Routes.Where(r => r.AppId == appId && r.Host == row.Host).ToListAsync(ct);
        db.Routes.RemoveRange(routes);

        // Deleting the primary must not leave a live custom domain marked nobody's primary: Overview,
        // the applications list and the backfill's "addressless" filter all key off IsPrimary, and an
        // app with a domain that is nobody's primary reads — wrongly — as having none at all.
        string? promoted = null;
        var survivorPrimaryAlready = await db.Domains
            .AnyAsync(d => d.AppId == appId && d.Id != row.Id && d.IsPrimary, ct);
        if (!survivorPrimaryAlready)
        {
            var survivor = await db.Domains.Where(d => d.AppId == appId && d.Id != row.Id)
                .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).FirstOrDefaultAsync(ct);
            if (survivor is not null)
            {
                survivor.IsPrimary = true;
                promoted = survivor.Host;
            }
        }

        await db.SaveChangesAsync(ct);

        await audit.LogAsync("app.domain_removed", "app", $"{app.Name}:{row.Host}", actor.ClientIp,
            metadataJson: JsonSerializer.Serialize(new { host = row.Host, routesRemoved = routes.Count, newPrimary = promoted }),
            workspaceId: actor.WorkspaceId, ct: ct);

        return ServiceOutcome<AppDomainRemoved>.Success(new AppDomainRemoved(row.Host, promoted));
    }

    private async Task<DomainName?> FindDomainAsync(Guid appId, AppItemRef target, bool track, CancellationToken ct)
    {
        var rows = db.Domains.Where(d => d.AppId == appId);
        if (!track) rows = rows.AsNoTracking();

        if (target.Id is { } id) return await rows.FirstOrDefaultAsync(d => d.Id == id, ct);
        var host = (target.Name ?? "").Trim().ToLowerInvariant();
        return host.Length == 0 ? null : await rows.FirstOrDefaultAsync(d => d.Host == host, ct);
    }

    // ---- volumes ----

    public async Task<IReadOnlyList<ServiceRefusal>> ValidateAddVolumeAsync(
        AppActor actor, Guid appId, string? mountPath, CancellationToken ct)
    {
        var check = await CheckVolumeAsync(actor, appId, mountPath, track: false, ct);
        if (check.Refusal is { } refusal) return [refusal];

        var quotaCheck = await quota.CanAddGovernedResourcesAsync(actor.WorkspaceId, new GovernanceQuotaDelta(Volumes: 1), ct);
        return quotaCheck.Allowed ? [] : [AppRefusals.Quota(quotaCheck, "Plan quota exceeded.", "سقف پلن پر شده است.")];
    }

    /// <remarks>
    /// Volumes could only arrive from a template, so an application built from an image or a repository
    /// had nowhere to put anything that survives a deploy — and the Data browser, which only appears
    /// when there is a volume, was unreachable for most of the platform.
    /// </remarks>
    public async Task<ServiceOutcome<AppVolumeAdded>> AddVolumeAsync(
        AppActor actor, Guid appId, string? mountPath, CancellationToken ct)
    {
        var check = await CheckVolumeAsync(actor, appId, mountPath, track: true, ct);
        if (check.Refusal is { } refusal) return ServiceOutcome<AppVolumeAdded>.Refused(refusal);
        var app = check.App!;

        await using var quotaReservation = await quota.AcquireCreationLockAsync(actor.WorkspaceId, ct);
        var quotaCheck = await quota.CanAddGovernedResourcesAsync(actor.WorkspaceId, new GovernanceQuotaDelta(Volumes: 1), ct);
        if (!quotaCheck.Allowed)
            return ServiceOutcome<AppVolumeAdded>.Refused(
                AppRefusals.Quota(quotaCheck, "Plan quota exceeded.", "سقف پلن پر شده است."));

        var volume = new Volume { Name = MountPath.VolumeNameFor(app.Slug, check.MountPath), MountPath = check.MountPath };
        app.Volumes.Add(volume);
        await db.SaveChangesAsync(ct);
        await quotaReservation.CommitAsync(ct);
        await audit.LogAsync("app.volume_added", "app", $"{app.Name}:{check.MountPath}", actor.ClientIp,
            workspaceId: actor.WorkspaceId, ct: ct);

        return ServiceOutcome<AppVolumeAdded>.Success(new AppVolumeAdded(volume.Id, volume.MountPath, volume.Name));
    }

    private sealed record VolumeCheck(App? App, string MountPath, ServiceRefusal? Refusal);

    private async Task<VolumeCheck> CheckVolumeAsync(AppActor actor, Guid appId, string? mountPath, bool track, CancellationToken ct)
    {
        var app = await ReachAsync(actor, appId, track, q => q.Include(a => a.Volumes), ct);
        if (app is null) return new VolumeCheck(null, "", AppRefusals.AppNotReachable);

        var refusal = MountPath.Check(mountPath);
        if (refusal != MountPathRefusal.None) return new VolumeCheck(app, "", AppRefusals.Mount(refusal));

        var normalised = MountPath.Normalise(mountPath)!;

        // Compared on the normalised form, so "/data" and "/data/" cannot both be added and then collide
        // at deploy time, where the message is about a duplicate bind rather than about what somebody
        // typed.
        if (app.Volumes.Any(v => v.MountPath == normalised))
            return new VolumeCheck(app, normalised, AppRefusals.AlreadyMounted(normalised));

        return new VolumeCheck(app, normalised, null);
    }

    public async Task<IReadOnlyList<ServiceRefusal>> ValidateRemoveVolumeAsync(
        AppActor actor, Guid appId, AppItemRef volume, bool deleteData, CancellationToken ct)
    {
        var check = await CheckRemoveVolumeAsync(actor, appId, volume, deleteData, track: false, ct);
        return check.Refusal is { } refusal ? [refusal] : [];
    }

    public async Task<ServiceOutcome<AppVolumeRemoved>> RemoveVolumeAsync(
        AppActor actor, Guid appId, AppItemRef volume, bool deleteData, CancellationToken ct)
    {
        var check = await CheckRemoveVolumeAsync(actor, appId, volume, deleteData, track: true, ct);
        if (check.Refusal is { } refusal) return ServiceOutcome<AppVolumeRemoved>.Refused(refusal);
        var (app, row) = (check.App!, check.Volume!);

        var name = row.Name;
        var path = row.MountPath;
        app.Volumes.Remove(row);
        await db.SaveChangesAsync(ct);

        // Two different acts, and the second is not undoable. Forgetting the mount leaves the data on
        // the host where it can be mounted again; deleting it is gone. Whether it actually IS gone is
        // not assumed from "deleteData was true" — the docker call is what decides that, and the audit
        // log and the result both follow what actually happened rather than what was asked for. Before
        // this, a Docker removal that threw was logged and then reported to the person as "Deleted"
        // anyway — the exact defect class this whole feature exists to close.
        var dataActuallyDeleted = false;
        if (deleteData)
        {
            var docker = await engines.ResolveAsync(app.ServerId, ct);
            try { await docker.RemoveVolumeAsync(name, ct); dataActuallyDeleted = true; }
            catch (Exception e) { logger.LogWarning(e, "Volume {Volume} was unmounted but not deleted.", name); }
        }

        await audit.LogAsync(
            dataActuallyDeleted ? "app.volume_deleted" : "app.volume_detached", "app",
            $"{app.Name}:{path}", actor.ClientIp, workspaceId: actor.WorkspaceId, ct: ct);

        return ServiceOutcome<AppVolumeRemoved>.Success(new AppVolumeRemoved(path, name, deleteData, dataActuallyDeleted));
    }

    private sealed record VolumeRemovalCheck(App? App, Volume? Volume, ServiceRefusal? Refusal);

    private async Task<VolumeRemovalCheck> CheckRemoveVolumeAsync(
        AppActor actor, Guid appId, AppItemRef target, bool deleteData, bool track, CancellationToken ct)
    {
        var app = await ReachAsync(actor, appId, track, q => q.Include(a => a.Volumes), ct);
        if (app is null) return new VolumeRemovalCheck(null, null, AppRefusals.AppNotReachable);

        var volume = target.Id is { } id
            ? app.Volumes.FirstOrDefault(v => v.Id == id)
            : MountPath.Normalise(target.Name) is { } path
                ? app.Volumes.FirstOrDefault(v => v.MountPath == path)
                : null;
        if (volume is null) return new VolumeRemovalCheck(app, null, AppRefusals.VolumeNotFound);

        // HARBORA-0033. Checked before the row is touched at all — a refusal here must leave the volume
        // mounted, tracked and exactly as it was, not removed-from-the-app-but-not-deleted.
        if (deleteData)
        {
            try { VolumeProtection.GuardAgainstDestroying([volume]); }
            catch (VolumeProtectedException ex) { return new VolumeRemovalCheck(app, volume, AppRefusals.VolumeProtected(ex)); }
        }

        return new VolumeRemovalCheck(app, volume, null);
    }

    // ---- shared ----

    /// <summary>
    /// The app, when the caller may change it with <see cref="Capabilities.AppsEnv"/>; null for "not
    /// yours" and "not there" alike. Asked here rather than trusted from the caller, so a caller that is
    /// not a controller cannot skip it. The workspace predicate is the request's own, on top of the
    /// tenant filter, the same pairing every app action has always used.
    /// </summary>
    private async Task<App?> ReachAsync(
        AppActor actor, Guid appId, bool track, Func<IQueryable<App>, IQueryable<App>> include, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return null;

        var apps = include(db.Apps);
        if (!track) apps = apps.AsNoTracking();
        return await apps.FirstOrDefaultAsync(a => a.Id == appId && a.WorkspaceId == actor.WorkspaceId, ct);
    }
}
