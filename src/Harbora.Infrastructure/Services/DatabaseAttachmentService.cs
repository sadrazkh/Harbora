using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Services;

/// <summary>A database now attached to an app. Its variables reach the app on that app's next deploy.</summary>
/// <param name="DatabaseId">The logical database the attachment points at; null for an engine with none.</param>
/// <param name="Alias">The prefix the app reaches it under (<c>{Alias}_DATABASE_URL</c> and friends).</param>
public sealed record DatabaseAttachResult(
    Guid ServiceId, string ServiceName, Guid AppId, string AppName, Guid? DatabaseId, string Alias);

/// <summary>An attachment that is gone. The running container keeps the old variables until its next deploy.</summary>
public sealed record DatabaseDetachResult(Guid ServiceId, Guid AppId, Guid? DatabaseId, string? Alias);

/// <summary>
/// Attaching a managed database to an app, and detaching it — moved out of
/// <c>DatabasesController.Attach</c>/<c>Detach</c> in round six (phase 0b) so the in-panel agent goes
/// through the same checks the panel does. The access checks moved in with the logic: a caller that is
/// not a controller cannot reach the join table without them.
/// </summary>
public interface IDatabaseAttachmentService
{
    /// <summary>The refusals <see cref="AttachAsync"/> would give right now; changes nothing.</summary>
    Task<IReadOnlyList<ServiceRefusal>> ValidateAttachAsync(
        Guid serviceId, Guid appId, string? alias, Guid? databaseId, CancellationToken ct);

    /// <summary>
    /// Re-checks, then writes the <see cref="AppManagedService"/> row (marked unpublished) and audits
    /// <c>database.attached</c>.
    /// </summary>
    /// <param name="alias">Null derives one from the database's name; a taken alias gets a suffix.</param>
    /// <param name="databaseId">Null uses the instance's own default logical database, when it has one.</param>
    Task<ServiceOutcome<DatabaseAttachResult>> AttachAsync(
        Guid serviceId, Guid appId, string? alias, Guid? databaseId, string? ipAddress, CancellationToken ct);

    /// <summary>The refusals <see cref="DetachAsync"/> would give right now; changes nothing.</summary>
    Task<IReadOnlyList<ServiceRefusal>> ValidateDetachAsync(
        Guid serviceId, Guid appId, Guid? databaseId, CancellationToken ct);

    /// <summary>Re-checks, then removes the join row and audits <c>database.detached</c>.</summary>
    /// <param name="databaseId">Disambiguates an app attached to two logical databases on one instance;
    /// null takes the one attachment the app has there.</param>
    Task<ServiceOutcome<DatabaseDetachResult>> DetachAsync(
        Guid serviceId, Guid appId, Guid? databaseId, string? ipAddress, CancellationToken ct);
}

