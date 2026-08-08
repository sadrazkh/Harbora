using Harbora.Data;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Tests.Fakes;

/// <summary>
/// Harbora's own database, able to refuse one save on demand — and, if asked, to stop answering
/// anything else afterwards.
///
/// <para>
/// The in-memory provider cannot be made to refuse a write, so a component's behaviour when its own
/// store says no is otherwise untestable. Taking the exception rather than a bool is deliberate:
/// what a caller does about a refusal usually depends on which refusal it was.
/// </para>
///
/// <para>
/// <b>Every save overload is intercepted</b>, not only the async-with-token one today's callers
/// reach for. Overriding just that one is correct until somebody writes <c>SaveChanges()</c>, at
/// which point the test keeps passing while covering nothing, and no assertion changes to say so.
/// </para>
///
/// <para>
/// <see cref="LoseTheConnectionToo"/> is the difference between the two halves of a failed save, and
/// it is a real difference. A constraint violation, a concurrency conflict or a statement timeout
/// leaves the connection healthy, so the next read works and code that recovers by reading again
/// recovers fine. A dropped connection, a failover or an exhausted pool does not, and that is the
/// half where "I will just rebuild the page" throws a second time and destroys whatever the first
/// failure left in hand. Disposal is the blunt way to model it — production raises
/// <c>NpgsqlException</c>, not <c>ObjectDisposedException</c> — but the property under test is only
/// that every later use of this context throws, and the caller must not care which type it is.
/// </para>
/// </summary>
public sealed class BrittleContext(DbContextOptions<HarboraDbContext> options) : HarboraDbContext(options)
{
    /// <summary>Thrown by the next save, once. Null means saves behave normally.</summary>
    public Exception? FailTheNextSaveWith { get; set; }

    /// <summary>When true, the failing save takes the context with it — reads included.</summary>
    public bool LoseTheConnectionToo { get; set; }

    /// <summary>
    /// Runs once, immediately after the next save succeeds — the seam another request would slip
    /// through. It is the only way to model "somebody deleted the row between this request's write
    /// and its next read" in a test that has exactly one thread.
    /// </summary>
    public Action? AfterTheNextSave { get; set; }

    public override int SaveChanges()
    {
        Refuse();
        var saved = base.SaveChanges();
        Interleave();
        return saved;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Refuse();
        var saved = base.SaveChanges(acceptAllChangesOnSuccess);
        Interleave();
        return saved;
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Refuse();
        var saved = await base.SaveChangesAsync(cancellationToken);
        Interleave();
        return saved;
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Refuse();
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        Interleave();
        return saved;
    }

    private void Refuse()
    {
        if (FailTheNextSaveWith is not { } failure) return;

        FailTheNextSaveWith = null;
        if (LoseTheConnectionToo) Dispose();

        throw failure;
    }

    /// <summary>Cleared before it runs, so the overloads delegating to each other cannot double-fire it.</summary>
    private void Interleave()
    {
        if (AfterTheNextSave is not { } interruption) return;

        AfterTheNextSave = null;
        interruption();
    }
}
