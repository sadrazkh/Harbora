namespace Harbora.Application.Abstractions;

/// <summary>
/// Which tenant the current unit of work belongs to. Drives the DbContext's global query filters,
/// so workspace isolation is a property of the model rather than something every query must
/// remember to add.
///
/// Two distinct situations must not be confused:
/// <list type="bullet">
/// <item><b>A request</b> — scoped to the caller's workspace. An unauthenticated request has no
/// workspace, which resolves to <see cref="Guid.Empty"/> and therefore matches nothing: deny by
/// default rather than leak.</item>
/// <item><b>System work</b> — background jobs, the deploy pipeline, reconcilers, schedulers and
/// startup seeding legitimately operate across every tenant and run <see cref="IsUnscoped"/>.</item>
/// </list>
/// </summary>
public interface IWorkspaceScope
{
    /// <summary>The workspace whose data is visible. Ignored when <see cref="IsUnscoped"/>.</summary>
    Guid WorkspaceId { get; }

    /// <summary>True for system work that must see every tenant's data.</summary>
    bool IsUnscoped { get; }
}

/// <summary>The scope used by background/system work and by tests that don't care about tenancy.</summary>
public sealed class SystemWorkspaceScope : IWorkspaceScope
{
    public static readonly SystemWorkspaceScope Instance = new();
    public Guid WorkspaceId => Guid.Empty;
    public bool IsUnscoped => true;
}

/// <summary>A fixed tenant scope — used by tests and anywhere a workspace is already known.</summary>
public sealed class FixedWorkspaceScope(Guid workspaceId) : IWorkspaceScope
{
    public Guid WorkspaceId { get; } = workspaceId;
    public bool IsUnscoped => false;
}
