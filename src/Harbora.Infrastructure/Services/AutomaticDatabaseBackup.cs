using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// What became of the daily backup a new database is given, said to the person who made it: set up,
/// and when the first one runs, or why there is none. Never silence — a database with no backup and
/// no word about it reads as one that has one.
/// </summary>
/// <param name="Scheduled">True when the database has a schedule now, this one or one it already had.</param>
/// <param name="ScheduleId">The schedule that covers it, when there is one.</param>
/// <param name="FirstRunAt">When a schedule made here first runs; null for one that already existed.</param>
public sealed record DatabaseBackupSetup(
    bool Scheduled, Guid? ScheduleId, DateTimeOffset? FirstRunAt, string Message, string MessageFa);

/// <summary>
/// Gives a database a person has just created a daily backup keeping seven, or as many as the plan
/// allows when that is fewer, so nobody has to go and set one up — and most never did: a database
/// made by Launch had no backup at all until its owner found the Backups page.
///
/// <para>
/// <b>Always after the database's own creation has committed, never inside it.</b> Nothing here can
/// fail or hold back the creation: every refusal and every exception becomes a
/// <see cref="DatabaseBackupSetup"/> with a sentence, and the schedule takes the workspace's creation
/// lock in a transaction of its own, so a plan check or a save that goes wrong rolls back the schedule
/// and only the schedule.
/// </para>
///
/// <para>
/// <b>The same plan checks the hand-made schedule asks</b> (<c>BackupsController.CreateSchedule</c>):
/// the count of schedules and the retention, under the same lock. A plan with no room gets no
/// schedule and a sentence that says so, never a database refused. A plan that keeps fewer than seven
/// gets a schedule keeping the most it allows, and the sentence says how many and why — the owner's
/// choice (round nine) over no schedule at all, which is what this did first.
/// </para>
///
/// <para>
/// <b>Only where a backup can really run.</b> A schedule that can only ever fail is worse than none:
/// it raises an alert every day about a backup nobody could have taken. So: not for a read replica
/// (its primary's backup holds the same data), not for an engine with no consistent dump of its own
/// (a cache, a broker, a search index — their contents are rebuilt rather than restored, and on a plan
/// that allows two schedules a Redis beside a Postgres would take the second one), and not on a server
/// the backup engine refuses (the older inbound agent, <c>BackupEngine.RequireCapableHost</c>). The
/// host is decided the way the engine decides it — the engine the factory resolves for that server,
/// compared with the factory's own local one, or a node behind it — rather than from the server row
/// a second time.
/// </para>
///
/// <para>
/// <b>The first run waits a full interval.</b> <c>BackupScheduler</c> treats a schedule with no
/// <c>NextRunAt</c> as due on its next five-minute tick, which for a database still pulling its image
/// is a failed backup and an alert about a database nothing is wrong with.
/// </para>
/// </summary>
public sealed class AutomaticDatabaseBackup(
    HarboraDbContext db,
    IQuotaService quota,
    IServerEngineFactory engines,
    IAuditLogger audit,
    ISystemClock clock,
    ILogger<AutomaticDatabaseBackup> logger)
{
    /// <summary>Daily. Proposed to the owner and accepted, with <see cref="RetentionCount"/>.</summary>
    public const int IntervalHours = 24;

    /// <summary>How many backups it keeps, unless the plan allows fewer; then the most it allows.</summary>
    public const int RetentionCount = 7;

    private static readonly string[] EnglishCounts = ["none", "one", "two", "three", "four", "five", "six", "seven"];
    private static readonly string[] PersianCounts = ["هیچ", "یک", "دو", "سه", "چهار", "پنج", "شش", "هفت"];

    /// <summary>Only ever asked whether a plan exists; never run.</summary>
    private static readonly ServiceCreds Probe = new("probe", 0, "probe", "probe", "probe");

    /// <summary>
    /// Sets up the schedule, or says why not. Does not throw: the database it is about already exists
    /// and was paid for, and whatever happens here must not make its creation look as if it failed.
    /// </summary>
    public async Task<DatabaseBackupSetup> ScheduleAsync(Guid serviceId, string? ipAddress, CancellationToken ct)
    {
        var name = serviceId.ToString();
        var added = new List<object>();
        try
        {
            // Unfiltered and narrowed by hand: the request path reads through its own tenant filter
            // anyway, and a caller without a session must not read an empty table and call it done.
            var service = await db.ManagedServices.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == serviceId, ct);
            if (service is null)
                return NotSet(name, "the database could not be found again", "دیتابیس دوباره پیدا نشد");
            name = service.Name;

            if (service.PrimaryManagedServiceId is not null)
                return NotSet(name,
                    "it is a read replica, and its primary's backup already holds the same data",
                    "این یک رپلیکای فقط‌خواندنی است و پشتیبانِ دیتابیسِ اصلی همین داده را دارد");

            if (WhyNotThisEngine(service.Type) is { } engine)
                return NotSet(name, engine.En, engine.Fa);

            if (await WhereBackupsCannotRunAsync(service, ct) is { } server)
                return NotSet(name,
                    $"it runs on {server}, which this panel cannot back up yet. Backups run on the panel's own server and on enrolled nodes",
                    $"روی {server} اجرا می‌شود و این پنل هنوز نمی‌تواند از آنجا پشتیبان بگیرد. پشتیبان‌گیری روی سرور خود پنل و نودهای ثبت‌شده کار می‌کند");

            await using var reservation = await quota.AcquireCreationLockAsync(service.WorkspaceId, ct);

            // A launch that is retried adopts the database an earlier attempt made instead of making it
            // again, and anything else that reaches here twice must not leave two schedules taking the
            // same backup.
            var existing = await db.BackupSchedules.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.WorkspaceId == service.WorkspaceId && s.TargetRef == service.Id.ToString()
                            && (s.Type == BackupType.Database || s.Type == BackupType.Service))
                .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            if (existing is { } existingId)
                return new DatabaseBackupSetup(true, existingId, null,
                    $"{name} already has a backup schedule, so no second one was added.",
                    $"«{name}» از قبل زمان‌بندی پشتیبان دارد، پس زمان‌بندی دومی اضافه نشد.");

            var count = await quota.CanAddGovernedResourcesAsync(service.WorkspaceId,
                new GovernanceQuotaDelta(BackupSchedules: 1), ct);
            if (!count.Allowed)
                return NotSet(name, PlanReason(count, isFa: false), PlanReason(count, isFa: true), roomInPlan: true);

            var (keep, refusal) = await RetentionAsync(service.WorkspaceId, ct);
            if (refusal is not null)
                return NotSet(name, PlanReason(refusal, isFa: false), PlanReason(refusal, isFa: true), roomInPlan: true);

            var destination = await DestinationAsync(service.WorkspaceId, added, ct);
            var firstRunAt = clock.UtcNow.AddHours(IntervalHours);
            var schedule = new BackupSchedule
            {
                WorkspaceId = service.WorkspaceId, DestinationId = destination, Type = BackupType.Database,
                TargetRef = service.Id.ToString(), IntervalHours = IntervalHours, RetentionCount = keep,
                NextRunAt = firstRunAt, IsEnabled = true
            };
            db.BackupSchedules.Add(schedule);
            added.Add(schedule);

            await db.SaveChangesAsync(ct);
            await reservation.CommitAsync(ct);
            added.Clear();

            await AuditAsync(schedule, service, firstRunAt, ipAddress, ct);

            return new DatabaseBackupSetup(true, schedule.Id, firstRunAt,
                Scheduled(name, keep, isFa: false), Scheduled(name, keep, isFa: true));
        }
        catch (Exception ex)
        {
            // Whatever was added must not ride out on the caller's next save — Launch writes its own
            // row with this same context straight after.
            foreach (var entity in added) db.Entry(entity).State = EntityState.Detached;
            logger.LogWarning(ex, "Could not set up the daily backup of database {ServiceId}.", serviceId);
            return NotSet(name, "it could not be saved", "ذخیره نشد");
        }
    }

    /// <summary>
    /// Why this engine is not given one, or null when it is. The engines with a consistent dump of
    /// their own are exactly the ones <see cref="DatabaseDumpPlan.For"/> has a plan for, asked rather
    /// than listed again here.
    /// </summary>
    private static (string En, string Fa)? WhyNotThisEngine(ManagedServiceType type)
    {
        if (DatabaseDumpPlan.For(type, Probe, "/backup/probe") is not null) return null;

        var definition = ServiceCatalog.All[type];
        var (kind, kindFa) = type switch
        {
            ManagedServiceType.Redis => ("a cache", "یک کش"),
            ManagedServiceType.Meilisearch => ("a search index", "یک نمایهٔ جست‌وجو"),
            ManagedServiceType.RabbitMq or ManagedServiceType.Nats => ("a message broker", "یک کارگزار پیام"),
            _ => ("an engine with no dump of its own", "موتوری بدون خروجی‌گیری مخصوص به خود")
        };

        return (
            $"{definition.DisplayName} is {kind}, whose contents are usually rebuilt rather than restored. You can still add a schedule by hand on the Backups page",
            $"{definition.DisplayNameFa} {kindFa} است و محتوایش معمولاً از نو ساخته می‌شود، نه بازیابی. اگر بخواهی، در صفحهٔ «پشتیبان‌ها» می‌توانی دستی زمان‌بندی بسازی");
    }

    /// <summary>
    /// The server's name when a backup of a database there would be refused, or null when one can run.
    /// The same two cases <c>BackupEngine</c> takes: this panel's own engine, or a node's.
    /// </summary>
    private async Task<string?> WhereBackupsCannotRunAsync(ManagedService service, CancellationToken ct)
    {
        try
        {
            var docker = await engines.ResolveAsync(service.ServerId, ct);
            if (ReferenceEquals(docker, engines.Local) || docker is Nodes.NodeWorkloadEngine) return null;
        }
        catch (InvalidOperationException)
        {
            // A revoked node, or a server with nothing behind it. The provision says so on its own; a
            // schedule there could only ever fail.
        }

        var serverName = await db.Servers.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == service.ServerId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(serverName) ? service.ServerId.ToString() : serverName;
    }

    /// <summary>
    /// The workspace's default destination, or its first — the one the database page's own export and
    /// back-up-now use. A workspace that has none gets the same local one the Backups page makes on its
    /// first visit (<c>BackupsController.EnsureDefaultDestinationAsync</c>), so opening that page later
    /// finds it rather than making a second.
    /// </summary>
    private async Task<Guid> DestinationAsync(Guid workspaceId, List<object> added, CancellationToken ct)
    {
        var existing = await db.BackupDestinations.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.WorkspaceId == workspaceId)
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.CreatedAt)
            .Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id) return id;

        var local = new BackupDestination
        {
            WorkspaceId = workspaceId, Name = "Local", Type = BackupDestinationType.Local, IsDefault = true
        };
        db.BackupDestinations.Add(local);
        added.Add(local);
        return local.Id;
    }

    /// <summary>
    /// Its own entry, aimed at the schedule rather than at the database: nobody pressed anything to
    /// make this, and months later the trail is the only thing that says where it came from. After the
    /// commit, and not allowed to turn a schedule that exists into a sentence saying it does not.
    /// </summary>
    private async Task AuditAsync(
        BackupSchedule schedule, ManagedService service, DateTimeOffset firstRunAt, string? ipAddress, CancellationToken ct)
    {
        try
        {
            await audit.LogAsync("backup.schedule_created", "backup_schedule", schedule.Id.ToString(), ipAddress,
                metadataJson: JsonSerializer.Serialize(new
                {
                    automatic = true,
                    service = service.Id,
                    name = service.Name,
                    intervalHours = IntervalHours,
                    retention = schedule.RetentionCount,
                    firstRunAt
                }),
                workspaceId: service.WorkspaceId, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The daily backup of {Name} was set up, but its audit entry was not written.", service.Name);
        }
    }

    /// <summary>
    /// <see cref="RetentionCount"/>, or the most the plan lets one schedule keep when that is fewer.
    /// Asked of the same check the hand-made schedule asks, counting down from seven, rather than read
    /// off the plan a second way that could come to a different answer. The refusal comes back only
    /// when it will not keep even one; it is the one given for one.
    /// </summary>
    private async Task<(int Keep, QuotaCheck? Refusal)> RetentionAsync(Guid workspaceId, CancellationToken ct)
    {
        QuotaCheck? refusal = null;
        for (var keep = RetentionCount; keep >= 1; keep--)
        {
            refusal = await quota.CanUseBackupRetentionAsync(workspaceId, keep, ct);
            if (refusal.Allowed) return (keep, null);
        }
        return (0, refusal);
    }

    /// <summary>
    /// What the person is told about a schedule made here: how many backups it keeps, said as a word
    /// in both languages, and — when the plan is why that is under seven — that it is the most the
    /// plan allows, so a smaller number does not read as a choice somebody made for them at random.
    /// </summary>
    private static string Scheduled(string name, int keep, bool isFa)
    {
        var capped = keep < RetentionCount;
        if (isFa)
        {
            var kept = keep == 1 ? "فقط یک نسخه" : $"{Count(PersianCounts, keep)} نسخهٔ آخر";
            return $"از «{name}» هر روز پشتیبان گرفته می‌شود و {kept} نگه داشته می‌شود" +
                   (capped ? "، یعنی بیشترین تعدادی که پلن اجازه می‌دهد" : "") +
                   $"؛ اولی {IntervalHours} ساعت دیگر گرفته می‌شود. در صفحهٔ «پشتیبان‌ها» می‌توانی تغییرش بدهی یا حذفش کنی.";
        }

        var keptEn = keep == 1 ? "only one backup is kept" : $"the last {Count(EnglishCounts, keep)} backups are kept";
        return $"{name} is backed up every day and {keptEn}" +
               (capped ? ", the most the plan allows" : "") +
               $"; the first runs in {IntervalHours} hours. You can change or remove this on the Backups page.";
    }

    private static string Count(string[] words, int n) => n >= 0 && n < words.Length ? words[n] : n.ToString();

    private static string PlanReason(QuotaCheck check, bool isFa) => isFa
        ? check.ReasonFa ?? check.Reason ?? "پلن جای زمان‌بندی دیگری ندارد"
        : check.Reason ?? "the plan has no room for another schedule";

    private static DatabaseBackupSetup NotSet(string name, string why, string whyFa, bool roomInPlan = false) => new(
        false, null, null,
        $"No daily backup was set up for {name}: {why.TrimEnd('.')}." +
        (roomInPlan ? " Make room or move to a larger plan, then add one on the Backups page." : ""),
        $"برای «{name}» پشتیبان‌گیری روزانه تنظیم نشد: {whyFa.TrimEnd('.')}." +
        (roomInPlan ? " جا باز کن یا پلن بزرگ‌تری بگیر، بعد از صفحهٔ «پشتیبان‌ها» یکی بساز." : ""));
}