/// <inheritdoc />
public sealed class DatabaseAttachmentService(
    HarboraDbContext db,
    ProjectAccessService access,
    IAuditLogger audit,
    ICurrentUser currentUser) : IDatabaseAttachmentService
{
    public async Task<IReadOnlyList<ServiceRefusal>> ValidateAttachAsync(
        Guid serviceId, Guid appId, string? alias, Guid? databaseId, CancellationToken ct) =>
        (await PlanAttachAsync(serviceId, appId, alias, databaseId, ct)).Refusals;

    public async Task<ServiceOutcome<DatabaseAttachResult>> AttachAsync(
        Guid serviceId, Guid appId, string? alias, Guid? databaseId, string? ipAddress, CancellationToken ct)
    {
        var (plan, refusals) = await PlanAttachAsync(serviceId, appId, alias, databaseId, ct);
        if (plan is null) return ServiceOutcome<DatabaseAttachResult>.Refused(refusals);

        var maxOrder = await db.AppManagedServices
            .Where(x => x.AppId == appId).Select(x => (int?)x.AttachOrder).MaxAsync(ct) ?? 0;

        db.AppManagedServices.Add(new AppManagedService
        {
            AppId = appId, ManagedServiceId = serviceId, ManagedServiceDatabaseId = plan.DatabaseId,
            Alias = plan.Alias, AttachOrder = maxOrder + 1, HasUnpublishedChanges = true
        });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("database.attached", "service", $"{serviceId}:{appId}", ipAddress,
            workspaceId: plan.Service.WorkspaceId, ct: ct);

        return ServiceOutcome<DatabaseAttachResult>.Success(new DatabaseAttachResult(
            serviceId, plan.Service.Name, appId, plan.App.Name, plan.DatabaseId, plan.Alias));
    }

    public async Task<IReadOnlyList<ServiceRefusal>> ValidateDetachAsync(
        Guid serviceId, Guid appId, Guid? databaseId, CancellationToken ct)
    {
        if (await CheckAccessAsync(serviceId, appId, ct) is { } denied) return [denied];

        return await AttachmentQuery(serviceId, appId, databaseId).AnyAsync(ct) ? [] : [NotAttached];
    }

    public async Task<ServiceOutcome<DatabaseDetachResult>> DetachAsync(
        Guid serviceId, Guid appId, Guid? databaseId, string? ipAddress, CancellationToken ct)
    {
        if (await CheckAccessAsync(serviceId, appId, ct) is { } denied)
            return ServiceOutcome<DatabaseDetachResult>.Refused(denied);

        var join = await AttachmentQuery(serviceId, appId, databaseId).FirstOrDefaultAsync(ct);
        if (join is null) return ServiceOutcome<DatabaseDetachResult>.Refused(NotAttached);

        db.AppManagedServices.Remove(join);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("database.detached", "service", $"{serviceId}:{appId}", ipAddress,
            workspaceId: currentUser.WorkspaceId, ct: ct);

        return ServiceOutcome<DatabaseDetachResult>.Success(
            new DatabaseDetachResult(serviceId, appId, join.ManagedServiceDatabaseId, join.Alias));
    }

    private static readonly ServiceRefusal NotAttached = new(
        "This app is not attached to this database.", "این اپ به این دیتابیس وصل نیست.");

    private sealed record AttachPlan(ManagedService Service, App App, Guid? DatabaseId, string Alias);

    /// <summary>
    /// Both halves of the question the panel always asked: may this person manage the database, and
    /// may they change this app's environment. Database first, as the panel always asked it.
    /// </summary>
    private async Task<ServiceRefusal?> CheckAccessAsync(Guid serviceId, Guid appId, CancellationToken ct)
    {
        if (!await access.CanTouchServiceAsync(serviceId, Capabilities.DatabasesManage, ct))
            return DatabaseRefusals.ServiceNotFound;
        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct))
            return DatabaseRefusals.AppNotFound;
        return null;
    }

    /// <summary>
    /// D1 (2026-08-25 shared-databases plan): naming which logical database disambiguates an app
    /// attached to two of them on the same instance. A caller that omits it gets the pre-D1 behaviour
    /// verbatim — the one attachment this app has on this instance, whichever logical database it
    /// happens to point at.
    /// </summary>
    private IQueryable<AppManagedService> AttachmentQuery(Guid serviceId, Guid appId, Guid? databaseId)
    {
        var query = db.AppManagedServices.Where(x => x.AppId == appId && x.ManagedServiceId == serviceId);
        if (databaseId is { } requested) query = query.Where(x => x.ManagedServiceDatabaseId == requested);
        return query;
    }

    private async Task<(AttachPlan? Plan, IReadOnlyList<ServiceRefusal> Refusals)> PlanAttachAsync(
        Guid serviceId, Guid appId, string? alias, Guid? databaseId, CancellationToken ct)
    {
        if (await CheckAccessAsync(serviceId, appId, ct) is { } denied) return (null, [denied]);

        var app = await db.Apps.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == appId && a.WorkspaceId == currentUser.WorkspaceId, ct);
        if (app is null) return (null, [DatabaseRefusals.AppNotFound]);

        var service = await db.ManagedServices.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (service is null) return (null, [DatabaseRefusals.ServiceNotFound]);

        // 3.2 (round-2 market-gaps plan): a read replica is never attached to an app on its own — that
        // would hand out a plain, unmarked DATABASE_URL for a connection that must never take a
        // write, exactly the ambiguity the plan requires this feature to refuse. A replica surfaces
        // automatically, as REPLICA_URL, the moment an app attaches its PRIMARY instead (see
        // ReplicaAttachEnv).
        if (service.PrimaryManagedServiceId is not null)
        {
            return (null, [new ServiceRefusal(
                $"'{service.Name}' is a read-only replica and cannot be attached to an app on its " +
                "own. Attach its primary instead — REPLICA_URL becomes available to the app " +
                "automatically.",
                $"«{service.Name}» یک رپلیکای فقط-خواندنی است و مستقیماً به اپی وصل نمی‌شود. " +
                "دیتابیس اصلی آن را وصل کنید — REPLICA_URL به‌صورت خودکار در دسترس اپ قرار می‌گیرد.",
                DatabaseRefusalFields.Service)]);
        }

        // An environment is a private network, so it is also the wiring boundary. Without this the
        // attach succeeded and wrote a hostname resolvable only on the other network — the service
        // then started, looked healthy, and could not reach its database.
        var verdict = NetworkWiring.CanAttach(app.EnvironmentId, service.EnvironmentId);
        if (!verdict.Allowed)
        {
            return (null, [new ServiceRefusal(
                verdict.Reason ?? "The app and the database are on different private networks.",
                "این دو در محیط‌های متفاوتی هستند و هر محیط شبکهٔ خصوصی جداگانه‌ای است؛ " +
                "اپ نام میزبانی می‌گیرد که نمی‌تواند آن را پیدا کند.",
                DatabaseRefusalFields.Environment)]);
        }

        // D1 (2026-08-25 shared-databases plan): which logical database this attachment actually
        // points at. A caller that names one gets exactly that database (refused if it does not
        // belong to this instance); a caller that does not gets the instance's own default database
        // when it has one, which is what makes an attachment made today behave exactly as it always
        // did. An instance with no logical database at all (Redis/RabbitMQ/NATS/Meilisearch, or a
        // Postgres/MySQL/MariaDB row a migration has not reached) resolves to null, the same fallback
        // every such attachment has always used.
        Guid? resolvedDatabaseId;
        if (databaseId is { } requestedDatabaseId)
        {
            var requested = await db.ManagedServiceDatabases.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == requestedDatabaseId && d.ManagedServiceId == serviceId, ct);
            if (requested is null)
            {
                return (null, [new ServiceRefusal(
                    "That logical database does not belong to this database instance.",
                    "این پایگاه‌دادهٔ منطقی متعلق به این نمونهٔ دیتابیس نیست.",
                    DatabaseRefusalFields.Database)]);
            }
            resolvedDatabaseId = requested.Id;
        }
        else
        {
            resolvedDatabaseId = await db.ManagedServiceDatabases.AsNoTracking()
                .Where(d => d.ManagedServiceId == serviceId && d.IsDefault)
                .Select(d => (Guid?)d.Id)
                .FirstOrDefaultAsync(ct);
        }

        // The same database attached to the same app twice is refused per logical database rather
        // than per instance, so two different logical databases on the same instance may both be
        // attached to one app (see the two partial indexes on AppManagedService in HarboraDbContext
        // for the constraint this mirrors at the schema level).
        var alreadyAttached = resolvedDatabaseId is { } attachedDatabaseId
            ? await db.AppManagedServices.AnyAsync(x => x.AppId == appId && x.ManagedServiceDatabaseId == attachedDatabaseId, ct)
            : await db.AppManagedServices.AnyAsync(
                x => x.AppId == appId && x.ManagedServiceId == serviceId && x.ManagedServiceDatabaseId == null, ct);

        if (alreadyAttached)
        {
            return (null, [new ServiceRefusal(
                "This database is already attached.", "این دیتابیس از قبل به این اپ متصل است.")]);
        }

        var existingAliases = await db.AppManagedServices
            .Where(x => x.AppId == appId).Select(x => x.Alias).ToListAsync(ct);
        var resolvedAlias = AppManagedServiceAlias.Resolve(alias, service.Name, existingAliases);

        return (new AttachPlan(service, app, resolvedDatabaseId, resolvedAlias), []);
    }
}
