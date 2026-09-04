using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Read replicas for a PostgreSQL instance (3.2, round-2 market-gaps plan) — creation and promotion,
/// on this same <c>/databases/{id}/…</c> route family. Separate file, same controller, the same
/// reasoning <c>DatabasesController.Pitr.cs</c> already gives its own split.
/// </summary>
public sealed partial class DatabasesController
{
    /// <summary>
    /// Creates a read replica of this instance — same server, same environment (and therefore the
    /// same private network the primary already answers on; see <c>ReadReplicaSeedPlan</c>'s own
    /// doc), same PostgreSQL major version, same admin login. Everything about it that could differ
    /// is refused rather than offered, by <see cref="Harbora.Infrastructure.Services.ReadReplicaPlan.WhyRefused"/>
    /// — see that class for why (cross-server replication is not built on this platform at all yet,
    /// the same limit <c>ExternalAccessAvailability</c> already gives a database's outside-access
    /// gateway).
    /// </summary>
    [HttpPost("{id:guid}/replicas/create")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> CreateReplica(Guid id, string name, CancellationToken ct)
    {
        await Guard(id, ct);
        var primary = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (primary is null) return NotFound();

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = IsFa ? "برای رپلیکا یک نام بگذارید." : "Name the replica.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var refusal = Harbora.Infrastructure.Services.ReadReplicaPlan.WhyRefused(primary, primary.ServerId, primary.Version);
        if (refusal is not null)
        {
            TempData["Error"] = refusal;
            return RedirectToAction(nameof(Details), new { id });
        }

        var slug = Slugify(name);
        if (await db.ManagedServices.AnyAsync(s => s.WorkspaceId == WorkspaceId && s.ContainerName == $"harbora-svc-{slug}", ct))
        {
            TempData["Error"] = IsFa ? "سرویسی با این نام از قبل وجود دارد." : "A service with this name already exists.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await using var quotaReservation = await quota.AcquireCreationLockAsync(WorkspaceId, ct);
        var check = await quota.CanAddServiceAsync(WorkspaceId, primary.InstanceSizeKey, ct);
        if (!check.Allowed)
        {
            TempData["Error"] = check.Reason ?? "Plan quota exceeded.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var replica = new ManagedService
        {
            WorkspaceId = WorkspaceId,
            // Same environment as the primary — never offered as a choice — so both containers land
            // on the identical private network ManagedServiceEngine.ProvisionAsync resolves from
            // EnvironmentId, which is what makes the primary's ContainerName resolvable at all.
            EnvironmentId = primary.EnvironmentId,
            ServerId = primary.ServerId,
            Name = name,
            Type = primary.Type,
            // Must match exactly — physical replication does not cross major versions
            // (ReadReplicaPlan.WhyRefused already checked this; restated here as what gets stored).
            Version = primary.Version,
            Status = ServiceStatus.Provisioning,
            ContainerName = $"harbora-svc-{slug}",
            VolumeName = $"harbora-svc-{slug}-data",
            InternalPort = primary.InternalPort,
            // The SAME login, not a new one: a physical replica is a byte-for-byte copy of the
            // primary's own roles table, so there is no separate credential to generate. The
            // ciphertext is copied as-is (never decrypted here) — same plaintext, same protector.
            Username = primary.Username,
            EncryptedPassword = primary.EncryptedPassword,
            DatabaseName = primary.DatabaseName,
            PrimaryManagedServiceId = primary.Id,
            // The replica must run the SAME image the primary does, or its own extension files would
            // not match a data directory that was seeded with pgvector's objects already in it.
            PgVectorEnabled = primary.PgVectorEnabled,
            InstanceSizeKey = primary.InstanceSizeKey,
            MemoryLimitBytes = primary.MemoryLimitBytes,
            DiskLimitBytes = primary.DiskLimitBytes,
            CpuLimit = primary.CpuLimit
        };

        db.ManagedServices.Add(replica);

        // Billed exactly like any other workload — reusing ResourceCreationBilling, the same call
        // every other managed-service create already makes. No new billing path: BillingTick's own
        // per-hour pass reads every row in ManagedServices with no notion of "replica" at all, so this
        // creation-time reservation is the only new call this feature needed.
        try
        {
            await creationBilling.SaveAsync(WorkspaceId,
                [new(Harbora.Domain.Billing.BilledResourceType.Service,
                    replica.Id, replica.Name, replica.InstanceSizeKey, replica.ServerId)], ct);
        }
        catch (Harbora.Infrastructure.Billing.CreationPaymentRequiredException ex)
        {
            db.ChangeTracker.Clear();
            TempData["Error"] = IsFa ? ex.ReasonFa : ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }

        await quotaReservation.CommitAsync(ct);
        await engine.QueueProvisionAsync(replica.Id, ct);

        await audit.LogAsync("database.replica_created", "service", $"{id}:{replica.Id}",
            HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: WorkspaceId, ct: ct);

        TempData["Message"] = IsFa
            ? $"رپلیکای «{replica.Name}» در حال ساخته‌شدن است. تا آماده‌شدن، تأخیر اندازه‌گیری‌نشده خواهد بود."
            : $"Replica '{replica.Name}' is being created. Its lag will read as not-yet-measured until it is ready.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Ends a replica's recovery mode and makes it an ordinary, independent, writable instance —
    /// typed-name confirmed, the same idiom <c>PitrRestore</c>'s overwrite arm and
    /// <c>DatabasesController.Remove</c>'s data-deleting arm already use for an act this irreversible:
    /// once promoted, the instance stops receiving anything from its former primary, and any app
    /// reading its <c>REPLICA_URL</c> is reading a copy that will never catch up again.
    /// </summary>
    [HttpPost("{id:guid}/promote")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> PromoteReplica(Guid id, string? confirmName, CancellationToken ct)
    {
        await Guard(id, ct);
        var replica = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (replica is null) return NotFound();

        if (!string.Equals(confirmName, replica.Name, StringComparison.Ordinal))
        {
            TempData["Error"] = IsFa
                ? $"برای ارتقای این رپلیکا به یک نمونهٔ مستقل و قابل‌نوشتن، نام آن را دقیقاً بنویسید: {replica.Name}"
                : $"To promote this replica to a standalone, writable instance, type its name exactly: {replica.Name}";
            return RedirectToAction(nameof(Details), new { id });
        }

        var (ok, error) = await engine.PromoteReplicaAsync(id, ct);
        if (!ok)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("database.replica_promoted", "service", id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: WorkspaceId, ct: ct);

        TempData["Message"] = IsFa
            ? $"«{replica.Name}» ارتقا یافت و اکنون نمونه‌ای مستقل و قابل‌نوشتن است."
            : $"'{replica.Name}' was promoted and is now a standalone, writable instance.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
