using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Common;

/// <summary>
/// Database-backed <c>Idempotency-Key</c> handling, shared by every module's API.
///
/// <para>
/// A table rather than a process-local cache, because the panel can run more than one instance: a
/// retry that lands on a different replica must get the SAME answer, not a second restore or a
/// second sync folder. An in-memory version would look correct on one machine and start duplicating
/// work the moment the deployment scaled.
/// </para>
/// <para>
/// Platform-level rather than per-module. The first version lived in the backup module, which meant
/// the sync module would have had to depend on Backup to reuse it — a dependency between two things
/// that are deliberately unrelated.
/// </para>
/// </summary>
public sealed class IdempotencyStore(HarboraDbContext db, ISystemClock clock) : IIdempotencyStore
{
    public async Task<Guid?> FindAsync(string endpoint, string key, CancellationToken ct)
    {
        var now = clock.UtcNow;

        var existing = await db.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.Endpoint == endpoint && r.Key == key && r.ExpiresAt > now, ct);

        return existing?.ResultId;
    }

    public async Task RememberAsync(
        Guid workspaceId, string endpoint, string key, Guid resultId, CancellationToken ct)
    {
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            WorkspaceId = workspaceId,
            Endpoint = endpoint,
            Key = key,
            ResultId = resultId,
            ExpiresAt = clock.UtcNow.AddDays(1)
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent identical request. The unique index is what makes that
            // safe: the other caller's row stands, and the work itself was already de-duplicated by
            // the service's own guards.
        }
    }
}
